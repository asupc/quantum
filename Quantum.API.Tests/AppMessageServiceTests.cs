using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Xunit;
using Quantum.Entities.Model;
using Quantum.Data;

namespace Quantum.API.Tests;

/// <summary>
/// App 会话消息服务：Seq 全局单调递增、增量同步与送达推进、已读回执/未读数、历史检索、msgId 复用。
/// </summary>
public class AppMessageServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly AppMessageService _service;

    public AppMessageServiceTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new AppMessageService(_db, NullLogger<AppMessageService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task AppendAsync_SeqIncrementsGlobally()
    {
        var m1 = await _service.AppendAsync(ChatMessageDirection.发送, "第一条", "text");
        var m2 = await _service.AppendAsync(ChatMessageDirection.发送, "第二条", "text");
        var b1 = await _service.AppendAsync(ChatMessageDirection.接收, "用户消息", "text", ChatMessageStatus.已读);

        Assert.Equal(1, m1.Seq);
        Assert.Equal(2, m2.Seq);
        Assert.Equal(3, b1.Seq); // 全局游标：不同方向连续递增
        Assert.Equal(ChatMessageDirection.发送, m1.Direction);
        Assert.Equal(ChatMessageStatus.发送中, m1.Status);
        Assert.False(string.IsNullOrEmpty(m1.MsgId));
    }

    [Fact]
    public async Task AppendAsync_ExplicitMsgId_IsReused()
    {
        // 通知镜像进会话流：与 t_app_notification 共用 msgId 保证客户端幂等
        var message = await _service.AppendAsync(ChatMessageDirection.发送, "【标题】\n内容", "text", ChatMessageStatus.发送中, "shared-msg-id");

        Assert.Equal("shared-msg-id", message.MsgId);
    }

    [Fact]
    public async Task SyncAsync_ReturnsMessagesAfterSeq_AndMarksBotMessagesDelivered()
    {
        await _service.AppendAsync(ChatMessageDirection.发送, "m1");
        await _service.AppendAsync(ChatMessageDirection.接收, "user say", "text", ChatMessageStatus.已读);
        await _service.AppendAsync(ChatMessageDirection.发送, "m2");

        var (messages, maxSeq) = await _service.SyncAsync(1);

        Assert.Equal(2, messages.Count); // 增量同步不区分方向，全量返回
        Assert.Equal(3, maxSeq);
        Assert.Equal("m2", messages[1].Content);
        // App 主动拉取即视为送达：机器人下发且「发送中」的消息推进为已送达（ExecuteUpdate 不经变更跟踪，需 NoTracking 重查）
        var stored = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "m2");
        Assert.Equal(ChatMessageStatus.已送达, stored.Status);
    }

    [Fact]
    public async Task SyncAsync_NoNewMessages_KeepsCursorAtAfterSeq()
    {
        await _service.AppendAsync(ChatMessageDirection.发送, "m1");

        var (messages, maxSeq) = await _service.SyncAsync(1);

        Assert.Empty(messages);
        Assert.Equal(1, maxSeq);
    }

    [Fact]
    public async Task SyncAsync_EmptyPageBeforeServerMax_DoesNotAdvanceCursor()
    {
        // 空页游标不变式：游标只随实取到的消息推进。曾用 GetMaxSeqAsync() 兜底，
        // 它与分页查询不在同一快照内——两次查询之间落库的消息会被游标越过，此后永久拉不回。
        await _service.AppendAsync(ChatMessageDirection.发送, "m1");
        await _service.AppendAsync(ChatMessageDirection.发送, "m2");

        var (messages, maxSeq) = await _service.SyncAsync(5);

        Assert.Empty(messages);
        Assert.Equal(5, maxSeq); // 不是 2（服务端 MAX）：空页绝不把游标往回或往前挪
    }

    [Fact]
    public async Task MarkDeliveredAsync_OnlyPromotesPendingMessages()
    {
        var message = await _service.AppendAsync(ChatMessageDirection.发送, "hello");

        Assert.True(await _service.MarkDeliveredAsync(message.MsgId));
        // 幂等：已送达后再次 ack 不再推进（也无害）
        Assert.False(await _service.MarkDeliveredAsync(message.MsgId));
        Assert.Equal(ChatMessageStatus.已送达, (await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Id == message.Id)).Status);
        Assert.False(await _service.MarkDeliveredAsync("not-exists"));
    }

    [Fact]
    public async Task MarkReadAsync_PushesReadStateUpToSeq_AndReturnsUnread()
    {
        await _service.AppendAsync(ChatMessageDirection.发送, "m1");
        await _service.AppendAsync(ChatMessageDirection.发送, "m2");
        await _service.AppendAsync(ChatMessageDirection.接收, "user say", "text", ChatMessageStatus.已读);

        var unreadAfterRead = await _service.MarkReadAsync(2);

        Assert.Equal(0, unreadAfterRead);
        Assert.Equal(0, await _service.UnreadCountAsync());

        await _service.AppendAsync(ChatMessageDirection.发送, "m3");
        Assert.Equal(1, await _service.UnreadCountAsync());
    }

    [Fact]
    public async Task HistoryAsync_FiltersByKeyword_AndPaginatesBySeqDesc()
    {
        await _service.AppendAsync(ChatMessageDirection.发送, "早餐吃什么");
        await _service.AppendAsync(ChatMessageDirection.接收, "运行任务A", "text", ChatMessageStatus.已读);
        await _service.AppendAsync(ChatMessageDirection.发送, "任务A已完成");

        var hit = await _service.HistoryAsync("任务A", 1, 20);
        Assert.Equal(2, hit.Count);
        Assert.Equal("任务A已完成", hit[0].Content); // Seq 倒序

        var page = await _service.HistoryAsync(null, 1, 2);
        Assert.Equal(2, page.Count);
        Assert.Equal(3, page[0].Seq);
    }
    [Fact]
    public async Task DeleteSessionAsync_RemovesOnlySessionRow_KeepsAllMessageRows()
    {
        // 2026-09-21 日志删除逻辑调整：会话删除只删会话数据（t_chat_session 行），
        // 消息行（业务真实产生的记录）全部保留——删除入口收敛到日志中心
        await _service.AppendAsync(ChatMessageDirection.发送, "默认1");
        await _service.AppendAsync(ChatMessageDirection.发送, "任务A消息", "text", sessionKey: "task-a");
        await _service.AppendAsync(ChatMessageDirection.发送, "任务B消息", "text", sessionKey: "task-b");

        var (deleted, maxSeq) = await _service.DeleteSessionAsync("task-a");

        Assert.True(deleted);
        // 消息一行不少（含被删会话的行）；MaxSeq 恒为当前全局最大（消息不删 → Seq 号段不复用）
        Assert.Equal(3, await _db.ChatMessages.AsNoTracking().CountAsync());
        Assert.NotNull(await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.SessionKey == "task-a"));
        Assert.Equal(3, maxSeq);
        Assert.False(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "task-a"));
        // 其他会话行不受影响
        Assert.True(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "task-b"));
    }

    [Fact]
    public async Task DeleteSessionAsync_EmptyKeyDeletesDefaultSessionRow_KeepsNullKeyRows()
    {
        // 默认会话（会话行键空串 / 消息行 SessionKey null）同语义：删行、留消息
        await _service.AppendAsync(ChatMessageDirection.发送, "默认1");
        await _service.AppendAsync(ChatMessageDirection.发送, "任务A消息", "text", sessionKey: "task-a");

        var (deleted, _) = await _service.DeleteSessionAsync(null);

        Assert.True(deleted);
        Assert.Equal(2, await _db.ChatMessages.AsNoTracking().CountAsync());
        Assert.Null(await _db.ChatSessions.AsNoTracking().SingleOrDefaultAsync(n => n.SessionKey == ""));
    }

    // ==================================================================== 会话生命周期（2026-09-18 会话分组归并批次）

    [Fact]
    public async Task AppendAsync_UpsertsSessionRow_AndAdvancesLastSeq()
    {
        await _service.AppendAsync(ChatMessageDirection.发送, "a1", "text", sessionKey: "task-a");
        await _service.AppendAsync(ChatMessageDirection.发送, "a2", "text", sessionKey: "task-a");
        await _service.AppendAsync(ChatMessageDirection.发送, "d1", "text"); // 默认会话（归一空串建行）

        var rows = await _db.ChatSessions.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Single(n => n.SessionKey == "task-a").LastSeq);
        var defaultRow = rows.Single(n => n.SessionKey == "");
        Assert.Equal(3, defaultRow.LastSeq);
        Assert.True(defaultRow.CreateTime >= rows.Single(n => n.SessionKey == "task-a").CreateTime);
    }

    [Fact]
    public async Task AppendAsync_SeqConflictRetry_DoesNotDuplicateSessionRow()
    {
        // 模拟 Seq 撞车重试：首次 SaveChanges 抛 DbUpdateException，重试成功——
        // 会话行（与消息同批 upsert）必须随消息一并脱离跟踪，否则重试会重复插入
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuantumSqliteDbContext>().UseSqlite(connection).Options;
        var db = new FlakyDbContext(options);
        db.Database.EnsureCreated();
        var service = new AppMessageService(db, NullLogger<AppMessageService>.Instance);
        try
        {
            var message = await service.AppendAsync(ChatMessageDirection.发送, "撞车消息", "text", sessionKey: "task-a");

            Assert.False(db.ThrowOnce); // 确认走过失败→重试路径
            Assert.Equal(1, message.Seq);
            Assert.Equal(1, await db.ChatMessages.AsNoTracking().CountAsync());
            Assert.Equal(1, await db.ChatSessions.AsNoTracking().CountAsync());
            Assert.Equal(1, (await db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-a")).LastSeq);
        }
        finally
        {
            db.Dispose();
            connection.Dispose();
        }
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovedSession_DisappearsFromOverview()
    {
        // 删除后的会话不再出现在 sessions/overview（会话表为主的主从结构），消息行仍在库
        await _service.AppendAsync(ChatMessageDirection.发送, "a1", "text", sessionKey: "task-a");
        await _service.AppendAsync(ChatMessageDirection.发送, "d1", "text");
        await _service.DeleteSessionAsync("task-a");

        var (sessions, maxSeq) = await _service.SessionsAsync(null);

        Assert.Equal([""], sessions.Select(n => n.SessionKey));
        Assert.Equal(2, maxSeq); // 消息未删，全局最大 Seq 原样保留
    }

    [Fact]
    public async Task SessionsAsync_EmptySessionRows_SortLastByCreateTimeDesc()
    {
        // LastSeq=0 的空会话行（从未有消息/仅建行）垫底，按 CreateTime 降序
        _db.ChatSessions.Add(new ChatSessionModel { SessionKey = "旧空会话", CreateTime = DateTime.Now.AddMinutes(-5), LastSeq = 0 });
        _db.ChatSessions.Add(new ChatSessionModel { SessionKey = "新空会话", CreateTime = DateTime.Now, LastSeq = 0 });
        await _db.SaveChangesAsync();
        await _service.AppendAsync(ChatMessageDirection.发送, "live", "text", sessionKey: "live");

        var (sessions, _) = await _service.SessionsAsync(null);

        Assert.Equal(["live", "新空会话", "旧空会话"], sessions.Select(n => n.SessionKey));
        Assert.Equal(0, sessions.Single(n => n.SessionKey == "新空会话").Total);
        Assert.Null(sessions.Single(n => n.SessionKey == "新空会话").Last);
    }

    [Fact]
    public async Task DeleteSessionAsync_IdempotentForMissingSession_MaxSeqAlwaysCurrent()
    {
        await _service.AppendAsync(ChatMessageDirection.发送, "a1", "text", sessionKey: "task-a"); // seq1
        await _service.AppendAsync(ChatMessageDirection.发送, "d1", "text");                        // seq2
        await _service.AppendAsync(ChatMessageDirection.发送, "b1", "text", sessionKey: "task-b"); // seq3

        // 消息不再删除：MaxSeq 恒为当前全局最大（客户端回拨 min(当前, MaxSeq) 退化为 no-op，兼容保留）
        var (deleted, maxSeq) = await _service.DeleteSessionAsync("task-a");
        Assert.True(deleted);
        Assert.Equal(3, maxSeq);

        // 删除含全局最大 Seq 的会话：MaxSeq 仍是 3（消息保留，不存在号段复用）
        (deleted, maxSeq) = await _service.DeleteSessionAsync("task-b");
        Assert.True(deleted);
        Assert.Equal(3, maxSeq);

        // 不存在的会话：幂等（无行可删返回 false，不报错）
        (deleted, maxSeq) = await _service.DeleteSessionAsync("no-such-session");
        Assert.False(deleted);
        Assert.Equal(3, maxSeq);
    }

    [Fact]
    public async Task AppendAsync_AfterSessionDeleted_RebuildsRowWithPresetWatermark()
    {
        // 删除会话 → 任务再推送：会话行自动重建，且 LastReadSeq 预置为该会话现有消息最大 Seq——
        // 保留下来的历史消息不整会话计未读（否则 LastReadSeq=0 + LastSeq=新消息 Seq → 全历史未读）
        await _service.AppendAsync(ChatMessageDirection.发送, "a1", "text", sessionKey: "task-a"); // seq1
        await _service.AppendAsync(ChatMessageDirection.发送, "a2", "text", sessionKey: "task-a"); // seq2
        await _service.DeleteSessionAsync("task-a");

        await _service.AppendAsync(ChatMessageDirection.发送, "a3", "text", sessionKey: "task-a"); // seq3 重建

        var row = await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-a");
        Assert.Equal(3, row.LastSeq);
        Assert.Equal(2, row.LastReadSeq); // 预置 = 重建前该会话消息最大 Seq
        var (sessions, _) = await _service.SessionsAsync(null);
        var entry = sessions.Single(n => n.SessionKey == "task-a");
        Assert.Equal(3, entry.Total);          // 重建会话带全部保留历史（预览/分页同源）
        Assert.Equal(1, entry.Unread);         // 仅新消息计未读
        Assert.Equal("a3", entry.Last.Content);
    }

    [Fact]
    public async Task AppendAsync_FreshSession_RowStartsWithZeroWatermark()
    {
        // 全新会话（无历史消息）建行：existingMax=0，预置水位为 0——与旧实现行为一致
        var message = await _service.AppendAsync(ChatMessageDirection.发送, "first", "text", sessionKey: "task-new");

        var row = await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-new");
        Assert.Equal(message.Seq, row.LastSeq);
        Assert.Equal(0, row.LastReadSeq);
    }

    // ==================================================================== 跨会话触发消息迁移（§4.3）

    [Fact]
    public async Task MoveMessageSessionAsync_DefaultOrigin_MovesRowAndUpsertsTargetSession()
    {
        // 默认会话（SessionKey=null）触发 → 迁任务会话：行改键、Seq 不变、目标会话行建行推进 LastSeq
        var bubble = await _service.AppendAsync(ChatMessageDirection.接收, "随机密码-1", "text", ChatMessageStatus.已读);

        var (moved, seq) = await _service.MoveMessageSessionAsync(bubble.MsgId, "task-pwd");

        Assert.True(moved);
        Assert.Equal(bubble.Seq, seq); // 行 Seq 不变：增量同步游标、已读水位不受影响
        Assert.Equal("task-pwd", (await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Id == bubble.Id)).SessionKey);
        Assert.Equal(bubble.Seq, (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-pwd")).LastSeq);
        // 源会话行保留、LastSeq 不回拨（水位只影响列表排序，容忍残留）
        Assert.Equal(bubble.Seq, (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "")).LastSeq);
    }

    [Fact]
    public async Task MoveMessageSessionAsync_TargetSessionExists_AdvancesLastSeq()
    {
        await _service.AppendAsync(ChatMessageDirection.发送, "历史消息", "text", sessionKey: "task-pwd"); // seq1 建会话行
        var bubble = await _service.AppendAsync(ChatMessageDirection.接收, "触发", "text", ChatMessageStatus.已读,
            sessionKey: "task-other"); // seq2

        var (moved, _) = await _service.MoveMessageSessionAsync(bubble.MsgId, "task-pwd");

        Assert.True(moved);
        Assert.Equal(bubble.Seq,
            (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-pwd")).LastSeq); // 推进
        // 源会话行保留不回拨
        Assert.Equal(bubble.Seq,
            (await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "task-other")).LastSeq);
    }

    [Fact]
    public async Task MoveMessageSessionAsync_AlreadyMovedOrMissing_Idempotent()
    {
        var bubble = await _service.AppendAsync(ChatMessageDirection.接收, "触发", "text", ChatMessageStatus.已读);

        var (moved, _) = await _service.MoveMessageSessionAsync(bubble.MsgId, "task-pwd");
        Assert.True(moved);

        // 二次迁移：行已在目标会话 → 0 行幂等
        var (again, seq) = await _service.MoveMessageSessionAsync(bubble.MsgId, "task-pwd");
        Assert.False(again);
        Assert.Equal(0, seq);

        // msgId 不存在 / 参数缺失防御：一律 (false, 0)
        (again, seq) = await _service.MoveMessageSessionAsync("no-such-msg", "task-pwd");
        Assert.False(again);
        (again, seq) = await _service.MoveMessageSessionAsync(null, "task-pwd");
        Assert.False(again);
        (again, seq) = await _service.MoveMessageSessionAsync(bubble.MsgId, "   ");
        Assert.False(again);
    }

    [Fact]
    public async Task MoveMessageSessionAsync_OnlyMovesUserSubmittedRows()
    {
        // 只迁「接收」方向（用户气泡）；机器人下发/通知镜像（发送方向）同 msgId 不动——
        // 出站消息会话键由 ctx.Notify 门面管理，不经本方法
        await _service.AppendAsync(ChatMessageDirection.发送, "机器人消息", "text", ChatMessageStatus.已送达, "bot-msg-id");

        var (moved, _) = await _service.MoveMessageSessionAsync("bot-msg-id", "task-pwd");

        Assert.False(moved);
        Assert.Null((await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.MsgId == "bot-msg-id")).SessionKey);
    }

    /// <summary>首次 SaveChangesAsync 抛 DbUpdateException 的可控假件：模拟 Seq 撞车后重试成功。</summary>
    private sealed class FlakyDbContext : QuantumSqliteDbContext
    {
        public FlakyDbContext(DbContextOptions<QuantumSqliteDbContext> options) : base(options)
        {
        }

        public bool ThrowOnce { get; set; } = true;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnce)
            {
                ThrowOnce = false;
                throw new DbUpdateException("simulated seq conflict");
            }
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
