using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 会话视图（Web 管理端会话列表 / 单会话分页）：
/// 每会话最后一条 + 总数 + 未读计数（按调用方水位）、倒序分页边界、默认会话与任务会话隔离。
/// </summary>
public class AppSessionViewTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly AppMessageService _service;

    public AppSessionViewTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new AppMessageService(_db, NullLogger<AppMessageService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<ChatMessageModel> Bot(string content, string sessionKey = null, string contentType = "text")
    {
        return _service.AppendAsync(ChatMessageDirection.发送, content, contentType,
            ChatMessageStatus.发送中, null, null, sessionKey);
    }

    private Task<ChatMessageModel> User(string content, string sessionKey = null)
    {
        return _service.AppendAsync(ChatMessageDirection.接收, content, "text",
            ChatMessageStatus.已读, null, null, sessionKey);
    }

    [Fact]
    public async Task SessionsAsync_ReturnsLastMessageAndTotalPerSession()
    {
        await Bot("默认会话第一条");
        await Bot("任务A第一条", "task-a");
        await Bot("任务A第二条", "task-a");

        var (sessions, maxSeq) = await _service.SessionsAsync(null);

        Assert.Equal(3, maxSeq);
        Assert.Equal(2, sessions.Count);
        var defaultSession = sessions.Single(n => n.SessionKey == "");
        Assert.Equal(1, defaultSession.Total);
        Assert.Equal("默认会话第一条", defaultSession.Last.Content);
        var taskA = sessions.Single(n => n.SessionKey == "task-a");
        Assert.Equal(2, taskA.Total);
        Assert.Equal("任务A第二条", taskA.Last.Content);
    }

    [Fact]
    public async Task SessionsAsync_UnreadFollowsPerSessionWatermark_AndIgnoresUserMessages()
    {
        await Bot("默认1");                    // seq1
        await Bot("默认2");                    // seq2
        await Bot("任务A1", "task-a");          // seq3
        await User("用户发的", "task-a");        // seq4：用户消息不计未读

        var (sessions, _) = await _service.SessionsAsync(new Dictionary<string, long>
        {
            [""] = 1,
            ["task-a"] = 3
        });

        Assert.Equal(1, sessions.Single(n => n.SessionKey == "").Unread);       // seq2 在默认会话水位之后
        Assert.Equal(0, sessions.Single(n => n.SessionKey == "task-a").Unread); // seq3 已在水位内，seq4 非机器人下发
    }

    [Fact]
    public async Task SessionsAsync_MissingWatermark_CountsAllBotMessagesAsUnread()
    {
        await Bot("默认1");
        await Bot("任务A1", "task-a");

        var (sessions, _) = await _service.SessionsAsync(new Dictionary<string, long>());

        Assert.Equal(1, sessions.Single(n => n.SessionKey == "").Unread);
        Assert.Equal(1, sessions.Single(n => n.SessionKey == "task-a").Unread);
    }

    [Fact]
    public async Task SessionsAsync_UnreadIgnoresGlobalReadStatus()
    {
        // 未读以调用方水位为准，不看 Status：MarkReadAsync 是按**全局 Seq** 推进的，
        // 多会话交错时会把别的会话一并标已读——用它算会话未读会串会话。
        await Bot("默认1");
        await Bot("任务A1", "task-a");

        await _service.MarkReadAsync(long.MaxValue);
        var (sessions, _) = await _service.SessionsAsync(new Dictionary<string, long>());

        Assert.Equal(0, await _service.UnreadCountAsync());                            // 全局未读（按 Status）
        Assert.Equal(1, sessions.Single(n => n.SessionKey == "").Unread);              // 会话未读（按水位）
        Assert.Equal(1, sessions.Single(n => n.SessionKey == "task-a").Unread);
    }

    [Fact]
    public async Task SessionsAsync_OrdersByLastSeqDesc()
    {
        await Bot("任务A最早", "task-a");
        await Bot("默认会话居中");
        await Bot("任务B最新", "task-b");

        var (sessions, _) = await _service.SessionsAsync(null);

        Assert.Equal(new[] { "task-b", "", "task-a" }, sessions.Select(n => n.SessionKey));
    }

    [Fact]
    public async Task SessionsAsync_EmptyTable_ReturnsEmptyWithZeroCursor()
    {
        var (sessions, maxSeq) = await _service.SessionsAsync(null);

        Assert.Empty(sessions);
        Assert.Equal(0, maxSeq);
    }

    [Fact]
    public async Task SessionPageAsync_ReturnsLatestPageAscending()
    {
        for (var i = 1; i <= 5; i++)
        {
            await Bot($"m{i}");
        }

        var (messages, hasMore) = await _service.SessionPageAsync(null, 0, 3);

        Assert.True(hasMore);
        Assert.Equal(new[] { "m3", "m4", "m5" }, messages.Select(n => n.Content)); // 取最近 3 条并按 Seq 升序返回
    }

    [Fact]
    public async Task SessionPageAsync_BeforeSeqLoadsEarlierPage()
    {
        for (var i = 1; i <= 5; i++)
        {
            await Bot($"m{i}");
        }

        // 更早还有 m1（本页只取 2 条），因此 hasMore 为真
        var (messages, hasMore) = await _service.SessionPageAsync(null, 4, 2);
        Assert.True(hasMore);
        Assert.Equal(new[] { "m2", "m3" }, messages.Select(n => n.Content));

        // 取到 m1 后不再有更早页
        var (firstPage, firstHasMore) = await _service.SessionPageAsync(null, 3, 2);
        Assert.False(firstHasMore);
        Assert.Equal(new[] { "m1", "m2" }, firstPage.Select(n => n.Content));
    }

    [Fact]
    public async Task SessionPageAsync_IsolatesSessionsAndTreatsBlankKeyAsDefault()
    {
        await Bot("默认1");
        await Bot("任务A1", "task-a");
        await Bot("默认2");

        var (defaultMessages, _) = await _service.SessionPageAsync(null, 0, 50);
        var (taskMessages, _) = await _service.SessionPageAsync("task-a", 0, 50);
        var (blankKeyMessages, _) = await _service.SessionPageAsync("   ", 0, 50);

        Assert.Equal(new[] { "默认1", "默认2" }, defaultMessages.Select(n => n.Content));
        Assert.Equal(new[] { "任务A1" }, taskMessages.Select(n => n.Content));
        Assert.Equal(new[] { "默认1", "默认2" }, blankKeyMessages.Select(n => n.Content)); // 空白键归一为默认会话
    }

    [Fact]
    public async Task SessionPageAsync_ClampsLimit()
    {
        for (var i = 1; i <= 60; i++)
        {
            await Bot($"m{i}");
        }

        // limit ≤ 0 回落默认 50；超上限收敛到 200（此处库内只有 60 条，按 60 返回且无更早页）
        var (defaulted, defaultedHasMore) = await _service.SessionPageAsync(null, 0, 0);
        var (overLimit, overLimitHasMore) = await _service.SessionPageAsync(null, 0, 999);

        Assert.Equal(50, defaulted.Count);
        Assert.True(defaultedHasMore);
        Assert.Equal(60, overLimit.Count);
        Assert.False(overLimitHasMore);
    }

    [Fact]
    public async Task SessionPageAsync_NoMessages_ReturnsEmptyPage()
    {
        var (messages, hasMore) = await _service.SessionPageAsync("task-none", 0, 50);

        Assert.Empty(messages);
        Assert.False(hasMore);
    }
}
