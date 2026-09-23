using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 仓库级 code review 高置信度/边缘项修复回归：
/// H1/H2 脚本名白名单、H3 外触更新 EF 双实例冲突、B1 会话消息 (UserId,Seq) 唯一索引、
/// H7 日志目录写读一致 + 详情读取路径越狱防御、B2 EnsureCreated 库迁移历史回填。
/// </summary>
public class CodeReviewFixesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;

    public CodeReviewFixesTests()
    {
        (_connection, _db) = AppTestDb.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---------- H1/H2：脚本文件名白名单（Task 保存 / 外触 SrciptFile / 分组仓库导入共用） ----------

    [Theory]
    [InlineData("../evil.cs")]
    [InlineData("a/../b.cs")]
    [InlineData("run.sh")]
    [InlineData("run.bat")]
    [InlineData("legacy.js")]
    [InlineData("legacy.py")]
    [InlineData("")]
    public void ValidateScriptFileName_RejectsTraversalOrNonExecutableExtension(string fileName)
    {
        Assert.Throws<BusinessException>(() => TaskService.ValidateScriptFileName(fileName));
    }

    [Theory]
    [InlineData("a.cs")]
    [InlineData("group/sub/x.cs")]
    public void ValidateScriptFileName_AcceptsRootedScriptWithExecutableExtension(string fileName)
    {
        // 只校验路径与扩展名，不要求文件已存在（先建配置后传脚本是合法顺序）
        TaskService.ValidateScriptFileName(fileName);
    }

    // ---------- H1 + H3：外触内执配置（更新分支 EF 双实例 / SrciptFile 白名单） ----------

    [Fact]
    public async Task OpenTriggerTask_UpdateViaRequestBodyCopy_PersistsWithoutEfTrackingConflict()
    {
        var service = new OpenTriggerTaskService(_db);
        var created = new OpenTriggerTask { Name = "t", Secret = "s1", SrciptFile = "a.cs", HttpMethod = "GET", Enable = true };
        await service.AddAsync(created);

        // 模拟前端编辑弹窗：请求体反序列化出的另一同 Id 实例（修复前必抛 InvalidOperationException）
        var fromBody = new OpenTriggerTask
        {
            Id = created.Id, Name = "t2", Secret = "s2", SrciptFile = "b.cs", HttpMethod = "POST",
            Enable = false, Whitelist = "1.2.3.4"
        };
        await service.AddAsync(fromBody);

        var reloaded = await _db.OpenTriggerTasks.AsNoTracking().SingleAsync(n => n.Id == created.Id);
        Assert.Equal("t2", reloaded.Name);
        Assert.Equal("s2", reloaded.Secret);
        Assert.Equal("b.cs", reloaded.SrciptFile);
        Assert.Equal("POST", reloaded.HttpMethod);
        Assert.False(reloaded.Enable);
        Assert.Equal("1.2.3.4", reloaded.Whitelist);
    }

    [Fact]
    public async Task OpenTriggerTask_IllegalSrciptFile_IsRejectedOnSave()
    {
        var service = new OpenTriggerTaskService(_db);
        await Assert.ThrowsAsync<BusinessException>(() => service.AddAsync(new OpenTriggerTask
        {
            Name = "x", Secret = "s1", SrciptFile = "../evil.cs", HttpMethod = "GET"
        }));
        Assert.Empty(_db.OpenTriggerTasks);
    }

    // ---------- B1：会话消息 Seq 唯一索引（AppendAsync 撞车重试的前提） ----------

    [Fact]
    public async Task ChatMessage_DuplicateSeq_IsRejectedByUniqueIndex()
    {
        _db.ChatMessages.Add(MakeChat(seq: 1));
        await _db.SaveChangesAsync();

        _db.ChatMessages.Add(MakeChat(seq: 1));
        // 修复前无唯一约束：两条同 Seq 双双成功，增量同步游标与已读回执语义被破坏
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task ChatMessage_SequentialGlobalSeq_IsAllowed()
    {
        _db.ChatMessages.Add(MakeChat(seq: 1));
        _db.ChatMessages.Add(MakeChat(seq: 2));
        await _db.SaveChangesAsync();
        Assert.Equal(2, await _db.ChatMessages.CountAsync());
    }

    // ---------- H7：日志目录名写读一致 + 详情读取越狱防御 ----------

    [Theory]
    [InlineData("a.js", "a")]
    [InlineData("group/sub/x.js", "group_sub_x")]
    [InlineData(@"group\sub\x.js", "group_sub_x")]
    public void LogDirNameFrom_StripsPathSeparators(string fileName, string expected)
    {
        Assert.Equal(expected, TaskExcuteService.LogDirNameFrom(fileName));
    }

    [Fact]
    public async Task LogDetails_TraversalMetadata_IsRejected()
    {
        _db.Logs.Add(new LogModel
        {
            Id = "L1", CreateTime = DateTime.Now, LogType = LogType.任务日志, Title = "t",
            DirectoryName = "../../evil", LogPath = "secret.log"
        });
        await _db.SaveChangesAsync();
        var service = new LogsService(_db);

        // 存量脏记录（DirectoryName 含穿越片段）不得借详情端点读任意文件
        await Assert.ThrowsAsync<BusinessException>(() => service.GetDetailsAsync("L1"));
    }

    [Fact]
    public async Task LogDetails_SanitizedSubDirectory_ReadsWrittenLogFile()
    {
        // 写入侧：子目录脚本日志落在 logs/group_sub_x/（分隔符已净化）
        var dir = $"./logs/{TaskExcuteService.LogDirNameFrom("group/sub/x.js")}";
        Directory.CreateDirectory(dir);
        var file = $"{dir}/{DateTime.Now:yyyyMMddHHmmssfff}.log";
        await File.WriteAllTextAsync(file, "HELLO-LOG");

        _db.Logs.Add(new LogModel
        {
            Id = "L2", CreateTime = DateTime.Now, LogType = LogType.任务日志, Title = "t",
            // 记录侧与写入侧同款净化（修复前原样 "group/sub"，详情永远读不到）
            DirectoryName = TaskExcuteService.LogDirNameFrom("group/sub/x.js"),
            LogPath = Path.GetFileName(file)
        });
        await _db.SaveChangesAsync();

        var service = new LogsService(_db);
        var content = await service.GetDetailsAsync("L2");
        Assert.Equal("HELLO-LOG", content);
    }

    /// <summary>
    /// §2-6：日志统计「按日 + 按级别」计数下推到数据库聚合后，结果须与旧的内存 GroupBy 实现逐值一致，
    /// 且窗口外（早于 since）的行被排除。SQLite 分支经 strftime 真实执行。
    /// </summary>
    [Fact]
    public async Task Statistics_Pushdown_MatchesLevelAndDailyCounts()
    {
        var today = DateTime.Now.Date.AddHours(10);
        var yesterday = today.AddDays(-1);
        var outOfWindow = today.AddDays(-60);

        void Add(DateTime t, LogSeverity sev, string id)
            => _db.Logs.Add(new LogModel { Id = id, CreateTime = t, Severity = sev, LogType = LogType.任务日志, Title = id });

        Add(today, LogSeverity.Info, "s1");
        Add(today, LogSeverity.Info, "s2");
        Add(today, LogSeverity.Warn, "s3");
        Add(yesterday, LogSeverity.Info, "s4");
        Add(yesterday, LogSeverity.Error, "s5");
        Add(outOfWindow, LogSeverity.Info, "s6"); // 窗口外，不应计入
        await _db.SaveChangesAsync();

        var service = new LogsService(_db);
        var stat = await service.StatisticsAsync(days: 7);

        Assert.Equal(5, stat.TotalCount);
        Assert.Equal(3, stat.InfoCount);
        Assert.Equal(1, stat.WarnCount);
        Assert.Equal(1, stat.ErrorCount);
        Assert.Equal(2, stat.Daily.Count);
        // 升序：昨天在前、今天在后
        Assert.Equal(yesterday.Date, stat.Daily[0].Date);
        Assert.Equal(2, stat.Daily[0].Count);
        Assert.Equal(today.Date, stat.Daily[1].Date);
        Assert.Equal(3, stat.Daily[1].Count);
    }

    [Fact]
    public void LiveLogBuffer_StreamsAllLinesToDisk_BoundsRingTail()
    {
        // §1-9：环形缓冲只驻留最近 capacityLines 行供实时尾览（内存有界），完整日志逐行落盘（不丢旧行）
        var dir = Path.Combine(Path.GetTempPath(), "quantum-logbuf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "x.log");
        try
        {
            var buffer = new TaskExcuteService.LiveLogBuffer(path, capacityLines: 2);
            buffer.AppendLine("l1");
            buffer.AppendLine("l2");
            buffer.AppendLine("l3");
            var snap = buffer.Snapshot();
            Assert.Contains("l2", snap);
            Assert.Contains("l3", snap);
            Assert.DoesNotContain("l1", snap);
            buffer.Dispose();

            var disk = File.ReadAllText(path);
            Assert.Contains("l1", disk);
            Assert.Contains("l2", disk);
            Assert.Contains("l3", disk);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    [Fact]
    public async Task GetDetails_LiveBufferHit_ReturnsTailWithExecutingMarker()
    {
        // §1-9 读取侧：执行中（Logs 命中）取环形快照 + "执行中"标记，不读文件；未命中才读落盘文件
        var dirName = "livebuf_" + Guid.NewGuid().ToString("N");
        var logFileName = "20260101000000000.log";
        var relativePath = $"./logs/{dirName}/{logFileName}";
        var tempDir = Path.Combine(Path.GetTempPath(), "quantum-livedir", dirName);
        Directory.CreateDirectory(tempDir);
        var diskPath = Path.Combine(tempDir, logFileName);
        var buffer = new TaskExcuteService.LiveLogBuffer(diskPath);
        buffer.AppendLine("正在跑的实时行A");
        TaskExcuteService.Logs[relativePath] = buffer;
        try
        {
            _db.Logs.Add(new LogModel
            {
                Id = "LIVE1", CreateTime = DateTime.Now, LogType = LogType.任务日志, Title = "t",
                DirectoryName = dirName, LogPath = logFileName
            });
            await _db.SaveChangesAsync();

            var service = new LogsService(_db);
            var content = await service.GetDetailsAsync("LIVE1");
            Assert.Contains("正在跑的实时行A", content);
            Assert.Contains("当前任务正在执行中", content);
        }
        finally
        {
            TaskExcuteService.Logs.TryRemove(relativePath, out _);
            buffer.Dispose();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    // ---------- B2：EnsureCreated 建库的迁移历史回填 ----------

    [Fact]
    public async Task EnsureCreatedDb_AfterHistoryBackfill_HasNoPendingMigrationsAndMigrateIsNoOp()
    {
        var dbPath = $"crfix_mig_{Guid.NewGuid():N}.db";
        try
        {
            var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<QuantumSqliteDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new QuantumSqliteDbContext(options);
            Assert.True(db.Database.EnsureCreated());
            // EnsureCreated 不写 __EFMigrationsHistory：全链迁移挂起，未来新增迁移会被整链重放卡死
            Assert.NotEmpty(db.Database.GetPendingMigrations());

            DbInitializer.MarkAllMigrationsAsApplied(db);

            Assert.Empty(db.Database.GetPendingMigrations());
            Assert.Equal(db.Database.GetMigrations().Count(), db.Database.GetAppliedMigrations().Count());
            // 模拟未来启动：Migrate 应为 no-op 且成功
            await db.Database.MigrateAsync();
            db.Dispose();
            await connection.DisposeAsync();
            // Microsoft.Data.Sqlite 内部连接池会持有已关闭连接：清池后才能删掉临时库文件
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    // ---------- B4：中间件下行帧与推送并发写同一连接的串行化 ----------

    [Fact]
    public async Task SendToOneAsync_ConcurrentWithPush_IsSerializedPerConnection()
    {
        var manager = new AppWebSocketManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<AppWebSocketManager>.Instance);
        var socket = new FakeSocket(delayPerSendMs: 5);
        manager.Add(socket);

        // 中间件帧（pong/sync/error）走 SendToOneAsync，与随机时刻的广播 SendToAllAsync 并发
        var push = Enumerable.Range(0, 10)
            .Select(_ => manager.SendToAllAsync("{\"type\":\"notify\"}"));
        var frames = Enumerable.Range(0, 10)
            .Select(_ => manager.SendToOneAsync(socket, "{\"type\":\"pong\"}"));
        await Task.WhenAll(push.Cast<Task>().Concat(frames.Cast<Task>()));

        Assert.False(socket.ConcurrentWriteDetected, "检测到对同一连接的并发写，SendToOneAsync 未与推送路径互斥");
        Assert.Equal(20, socket.SendCount);
    }

    [Fact]
    public async Task SendToOneAsync_UnregisteredSocket_ReturnsNotFound()
    {
        var manager = new AppWebSocketManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<AppWebSocketManager>.Instance);
        Assert.Equal(SendToOneResult.NotFound, await manager.SendToOneAsync(new FakeSocket(), "{}"));
    }

    /// <summary>
    /// 内存型 WebSocket：记录发送次数，检测并发写（与 AppWebSocketManagerTests 同款语义）。
    /// </summary>
    private sealed class FakeSocket(int delayPerSendMs = 0) : System.Net.WebSockets.WebSocket
    {
        private readonly object _gate = new();
        private bool _sending;

        public int SendCount { get; private set; }
        public bool ConcurrentWriteDetected { get; private set; }

        private System.Net.WebSockets.WebSocketState _state = System.Net.WebSockets.WebSocketState.Open;

        public override System.Net.WebSockets.WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override string? CloseStatusDescription => null;

        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;

        public override void Abort() => _state = System.Net.WebSockets.WebSocketState.Aborted;

        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = System.Net.WebSockets.WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = System.Net.WebSockets.WebSocketState.Closed;

        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override async Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_sending)
                {
                    ConcurrentWriteDetected = true;
                }
                _sending = true;
            }
            try
            {
                if (delayPerSendMs > 0)
                {
                    await Task.Delay(delayPerSendMs, cancellationToken);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _sending = false;
                    SendCount++;
                }
            }
        }
    }

    private static ChatMessageModel MakeChat(long seq)
    {
        return new ChatMessageModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Seq = seq,
            Direction = ChatMessageDirection.发送,
            Content = "hi",
            ContentType = "text",
            MsgId = Guid.NewGuid().ToString("N"),
            Status = ChatMessageStatus.已送达,
            CreateTime = DateTime.Now
        };
    }
}
