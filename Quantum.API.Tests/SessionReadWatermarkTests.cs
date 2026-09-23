using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.WebSockets;
using System.Text;
using Quantum.Application;
using Quantum.Utils;
using Xunit;
using Quantum.Entities.Model;
using Quantum.Data;

namespace Quantum.API.Tests;

/// <summary>
/// 会话已读水位双端同步（2026-09-21 批次）：t_chat_session.LastReadSeq 服务端权威化——
/// 合并钳制（只进 + 钳到 LastSeq + 删除会话随行清水位/重建预置）、sessions/read 只在实际推进时广播 session_read 帧、
/// overview 合并落库并响应 ReadSeq、逐会话 Status 推进不跨会话。
/// </summary>
public class SessionReadWatermarkTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly AppMessageService _service;

    public SessionReadWatermarkTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new AppMessageService(_db, NullLogger<AppMessageService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<ChatMessageModel> Append(ChatMessageDirection direction, string content, string sessionKey = null,
        ChatMessageStatus status = ChatMessageStatus.发送中) =>
        _service.AppendAsync(direction, content, "text", status, sessionKey: sessionKey);

    [Fact]
    public async Task MarkSessionsReadAsync_OnlyAdvances_NeverRegresses()
    {
        await Append(ChatMessageDirection.发送, "m1", "task-a"); // seq1
        await Append(ChatMessageDirection.发送, "m2", "task-a"); // seq2

        var advanced = await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 2 });

        Assert.Equal([("task-a", 2L)], advanced);
        // 更低的上报（旧设备 stale）：不推进、无广播条目
        var again = await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 1 });
        Assert.Empty(again);
        Assert.Equal(2, (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-a")).LastReadSeq);
    }

    [Fact]
    public async Task MarkSessionsReadAsync_ClampsToLastSeq()
    {
        await Append(ChatMessageDirection.发送, "m1", "task-a"); // seq1

        // 异常高水位（stale 高报）被钳到 LastSeq，不越界落库
        var advanced = await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 999 });

        Assert.Equal([("task-a", 1L)], advanced);
        Assert.Equal(1, (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-a")).LastReadSeq);
    }

    [Fact]
    public async Task MarkSessionsReadAsync_SkipsUnknownSessions_AndNormalizesKeys()
    {
        await Append(ChatMessageDirection.发送, "d1"); // 默认会话 seq1

        // 不存在的会话键跳过（不建行——空会话水位无意义）；空白键归一为默认会话
        var advanced = await _service.MarkSessionsReadAsync(new Dictionary<string, long>
        {
            ["no-such"] = 5,
            ["  "] = 1
        });

        Assert.Equal([("", 1L)], advanced);
        Assert.False(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "no-such"));
        Assert.Equal(1, (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "")).LastReadSeq);
    }

    [Fact]
    public async Task MarkSessionsReadAsync_PromotesStatusOnlyWithinThatSession()
    {
        await Append(ChatMessageDirection.发送, "a1", "task-a"); // seq1
        await Append(ChatMessageDirection.发送, "d1");            // seq2 默认会话（消息表 SessionKey=null）
        await Append(ChatMessageDirection.发送, "a2", "task-a"); // seq3

        var advanced = await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 3 });

        Assert.Equal([("task-a", 3L)], advanced);
        var a1 = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "a1");
        var a2 = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "a2");
        Assert.Equal(ChatMessageStatus.已读, a1.Status);
        Assert.Equal(ChatMessageStatus.已读, a2.Status);
        // 其他会话（含默认会话 null 键）不被跨会话误标——旧全局 MarkReadAsync 的缺陷在本端点不出现
        var d1 = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "d1");
        Assert.Equal(ChatMessageStatus.发送中, d1.Status);

        // 默认会话上报：null 键行被正确推进（谓词显式含 null）
        await _service.MarkSessionsReadAsync(new Dictionary<string, long> { [""] = 2 });
        Assert.Equal(ChatMessageStatus.已读, (await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "d1")).Status);
    }

    [Fact]
    public async Task MarkSessionsReadAsync_OverEntryLimit_Throws()
    {
        var tooMany = Enumerable.Range(0, AppMessageService.MaxReadWatermarkEntries + 1)
            .ToDictionary(n => $"s{n}", n => (long)n);
        await Assert.ThrowsAsync<BusinessException>(() => _service.MarkSessionsReadAsync(tooMany));
    }

    [Fact]
    public async Task MarkSessionsReadAsync_StatusAlreadyRead_NoReAdvanceNoFrame()
    {
        // 水位已到位后重复上报：无推进 → 广播列表为空（两台设备同屏读同一会话，后到者不产生帧）
        await Append(ChatMessageDirection.发送, "m1", "task-a"); // seq1
        await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 1 });

        var again = await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 1 });

        Assert.Empty(again);
    }

    [Fact]
    public async Task SessionsAsync_MergesWatermarkIntoDb_AndRespondsReadSeq()
    {
        await Append(ChatMessageDirection.发送, "m1", "task-a"); // seq1
        await Append(ChatMessageDirection.发送, "m2", "task-a"); // seq2

        // Web 端 overview 上报本地水位：服务端吸收落库（只进），响应条目带权威 ReadSeq
        var (sessions, _) = await _service.SessionsAsync(new Dictionary<string, long> { ["task-a"] = 1 });
        Assert.Equal(1, sessions.Single(n => n.SessionKey == "task-a").ReadSeq);
        Assert.Equal(1, sessions.Single(n => n.SessionKey == "task-a").Unread); // seq2 > 水位 1
        Assert.Equal(1, (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-a")).LastReadSeq);

        // 再报满：水位钳到 LastSeq=2，未读归零
        (sessions, _) = await _service.SessionsAsync(new Dictionary<string, long> { ["task-a"] = 2 });
        Assert.Equal(2, sessions.Single(n => n.SessionKey == "task-a").ReadSeq);
        Assert.Equal(0, sessions.Single(n => n.SessionKey == "task-a").Unread);
        Assert.Equal(2, (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-a")).LastReadSeq);
    }

    [Fact]
    public async Task SessionsAsync_UnreadUsesAuthoritativeMergedWatermark()
    {
        // 跨端同步核心语义：另一端已把服务端水位推到 2，本端本地水位仍是 0——
        // overview 未读必须按合并后的权威水位（0 条）计算，而非调用方本地水位（2 条）
        await Append(ChatMessageDirection.发送, "m1", "task-a"); // seq1
        await Append(ChatMessageDirection.发送, "m2", "task-a"); // seq2
        await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 2 });

        var (sessions, _) = await _service.SessionsAsync(new Dictionary<string, long>());

        var entry = sessions.Single(n => n.SessionKey == "task-a");
        Assert.Equal(0, entry.Unread);
        Assert.Equal(2, entry.ReadSeq); // 本端吸收响应 ReadSeq 即完成追赶
    }

    [Fact]
    public async Task SessionsAsync_NoAdvancement_DoesNotWrite()
    {
        await Append(ChatMessageDirection.发送, "m1", "task-a"); // seq1

        // 无上报（null）：effective = 服务端现值，无推进零写——两次调用行为一致
        var (first, _) = await _service.SessionsAsync(null);
        var (second, _) = await _service.SessionsAsync(null);
        Assert.Equal(first.Single(n => n.SessionKey == "task-a").ReadSeq, second.Single(n => n.SessionKey == "task-a").ReadSeq);
        Assert.Equal(0, (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-a")).LastReadSeq);
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesWatermarkWithRow()
    {
        // 2026-09-21 日志删除逻辑调整：删除会话 = 删会话行（水位随行消失），消息行保留。
        // 水位信息无处残留——stale 高水位上报因行不存在而无效果（不建行、无推进）
        await Append(ChatMessageDirection.发送, "m1", "task-a"); // seq1
        await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 1 });

        var (deleted, _) = await _service.DeleteSessionAsync("task-a");
        Assert.True(deleted);
        Assert.False(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "task-a"));

        // 行不存在：stale 高报无效果，也不会凭空建行
        var advanced = await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 5 });
        Assert.Empty(advanced);
        Assert.False(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "task-a"));
    }

    [Fact]
    public async Task DeleteSession_RebuildPreservesHistoryWithoutUnreadFlood()
    {
        // 端到端（2026-09-21 语义）：删除会话后 stale 高报无效 → 任务再推送重建会话行，
        // 预置水位让保留的历史不计未读；异常高水位仍被钳到 LastSeq 不越界
        await Append(ChatMessageDirection.发送, "m1", "task-a"); // seq1
        await Append(ChatMessageDirection.发送, "m2", "task-a"); // seq2
        await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 2 });
        await _service.DeleteSessionAsync("task-a");

        // 删除后空窗期 stale 高报：行不存在 → 无推进
        var advanced = await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 2 });
        Assert.Empty(advanced);

        // 重建：新消息落库建行，预置水位 = 保留历史最大 Seq（2）→ 未读仅新消息
        await Append(ChatMessageDirection.发送, "m3", "task-a"); // seq3
        var (sessions, _) = await _service.SessionsAsync(null);
        var entry = sessions.Single(n => n.SessionKey == "task-a");
        Assert.Equal(3, entry.Total);
        Assert.Equal(1, entry.Unread);
        Assert.Equal(2, entry.ReadSeq);

        // 异常高水位照旧被钳到 LastSeq，不越界落库
        await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-a"] = 999 });
        Assert.Equal(3, (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-a")).LastReadSeq);
    }

    [Fact]
    public async Task SessionsAsync_OverEntryLimit_Throws()
    {
        // overview 与 read 共用同一防护（第二轮审核问题 10）
        var tooMany = Enumerable.Range(0, AppMessageService.MaxReadWatermarkEntries + 1)
            .ToDictionary(n => $"s{n}", n => (long)n);
        await Assert.ThrowsAsync<BusinessException>(() => _service.SessionsAsync(tooMany));
    }

    [Fact]
    public async Task RenameSession_MergeBranch_TakesMaxReadWatermark()
    {
        // 会话改名并入既有会话（P2 合并）：源会话已读进度带入，不能重现未读
        await Append(ChatMessageDirection.发送, "m1", "task-old"); // seq1
        await _service.MarkSessionsReadAsync(new Dictionary<string, long> { ["task-old"] = 1 });
        await Append(ChatMessageDirection.发送, "m2", "task-new"); // seq2

        await _service.RenameSession("task-old", "task-new");

        var merged = await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-new");
        Assert.Equal(2, merged.LastSeq);
        Assert.Equal(1, merged.LastReadSeq); // 取大：源会话已读到 1，合并后 seq1 仍算已读
        Assert.False(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "task-old"));
    }
}

/// <summary>
/// 会话已读水位广播（session_read 帧）：实际推进才发帧、默认会话省略 session 字段、离线（无设备）不抛。
/// </summary>
public class SessionReadBroadcastTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly AppMessageService _messageService;
    private readonly AppWebSocketManager _wsManager;
    private readonly AppPushService _pushService;

    public SessionReadBroadcastTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _messageService = new AppMessageService(_db, NullLogger<AppMessageService>.Instance);
        _wsManager = new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance);
        _pushService = new AppPushService(_db, _messageService, _wsManager, NullLogger<AppPushService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task BroadcastSessionReadAsync_AdvancedSessions_EmitFrames()
    {
        var socket = new FakeReadSocket();
        _wsManager.Add(socket);
        await _messageService.AppendAsync(ChatMessageDirection.发送, "m1", "text", sessionKey: "task-a");
        await _messageService.AppendAsync(ChatMessageDirection.发送, "m2", "text");

        var advanced = await _messageService.MarkSessionsReadAsync(new Dictionary<string, long>
        {
            ["task-a"] = 1,
            [""] = 1
        });
        await _pushService.BroadcastSessionReadAsync(advanced);

        Assert.Contains("\"type\":\"session_read\"", socket.SentText.Replace(" ", ""));
        Assert.Contains("\"session\":\"task-a\"", socket.SentText);
        Assert.Contains("\"seq\":1", socket.SentText);
        // 默认会话：session 字段省略（与 message 帧同风格，客户端按缺省归默认会话）
        Assert.Contains("session_read", socket.SentText.Replace(" ", ""));
        var frames = socket.SentFrames();
        Assert.Equal(2, frames.Count);
        Assert.True(frames.All(n => n.Replace(" ", "").Contains("\"type\":\"session_read\"")));
        Assert.Single(frames, n => n.Replace(" ", "").Contains("\"session\":\"task-a\""));
        Assert.Single(frames, n => !n.Replace(" ", "").Contains("\"session\""));
    }

    [Fact]
    public async Task BroadcastSessionReadAsync_EmptyList_NoFrame()
    {
        var socket = new FakeReadSocket();
        _wsManager.Add(socket);

        await _pushService.BroadcastSessionReadAsync([]);

        Assert.Empty(socket.SentFrames());
    }

    [Fact]
    public async Task BroadcastSessionReadAsync_NoOnlineDevice_DoesNotThrow()
    {
        await _pushService.BroadcastSessionReadAsync([("task-a", 3L)]);
    }

    /// <summary>内存型 WebSocket（与 AppPushServiceTests 中的假件同构），逐帧记录发送内容。</summary>
    private sealed class FakeReadSocket : WebSocket
    {
        private readonly List<string> _frames = [];

        public string SentText => string.Join("", _frames);

        public List<string> SentFrames() => [.. _frames];

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("FakeReadSocket 不产生入站帧");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            _frames.Add(Encoding.UTF8.GetString(buffer.Array!.Skip(buffer.Offset).Take(buffer.Count).ToArray()));
            return Task.CompletedTask;
        }
    }
}
