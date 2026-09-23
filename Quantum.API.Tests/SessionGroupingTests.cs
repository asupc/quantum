using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Plugins;
using Quartz;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 会话分组归并（2026-09-18 批次）：
/// 三级路由（①点选精确 / ②会话任务集[P4 多命中不外溢] / ③全局）、任务会话名校验（含导入口径）、
/// 改名迁移（单属迁移 / P1 共享守卫跳过 / P2 目标行合并 / 幂等）、门面双键与选项载荷根部 taskId、
/// 出站克隆置空 TargetTaskId（P3 防自回路）。
/// TaskService 用例触碰 CacheManager（UpdateAsync 删子任务步骤读缓存），并入 ConstsState 全局静态串行集合（§5-3）。
/// </summary>
[Collection("ConstsState")]
public class SessionGroupingTests : IDisposable
{
    private const string Sentinel = "session-grouping-test";

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly AppMessageService _messageService;
    private readonly NotifyService _notifyService;

    /// <summary>
    /// 测试进程级 Quartz 容器（RAM 存储）：JobHelper 持有的是工厂的底层容器，
    /// GetScheduler 惰性解析服务，容器必须与测试进程同生命周期（不可提前 Dispose）。
    /// </summary>
    private static ServiceProvider _quartzProvider;

    public SessionGroupingTests()
    {
        // UpdateAsync 内部走 Quartz 作业解绑/重建：装配 RAM 调度器工厂（与 Startup.AddQuartz 同源；
        // Quartz 4 已移除 StdSchedulerFactory，测试任务无 Cron，CreateQuartzJob 早退）
        _quartzProvider?.Dispose();
        var services = new ServiceCollection();
        services.AddQuartz();
        _quartzProvider = services.BuildServiceProvider();
        JobHelper.Init(_quartzProvider.GetRequiredService<ISchedulerFactory>());
        (_connection, _db) = AppTestDb.Create();
        _messageService = new AppMessageService(_db, NullLogger<AppMessageService>.Instance);
        _notifyService = new NotifyService();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private TaskService NewTaskService() => new(_db, _messageService, new ScriptVersionService(_db));

    /// <summary>
    /// 换发送接缝捕获 DTO（只留本类哨兵内容，隔离其他测试可能入队的消息）；
    /// 静态泵可能抢先出队但走同一 Sender，故按哨兵过滤 + 轮询等待（与 RichMessagePayloadTests 同一惯例）。
    /// </summary>
    private static async Task<List<MessageProccessDTO>> CaptureSentAsync(Func<Task> send, int expected)
    {
        var captured = new List<MessageProccessDTO>();
        var original = SendMessageHelper.Sender;
        SendMessageHelper.Sender = m =>
        {
            if (m.message != null && m.message.Contains(Sentinel, StringComparison.Ordinal))
            {
                captured.Add(m);
            }
            return Task.CompletedTask;
        };
        try
        {
            await send();
            for (var i = 0; captured.Count < expected && i < 200; i++)
            {
                await Task.Delay(10);
            }
        }
        finally
        {
            SendMessageHelper.Sender = original;
        }
        return captured;
    }

    // ==================================================================== 出站会话键解析

    [Fact]
    public void ResolveSessionKey_BlankSessionName_UsesTaskId_OtherwiseTrimmedName()
    {
        Assert.Equal("t-1", TaskExcuteService.ResolveSessionKey("t-1", null));
        Assert.Equal("t-1", TaskExcuteService.ResolveSessionKey("t-1", "   "));
        Assert.Equal("媒体", TaskExcuteService.ResolveSessionKey("t-1", "  媒体 "));
        Assert.Equal("媒体", TaskExcuteService.ResolveSessionKey(new TaskModel { Id = "t-1", SessionName = " 媒体 " }));
    }

    // ==================================================================== 入站三级路由（P4 版）

    [Fact]
    public void SelectTaskCandidates_TargetTaskIdHit_ReturnsOnlyTarget()
    {
        // 合并会话内点选：①级精确路由只返回点选来源任务（纯数字双触发不回归）
        var movie = NewTask("t-movie", "^[0-9,，\\s]+$");
        var music = NewTask("t-music", "^[0-9,，\\s]+$");
        var process = new MessageProcess();

        var candidates = MessageProcess.SelectTaskCandidates([movie, music],
            new MessageProccessDTO { message = "3", TargetTaskId = "t-music", SessionKey = "媒体", CommunicationType = CommunicationType.App },
            process.commandReg);

        var candidate = Assert.Single(candidates);
        Assert.Equal("t-music", candidate.Id);
    }

    [Fact]
    public void SelectTaskCandidates_TargetTaskIdMismatch_FallsIntoSessionRouting()
    {
        // ①级失效（指令不匹配 / 任务不存在）不直接否决：落入②级会话任务集路由
        var movie = NewTask("t-movie", "^[0-9,，\\s]+$");          // 组内（Id == SessionKey）
        var music = NewTask("t-music", "^音乐搜索.*");               // TargetTaskId 指向它但纯数字不匹配其指令
        var process = new MessageProcess();

        // 指令不匹配：落②，组内唯一命中 → 只返回电影港
        var mismatch = MessageProcess.SelectTaskCandidates([movie, music],
            new MessageProccessDTO { message = "3", TargetTaskId = "t-music", SessionKey = "t-movie", CommunicationType = CommunicationType.App },
            process.commandReg);
        var hit = Assert.Single(mismatch);
        Assert.Equal("t-movie", hit.Id);

        // 目标任务不存在（已删）：同样落②
        var gone = MessageProcess.SelectTaskCandidates([movie, music],
            new MessageProccessDTO { message = "3", TargetTaskId = "t-gone", SessionKey = "t-movie", CommunicationType = CommunicationType.App },
            process.commandReg);
        Assert.Equal("t-movie", Assert.Single(gone).Id);
    }

    [Fact]
    public void SelectTaskCandidates_SessionNameGroup_UniqueHit_ReturnsOnlyIt()
    {
        // ②级会话名归组：两个任务配同一会话名，组内指令唯一命中 → 只返回它（不误触会话外任务）
        var movie = NewTask("t-movie", "^[0-9,，\\s]+$", sessionName: "媒体");
        var music = NewTask("t-music", "^音乐搜索.*", sessionName: "媒体");
        var weather = NewTask("t-weather", "^天气.*");
        var process = new MessageProcess();

        var candidates = MessageProcess.SelectTaskCandidates([movie, music, weather],
            new MessageProccessDTO { message = "3", SessionKey = "媒体", CommunicationType = CommunicationType.App },
            process.commandReg);

        var hit = Assert.Single(candidates);
        Assert.Equal("t-movie", hit.Id);
    }

    [Fact]
    public void SelectTaskCandidates_SessionGroupMultiHit_ReturnsOnlyGroupHits()
    {
        // P4 决策：组内多命中只返回组内命中集，不外溢全局——组外任务（t-weather 同样指令匹配）不被误触
        var movie = NewTask("t-movie", "^[0-9,，\\s]+$", sessionName: "媒体");
        var music = NewTask("t-music", "^[0-9,，\\s]+$", sessionName: "媒体");
        var weather = NewTask("t-weather", "^[0-9,，\\s]+$");
        var process = new MessageProcess();

        var candidates = MessageProcess.SelectTaskCandidates([movie, music, weather],
            new MessageProccessDTO { message = "3", SessionKey = "媒体", CommunicationType = CommunicationType.App },
            process.commandReg);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(["t-movie", "t-music"], candidates.Select(n => n.Id).OrderBy(n => n));
    }

    [Fact]
    public void SelectTaskCandidates_SessionGroupZeroHit_FallsBackToAll()
    {
        // 组内零命中 → 回落全局（会话不是指令隔离区，与现状语义一致；由上层做指令匹配）
        var movie = NewTask("t-movie", "^[0-9,，\\s]+$", sessionName: "媒体");
        var music = NewTask("t-music", "^[0-9,，\\s]+$", sessionName: "媒体");
        var weather = NewTask("t-weather", "^天气.*");
        var process = new MessageProcess();

        var candidates = MessageProcess.SelectTaskCandidates([movie, music, weather],
            new MessageProccessDTO { message = "天气 北京", SessionKey = "媒体", CommunicationType = CommunicationType.App },
            process.commandReg);

        Assert.Equal(3, candidates.Count);
    }

    // ==================================================================== 会话名校验

    [Fact]
    public async Task AddAsync_SessionNameTrimmed_BlankStoredAsNull()
    {
        await NewTaskService().AddAsync(SaveModel("空白会话名", "   "));
        await NewTaskService().AddAsync(SaveModel("归并影视", "  影视  "));

        Assert.Null((await _db.Tasks.AsNoTracking().SingleAsync(n => n.Name == "空白会话名")).SessionName);
        Assert.Equal("影视", (await _db.Tasks.AsNoTracking().SingleAsync(n => n.Name == "归并影视")).SessionName);
    }

    [Fact]
    public async Task ValidateSessionName_TooLong_Throws()
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewTaskService().ValidateSessionNameAsync(new string('x', TaskService.MaxSessionNameLength + 1)));
        Assert.Contains("100", ex.Message);
        // 恰好 100 不抛
        await NewTaskService().ValidateSessionNameAsync(new string('x', TaskService.MaxSessionNameLength));
    }

    [Fact]
    public async Task ValidateSessionName_CollidesWithTaskId_Throws_UnlessExcluded()
    {
        _db.Tasks.Add(new TaskModel { Id = "abc123", Name = "现存任务", Command = "cmd", FileName = "abc.cs", Enable = true, CreateTime = DateTime.Now });
        await _db.SaveChangesAsync();

        // 与现存任务 Id 相同：拒绝（防入站路由串线）
        await Assert.ThrowsAsync<BusinessException>(() => NewTaskService().ValidateSessionNameAsync("abc123"));

        // 导入口径：该 Id 将被同文件名替换删除 → 从校验集中剔除后放行
        await NewTaskService().ValidateSessionNameAsync("abc123", ["abc123"]);
    }

    // ==================================================================== 改名迁移（§2.5 / P1 / P2）

    [Fact]
    public async Task UpdateAsync_SingleOwnerRename_MigratesMessagesAndSessionRow()
    {
        var task = await SeedTaskAsync("t-a");
        Assert.Null(task.SessionName);
        await _messageService.AppendAsync(ChatMessageDirection.发送, "旧消息1", "text", sessionKey: "t-a");
        await _messageService.AppendAsync(ChatMessageDirection.发送, "旧消息2", "text", sessionKey: "t-a");

        await NewTaskService().UpdateAsync(SaveModel("任务t-a", "影视", id: "t-a", fileName: "t-a.cs"));

        Assert.Equal("影视", (await _db.Tasks.AsNoTracking().SingleAsync(n => n.Id == "t-a")).SessionName);
        // 消息行整段迁移到新键
        Assert.Equal(2, await _db.ChatMessages.AsNoTracking().CountAsync(n => n.SessionKey == "影视"));
        Assert.Null(await _db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(n => n.SessionKey == "t-a"));
        // 会话行同步迁移（源键无行 → 改名），LastSeq 随迁
        var session = await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "影视");
        Assert.Equal(2, session.LastSeq);
        Assert.False(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "t-a"));
    }

    [Fact]
    public async Task UpdateAsync_SharedOldKey_SkipsMigration()
    {
        // P1 守卫：旧键被其他在册任务（t-b 同配「共享」）继续解析 → 跳过迁移，历史留在共享会话
        await SeedTaskAsync("t-a", "共享");
        await SeedTaskAsync("t-b", "共享");
        await _messageService.AppendAsync(ChatMessageDirection.发送, "共享消息", "text", sessionKey: "共享");

        await NewTaskService().UpdateAsync(SaveModel("任务t-a", "独立", id: "t-a", fileName: "t-a.cs"));

        Assert.Equal("独立", (await _db.Tasks.AsNoTracking().SingleAsync(n => n.Id == "t-a")).SessionName);
        // 未迁移：消息与会话行都留在原键（t-b 继续使用）
        Assert.NotNull(await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.SessionKey == "共享"));
        Assert.True(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "共享"));
    }

    [Fact]
    public async Task RenameSession_TargetRowExists_MergesNotViolatesUniqueKey()
    {
        // P2 合并：目标键已有会话行时合并且删源行（直接改键会撞主键）
        var first = await _messageService.AppendAsync(ChatMessageDirection.发送, "a1", "text", sessionKey: "task-a");
        await Task.Delay(20); // 拉开 CreateTime 便于断言「取小」
        await _messageService.AppendAsync(ChatMessageDirection.发送, "b1", "text", sessionKey: "影视");

        await _messageService.RenameSession("task-a", "影视");

        Assert.Equal(2, await _db.ChatMessages.AsNoTracking().CountAsync(n => n.SessionKey == "影视"));
        var merged = await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == "影视");
        Assert.Equal(2, merged.LastSeq);                                   // 取大
        Assert.True(merged.CreateTime <= first.CreateTime);                // 取小
        Assert.False(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "task-a")); // 删源行
    }

    [Fact]
    public async Task RenameSession_OldKeyHasNoRows_Idempotent()
    {
        // 旧键既无消息也无会话行：无操作、不产生新键会话行
        await _messageService.RenameSession("nothing", "new-name");

        Assert.Empty(await _db.ChatMessages.AsNoTracking().Where(n => n.SessionKey == "new-name").ToListAsync());
        Assert.False(await _db.ChatSessions.AsNoTracking().AnyAsync(n => n.SessionKey == "new-name"));
    }

    // ==================================================================== 门面双键与选项载荷 taskId

    [Fact]
    public async Task Facade_SendOptionsAsync_PayloadCarriesRootTaskId_SessionKeyUsesGroupName()
    {
        var captured = await CaptureSentAsync(() => new QuantumNotifyFacade(_notifyService, "t-music", "媒体")
            .SendOptionsAsync($"{Sentinel} 选一首：", [new QuantumOption("3", "晴天")]), 1);

        var dto = Assert.Single(captured);
        Assert.Equal("媒体", dto.SessionKey);   // 出站会话键 = 会话名（非任务 Id）
        var payload = JObject.Parse(dto.Payload);
        Assert.Equal("t-music", (string)payload["taskId"]);   // 根部 taskId = 真实任务 Id
        Assert.Equal("3", (string)payload["options"][0]["key"]);
    }

    [Fact]
    public async Task Facade_SendImageAsync_WithOptions_CarriesRootTaskId()
    {
        var captured = await CaptureSentAsync(() => new QuantumNotifyFacade(_notifyService, "t-movie", "媒体")
            .SendImageAsync($"https://img/{Sentinel}.jpg", "说明", [new QuantumOption("45148", "订阅本片")]), 1);

        var dto = Assert.Single(captured);
        Assert.Equal("媒体", dto.SessionKey);
        var payload = JObject.Parse(dto.Payload);
        Assert.Equal("t-movie", (string)payload["taskId"]);
        Assert.Equal("45148", (string)payload["options"][0]["key"]);
    }

    [Fact]
    public async Task Facade_SendVideoAsync_PosterPayload_HasNoTaskId()
    {
        // 视频消息走独立封面载荷、无选项：不注入 taskId（点选路由仅对选项消息有意义）
        var captured = await CaptureSentAsync(() => new QuantumNotifyFacade(_notifyService, "t-video", "媒体")
            .SendVideoAsync($"https://v/{Sentinel}.mp4", null, $"https://img/{Sentinel}-poster.jpg"), 1);

        var dto = Assert.Single(captured);
        var payload = JObject.Parse(dto.Payload);
        Assert.Equal($"https://img/{Sentinel}-poster.jpg", (string)payload["poster"]);
        Assert.Null((string)payload["taskId"]);
    }

    // ==================================================================== 出站克隆置空 TargetTaskId（P3）

    [Fact]
    public async Task SendMessage_CloneClearsTargetTaskId_KeepsSessionKey()
    {
        var captured = await CaptureSentAsync(() =>
        {
            var dto = new MessageProccessDTO
            {
                message = $"{Sentinel} 原文",
                user_id = "admin",
                CommunicationType = CommunicationType.App,
                SessionKey = "媒体",
                TargetTaskId = "t-music"
            };
            dto.SendMessage($"{Sentinel} 回复");
            return Task.CompletedTask;
        }, 1);

        var clone = Assert.Single(captured);
        Assert.Equal($"{Sentinel} 回复", clone.message);
        // P3：克隆必须置空点选来源任务——否则回复文本匹配自身指令会经①级形成自触发回路
        Assert.Null(clone.TargetTaskId);
        // 会话键保留（快捷回复/系统指令回复归来源会话的既有机制不受影响）
        Assert.Equal("媒体", clone.SessionKey);
    }

    // ==================================================================== 跨会话触发消息迁移（§4.2）

    [Fact]
    public void ResolveMoveTarget_UniqueCrossSession_ReturnsTaskKey()
    {
        // 单任务命中且任务会话 ≠ 来源会话：迁到任务会话键（Id/会话名口径与 ResolveSessionKey 一致）
        var steps = new List<TaskCommandStep> { Step(NewTask("t-pwd", "cmd")) };
        Assert.Equal("t-pwd", MessageProcess.ResolveMoveTarget(steps, null));          // 默认会话来源
        Assert.Equal("t-pwd", MessageProcess.ResolveMoveTarget(steps, "别的会话"));
        Assert.Equal("媒体", MessageProcess.ResolveMoveTarget(
            new List<TaskCommandStep> { Step(NewTask("t-movie", "cmd", sessionName: " 媒体 ")) }, null));
    }

    [Fact]
    public void ResolveMoveTarget_SameKeyAsOrigin_ReturnsNull()
    {
        // 用户已在目标会话里触发：不迁移（来源=任务 Id / 来源=会话名两侧等价；归一化 Trim）
        var steps = new List<TaskCommandStep> { Step(NewTask("t-pwd", "cmd")) };
        Assert.Null(MessageProcess.ResolveMoveTarget(steps, "t-pwd"));
        Assert.Null(MessageProcess.ResolveMoveTarget(steps, " t-pwd "));
        var named = new List<TaskCommandStep> { Step(NewTask("t-movie", "cmd", sessionName: "媒体")) };
        Assert.Null(MessageProcess.ResolveMoveTarget(named, "媒体"));
        // origin 归一化：null 与空白同义（默认会话），仍照迁
        Assert.Equal("t-pwd", MessageProcess.ResolveMoveTarget(steps, "  "));
    }

    [Fact]
    public void ResolveMoveTarget_MultipleDistinctSessionKeys_ReturnsNull()
    {
        // D1：多命中且会话键不同 → 有歧义不迁移（留原会话，两边回复照常各自落会话）
        var steps = new List<TaskCommandStep>
        {
            Step(NewTask("t-a", "cmd")),
            Step(NewTask("t-b", "cmd", sessionName: "媒体"))
        };
        Assert.Null(MessageProcess.ResolveMoveTarget(steps, null));
    }

    [Fact]
    public void ResolveMoveTarget_MultipleTasksSameSessionKey_ReturnsThatKey()
    {
        // 合并会话多触发（两个任务同配「媒体」）：同键无歧义，照迁
        var steps = new List<TaskCommandStep>
        {
            Step(NewTask("t-a", "cmd", sessionName: "媒体")),
            Step(NewTask("t-b", "cmd", sessionName: "媒体"))
        };
        Assert.Equal("媒体", MessageProcess.ResolveMoveTarget(steps, null));
    }

    [Fact]
    public void ResolveMoveTarget_EmptySteps_ReturnsNull()
    {
        // 零命中（无任务消费）/ 空步骤集：不动
        Assert.Null(MessageProcess.ResolveMoveTarget([], null));
        Assert.Null(MessageProcess.ResolveMoveTarget(null, "任意会话"));
    }

    // ==================================================================== 测试工具

    private async Task<TaskModel> SeedTaskAsync(string id, string sessionName = null)
    {
        var task = new TaskModel
        {
            Id = id,
            Name = "任务" + id,
            Command = "cmd-" + id,
            FileName = id + ".cs",
            Enable = true,
            SessionName = sessionName,
            CreateTime = DateTime.Now
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        // 生产每次请求是独立 scoped DbContext；测试复用实例须解除跟踪，
        // 否则 UpdateAsync 内 db.Update(重查实体) 与种子实例同键双跟踪冲突（ScriptEngineTests 同款处理）
        _db.ChangeTracker.Clear();
        // UpdateAsync 删子任务步骤读 CacheManager：预置空缓存避免回源开库
        CacheManager.Set(new List<TaskSubModel>());
        return task;
    }

    private static TaskSaveModel SaveModel(string name, string sessionName, string id = null, string fileName = null) => new()
    {
        Id = id,
        Name = name,
        Command = "cmd-" + name,
        FileName = fileName ?? (name + ".cs"),
        Enable = true,
        SessionName = sessionName
    };

    private static TaskModel NewTask(string id, string command, string sessionName = null) =>
        new()
        {
            Id = id,
            Name = id,
            Command = command,
            EnableRegex = true,
            SessionName = sessionName,
            Enable = true
        };

    private static TaskCommandStep Step(TaskModel task) => new() { Task = task };
}
