using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Quartz;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// AI 写权限工具（2026-09-21 写权限扩展计划 §6）回归：
/// 开关全关不变量、触发源白名单（Manual 独享）、delete_script（引用护栏/留底失败拒删/force 去重回归）、
/// save_task（JObject 键存在性合并/子任务回填/Manager 不暴露）、delete_task 备份、env 组（脱敏审计/备份）、
/// CustomData 五工具（读-合并-写/互斥/上限/备份）、系统提示词分权限、单 run 写操作计数上限。
/// 触碰 CacheManager 与进程级 Quartz 容器，并入 ConstsState 全局静态串行集合。
/// </summary>
[Collection("ConstsState")]
public class AgentWriteToolsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly ScriptVersionService _versionService;
    private readonly AgentTestRunService _testRunService;
    private readonly AgentProposalService _proposalService;
    private readonly AgentWriteTools _writeTools;
    private readonly AiProviderService _providerService;
    private readonly AgentService _service;
    private readonly FakeWriteLlm _llm = new();

    /// <summary>测试进程级 Quartz 容器（RAM 存储）：UpdateAsync 走作业解绑/重建，与 SessionGroupingTests 同款。</summary>
    private static ServiceProvider _quartzProvider;

    public AgentWriteToolsTests()
    {
        _quartzProvider?.Dispose();
        var services = new ServiceCollection();
        services.AddQuartz();
        _quartzProvider = services.BuildServiceProvider();
        JobHelper.Init(_quartzProvider.GetRequiredService<Quartz.ISchedulerFactory>());
        (_connection, _db) = AppTestDb.Create();
        _versionService = new ScriptVersionService(_db);
        _testRunService = new AgentTestRunService(_db);
        _proposalService = new AgentProposalService(_db, _versionService, _testRunService);
        var taskService = new TaskService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance), _versionService);
        _writeTools = new AgentWriteTools(_db, taskService, new EnvService(_db),
            new CustomDataService(_db), new CustomDataTitleService(_db), _versionService);
        _providerService = new AiProviderService(_db, _llm);
        _service = new AgentService(_db, _providerService, _llm, null!, _proposalService, _testRunService, _writeTools);
        // 预置空缓存：CacheManager 回源会尝试按 appsettings 开真实库（测试环境必失败但留错误日志）
        CacheManager.Set(new List<TaskSubModel>());
        CacheManager.Set(new List<EnvModel>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        CacheManager.Refresh<TaskSubModel>();
        CacheManager.Refresh<EnvModel>();
    }

    private const string TaskSource = """
        using Quantum.Plugins;
        public class WriteToolTask : IQuantumTask
        {
            public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
            {
                ctx.Log("write tool test");
                return Task.CompletedTask;
            }
        }
        """;

    // ==================================================================== 1. 全关不变量 + 10. 触发源白名单

    [Fact]
    public void BuildTools_AllOff_ReturnsEmptyAndKeepsBaseline()
    {
        Assert.Empty(AgentWriteTools.BuildTools(new AiAgentWritePermissions { TriggerType = AiRunTrigger.Manual }));
        Assert.Empty(AgentWriteTools.BuildTools(null));
        // 全关实例的工具集 = 只读基线（写权限扩展前的现状），不含 list_tasks
        var names = BuildToolbox(new AiAgentWritePermissions { TriggerType = AiRunTrigger.Manual }).Tools.Select(n => n.Name).ToList();
        Assert.Equal(AgentToolbox.ReadOnlyTools.Select(n => n.Name), names);
        Assert.DoesNotContain("list_tasks", names);
    }

    [Fact]
    public void Resolve_NonManualTrigger_NeverGrantsWriteEvenAllEnabled()
    {
        var allOn = new AiSettingModel
        {
            AllowScriptDelete = true, AllowTaskManage = true, AllowEnvManage = true, AllowCustomDataManage = true
        };
        // 白名单语义：仅 Manual 可写；AutoFailure（无人值守失败分析，上下文是不可信任务日志）与已定义未用的 Retry 恒只读
        Assert.False(AiAgentWritePermissions.Resolve(allOn, AiRunTrigger.AutoFailure).AnyEnabled);
        Assert.False(AiAgentWritePermissions.Resolve(allOn, AiRunTrigger.Retry).AnyEnabled);
        Assert.False(AiAgentWritePermissions.Resolve(null, AiRunTrigger.Manual).AnyEnabled);
        var manual = AiAgentWritePermissions.Resolve(allOn, AiRunTrigger.Manual);
        Assert.True(manual.AllowScriptDelete && manual.AllowTaskManage && manual.AllowEnvManage && manual.AllowCustomDataManage);
    }

    [Fact]
    public async Task Toolbox_NonManualTrigger_WriteToolCallRejected()
    {
        await EnableWritePermissions(script: true);
        // 四开关全开 + AutoFailure 触发：工具集不含写工具与 list_tasks，直接调用也被执行前二次校验拒绝
        var snapshot = AiAgentWritePermissions.Resolve(AllOnSetting(), AiRunTrigger.AutoFailure);
        var box = BuildToolbox(snapshot);
        Assert.DoesNotContain("delete_script", box.Tools.Select(n => n.Name));
        Assert.DoesNotContain("list_tasks", box.Tools.Select(n => n.Name));
        var result = await box.InvokeAsync("delete_script", new JObject { ["fileName"] = "wtool_guard.cs" });
        Assert.Contains("写操作已拒绝", result);
    }

    [Fact]
    public async Task Toolbox_AllOff_WriteToolNameUnknown()
    {
        // 全关（且未传写工具执行器）：写工具名走「不支持的工具」（AgentServiceTests 同款锁定，此处覆盖开关运行中被关的回落）
        var box = new AgentToolbox(_db, false, "conv-off", _proposalService, _testRunService, AiTestRunNotifyMode.Silent);
        Assert.Contains("不支持的工具", await box.InvokeAsync("delete_script", new JObject { ["fileName"] = "x.cs" }));
    }

    // ==================================================================== 2. delete_script

    [Fact]
    public async Task DeleteScript_RemovesFileAndRecordsAgentDeleteVersionWithForce()
    {
        WriteScript("wtool_del.cs", TaskSource);
        // 典型场景：待删内容即该文件最新版本——不 force 会被 RecordAsync 哈希去重吞掉（返回 null）
        await _versionService.RecordAsync("wtool_del.cs", TaskSource, ScriptVersionSource.ManualEdit);

        await EnableWritePermissions(script: true);
        DrainLogs();
        var result = await InvokeWriteAsync("delete_script", """{"fileName":"wtool_del.cs","reason":"测试删除"}""");

        Assert.Contains("已删除", result);
        Assert.False(File.Exists(ScriptPath("wtool_del.cs")));
        var agentDelete = await _db.ScriptVersions.AsNoTracking()
            .Where(n => n.FileName == "wtool_del.cs" && n.Source == ScriptVersionSource.AgentDelete)
            .ToListAsync();
        var row = Assert.Single(agentDelete);
        Assert.Equal("AI", row.Creator);
        Assert.Contains("测试删除", row.Remark);
        // 双轨审计：操作日志一条 Info
        Assert.Contains(TakeLogs(), n => n.Title == "AI 写操作：删除脚本" && n.LogType == LogType.AI助手 && n.Remark.Contains("wtool_del.cs"));
    }

    [Fact]
    public async Task DeleteScript_VersionRecordFails_RefusesDelete()
    {
        WriteScript("wtool_keep.cs", TaskSource);
        await EnableWritePermissions(script: true);
        // 留底必失败：版本表被移除，RecordQuietlyAsync 吞异常返回 null → 不得执行删除（「先留底后删除」的失败分支）
        await _db.Database.ExecuteSqlRawAsync("DROP TABLE t_script_version");

        var result = await InvokeWriteAsync("delete_script", """{"fileName":"wtool_keep.cs"}""");

        Assert.Contains("版本留底失败，已拒绝删除", result);
        Assert.True(File.Exists(ScriptPath("wtool_keep.cs")));
        Assert.Contains(TakeLogs(), n => n.Title == "AI 写操作被拒：删除脚本");
    }

    [Theory]
    [InlineData(true)]   // 启用任务引用
    [InlineData(false)]  // 已禁用任务引用同样拒绝
    public async Task DeleteScript_ReferencedByTask_Rejected(bool enable)
    {
        WriteScript("wtool_ref.cs", TaskSource);
        await SeedTaskAsync("wtool-t-ref", "引用任务", "wtool_ref.cs", enable: enable);
        await EnableWritePermissions(script: true);

        var result = await InvokeWriteAsync("delete_script", """{"fileName":"wtool_ref.cs"}""");

        Assert.Contains("被以下任务引用", result);
        Assert.Contains("引用任务", result);
        Assert.True(File.Exists(ScriptPath("wtool_ref.cs")));
    }

    [Fact]
    public async Task DeleteScript_ReferenceCaseInsensitive()
    {
        WriteScript("wtool_case.cs", TaskSource);
        // 引用比对按 OrdinalIgnoreCase（与 list_scripts 口径一致）：大小写变体不得绕过护栏
        await SeedTaskAsync("wtool-t-case", "大小写任务", "WTOOL_CASE.cs", enable: true);
        await EnableWritePermissions(script: true);

        var result = await InvokeWriteAsync("delete_script", """{"fileName":"wtool_case.cs"}""");

        Assert.Contains("被以下任务引用", result);
        Assert.True(File.Exists(ScriptPath("wtool_case.cs")));
    }

    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("agent-tmp/shadow.cs")]
    [InlineData("no_such_script.cs")]
    public async Task DeleteScript_TraversalStagingOrMissing_Rejected(string fileName)
    {
        await EnableWritePermissions(script: true);
        var result = await InvokeWriteAsync("delete_script", new JObject { ["fileName"] = fileName }.ToString());
        Assert.DoesNotContain("已删除", result);
    }

    [Fact]
    public async Task RecordAsync_ForceBypassesDedup_DefaultUnchanged()
    {
        // force 缺省（既有调用方）：同内容第二次记录返回 null（哈希去重行为不变回归）
        var first = await _versionService.RecordAsync("wtool_dedup.cs", TaskSource, ScriptVersionSource.ManualEdit);
        Assert.NotNull(first);
        var second = await _versionService.RecordAsync("wtool_dedup.cs", TaskSource, ScriptVersionSource.ManualEdit);
        Assert.Null(second);
        // force:true 绕过去重，动作类留底必有溯源行
        var forced = await _versionService.RecordAsync("wtool_dedup.cs", TaskSource, ScriptVersionSource.AgentDelete, force: true);
        Assert.NotNull(forced);
        // 吞异常版：fileName 非法时吞异常并返回 null（留底失败语义）
        Assert.Null(await _versionService.RecordQuietlyAsync(" ", TaskSource, ScriptVersionSource.ManualEdit));
    }

    // ==================================================================== 3. save_task / delete_task / list_tasks

    [Fact]
    public async Task SaveTask_Add_CreatesTaskWithDefaults()
    {
        WriteScript("wtool_new.cs", TaskSource);
        await EnableWritePermissions(task: true);

        var missing = await InvokeWriteAsync("save_task", """{"fileName":"wtool_new.cs"}""");
        Assert.Contains("必须提供 name", missing);
        var ghost = await InvokeWriteAsync("save_task", """{"name":"AI新任务","fileName":"ghost.cs"}""");
        Assert.Contains("脚本文件不存在", ghost);

        var ok = await InvokeWriteAsync("save_task",
            """{"name":"AI新任务","fileName":"wtool_new.cs","command":"ai-cmd","cron":null}""");
        Assert.Contains("已新增任务", ok);
        var task = await _db.Tasks.AsNoTracking().SingleAsync(n => n.Name == "AI新任务");
        Assert.Equal("wtool_new.cs", task.FileName);
        Assert.Equal("ai-cmd", task.Command);
        Assert.False(task.Manager);   // AI 不可制造管理员专属任务
        Assert.True(task.Enable);     // 新增默认启用
    }

    [Fact]
    public async Task SaveTask_EditOnlyCron_OtherFieldsUntouched()
    {
        await SeedTaskAsync("wtool-t-edit", "编辑目标", "wtool_edit.cs", enable: false,
            sessionName: "影视", remark: "原备注", manager: true, communicationTypes: "legacy", enablePush: true);
        WriteScript("wtool_edit.cs", TaskSource);
        await EnableWritePermissions(task: true);

        var result = await InvokeWriteAsync("save_task", """{"id":"wtool-t-edit","cron":"0 0 8 * * ?"}""");

        Assert.Contains("Cron", result);
        var task = await _db.Tasks.AsNoTracking().SingleAsync(n => n.Id == "wtool-t-edit");
        Assert.Equal("0 0 8 * * ?", task.Cron);
        // 未传字段一律保留（含不暴露的 Manager/CommunicationTypes 与会话名、推送开关）
        Assert.Equal("编辑目标", task.Name);
        Assert.Equal("影视", task.SessionName);
        Assert.Equal("原备注", task.Remark);
        Assert.True(task.Manager);
        Assert.Equal("legacy", task.CommunicationTypes);
        Assert.True(task.EnablePush);
        Assert.False(task.Enable);
    }

    [Fact]
    public async Task SaveTask_EditExplicitFalseAndZero_AppliedByKeyPresence()
    {
        // 键存在性合并回归：显式传 enable=false / dayLimit=0 必须生效（「非 null 判定」会把它们当未传或反之）
        await SeedTaskAsync("wtool-t-zero", "显式值任务", "wtool_zero.cs", enable: true, dayLimit: 5);
        await EnableWritePermissions(task: true);

        var result = await InvokeWriteAsync("save_task", """{"id":"wtool-t-zero","enable":false,"dayLimit":0}""");

        Assert.Contains("Enable", result);
        Assert.Contains("DayLimit", result);
        var task = await _db.Tasks.AsNoTracking().SingleAsync(n => n.Id == "wtool-t-zero");
        Assert.False(task.Enable);
        Assert.Equal(0, task.DayLimit);
        Assert.Equal("显式值任务", task.Name);
    }

    [Fact]
    public async Task SaveTask_EditRebindToMissingScript_Rejected()
    {
        await SeedTaskAsync("wtool-t-bind", "换绑任务", "wtool_bind.cs", enable: false);
        await EnableWritePermissions(task: true);

        var result = await InvokeWriteAsync("save_task", """{"id":"wtool-t-bind","fileName":"ghost_rebind.cs"}""");

        Assert.Contains("脚本文件不存在", result);
        Assert.Equal("wtool_bind.cs", (await _db.Tasks.AsNoTracking().SingleAsync(n => n.Id == "wtool-t-bind")).FileName);
    }

    [Fact]
    public async Task SaveTask_TaskSubs_ReplacedWhenPassed_BackfilledWhenAbsent()
    {
        var seed = await SeedTaskAsync("wtool-t-subs", "子任务回填", "wtool_subs.cs", enable: false);
        await _db.TaskSubs.AddAsync(new Quantum.Entities.Model.TaskSubModel
        {
            Id = "sub-old", TaskId = "wtool-t-subs", Name = "旧步骤", Command = "old-cmd", CommandEnv = "m", Sort = 1
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        // UpdateAsync 全删重插按缓存取 Id、工具回填也读缓存——播种现有子任务
        CacheManager.Set(await _db.TaskSubs.AsNoTracking().ToListAsync());
        await EnableWritePermissions(task: true);

        // 不传 taskSubs：现有子任务回填，不得被「全删重插」语义清空
        await InvokeWriteAsync("save_task", """{"id":"wtool-t-subs","remark":"仅改备注"}""");
        var subs = await _db.TaskSubs.AsNoTracking().Where(n => n.TaskId == "wtool-t-subs").ToListAsync();
        var kept = Assert.Single(subs);
        Assert.Equal("旧步骤", kept.Name);
        Assert.Equal("仅改备注", (await _db.Tasks.AsNoTracking().SingleAsync(n => n.Id == "wtool-t-subs")).Remark);

        // 生产里 UpdateAsync 的 AddRangeAsync(扩展)会 Refresh 缓存、下次 Get 回源拿最新行；
        // 测试回源开不到内存库，手动重播同款最新行
        CacheManager.Set(await _db.TaskSubs.AsNoTracking().ToListAsync());

        // 传入即全量替换
        await InvokeWriteAsync("save_task",
            """{"id":"wtool-t-subs","taskSubs":[{"name":"新步骤","command":"new-cmd","commandEnv":"m","sort":1}]}""");
        var replaced = await _db.TaskSubs.AsNoTracking().Where(n => n.TaskId == "wtool-t-subs").ToListAsync();
        Assert.Single(replaced);
        Assert.Equal("新步骤", replaced[0].Name);
    }

    [Fact]
    public async Task SaveTask_SchemaDoesNotExposeManagerOrCommunicationTypes()
    {
        // 全开工具集的 schema 必须全部是合法 JSON（save_task 的深层嵌套曾漏过一个闭括号，LLM 请求阶段才炸）
        var allOn = AgentWriteTools.BuildTools(new AiAgentWritePermissions
        {
            TriggerType = AiRunTrigger.Manual,
            AllowScriptDelete = true, AllowTaskManage = true, AllowEnvManage = true, AllowCustomDataManage = true
        });
        Assert.Equal(11, allOn.Count);
        foreach (var tool in allOn)
        {
            Assert.NotNull(JObject.Parse(tool.ParametersJson));
        }
        var saveTask = allOn.Single(n => n.Name == "save_task");
        Assert.DoesNotContain("manager", saveTask.ParametersJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("communicationTypes", saveTask.ParametersJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteTask_CascadesAndBacksUpWithSubs()
    {
        await SeedTaskAsync("wtool-t-del", "待删任务", "wtool_del_task.cs", enable: false);
        await _db.TaskSubs.AddAsync(new Quantum.Entities.Model.TaskSubModel
        {
            Id = "sub-del", TaskId = "wtool-t-del", Name = "删前步骤", Command = "del-cmd", CommandEnv = "m", Sort = 1
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        await EnableWritePermissions(task: true);
        DrainLogs();

        var result = await InvokeWriteAsync("delete_task", """{"ids":["wtool-t-del"]}""");

        Assert.Contains("已删除 1 个任务", result);
        Assert.False(await _db.Tasks.AsNoTracking().AnyAsync(n => n.Id == "wtool-t-del"));
        Assert.False(await _db.TaskSubs.AsNoTracking().AnyAsync(n => n.TaskId == "wtool-t-del"));
        // 备份文件含子任务集合（仅走 GetByIdAsync 会漏——TaskSubs 是 [NotMapped]）
        var dir = Path.Combine("logs", AgentWriteTools.DeleteTasksLogDir);
        var backup = new DirectoryInfo(dir).GetFiles().OrderByDescending(n => n.Name).First();
        var text = await File.ReadAllTextAsync(backup.FullName);
        Assert.Contains("待删任务", text);
        Assert.Contains("删前步骤", text);
        File.Delete(backup.FullName);
        Assert.Contains(TakeLogs(), n => n.Title == "AI 写操作：删除任务");
    }

    [Fact]
    public async Task DeleteTask_OverLimit_Rejected()
    {
        await EnableWritePermissions(task: true);
        var ids = string.Join(",", Enumerable.Range(0, 51).Select(n => $"\"id-{n}\""));
        var result = await InvokeWriteAsync("delete_task", $$"""{"ids":[{{ids}}]}""");
        Assert.Contains($"最多删除 {AgentWriteTools.MaxBatchIds}", result);
    }

    [Fact]
    public async Task ListTools_ListTasks_AssembledWithAnyWriteSwitch()
    {
        await SeedTaskAsync("wtool-t-list", "清单任务", "wtool_list.cs", enable: true, sessionName: "清单会话");
        await EnableWritePermissions(env: true);
        // 任一写开关开启即装配 list_tasks（含各写域工具，schema 全部合法）
        var box = BuildToolbox(AiAgentWritePermissions.Resolve(AllOnSetting(onlyEnv: true), AiRunTrigger.Manual));
        var names = box.Tools.Select(n => n.Name).ToList();
        Assert.Contains("list_tasks", names);
        Assert.Contains("save_env", names);
        Assert.DoesNotContain("delete_script", names);
        foreach (var tool in box.Tools)
        {
            Assert.NotNull(JObject.Parse(tool.ParametersJson));
        }
        var text = await box.InvokeAsync("list_tasks", []);
        Assert.Contains("wtool-t-list", text);
        Assert.Contains("清单任务", text);
        Assert.Contains("清单会话", text);
    }

    // ==================================================================== 5. save_env / delete_env

    [Fact]
    public async Task SaveEnv_AddThenEditById_MergesFields()
    {
        await EnableWritePermissions(env: true);

        var added = await InvokeWriteAsync("save_env", """{"name":"wt_cookie","value":"secret-abc"}""");
        Assert.Contains("已新增环境变量", added);
        var env = await _db.Envs.AsNoTracking().SingleAsync(n => n.Name == "wt_cookie");
        Assert.Equal("secret-abc", env.Value);

        // 按 id 编辑：只改备注与值，未传字段保留
        var edited = await InvokeWriteAsync("save_env", $$"""{"id":"{{env.Id}}","value":"secret-xyz","remark":"新备注"}""");
        Assert.Contains("已更新环境变量", edited);
        var after = await _db.Envs.AsNoTracking().SingleAsync(n => n.Id == env.Id);
        Assert.Equal("secret-xyz", after.Value);
        Assert.Equal("新备注", after.Remark);
        Assert.True(after.Enable);
    }

    [Fact]
    public async Task SaveEnv_InvalidName_Rejected()
    {
        await EnableWritePermissions(env: true);
        var result = await InvokeWriteAsync("save_env", """{"name":"bad-name","value":"x"}""");
        Assert.Contains("环境变量名称", result);
        Assert.False(await _db.Envs.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task SaveEnv_DuplicateNameValue_Skipped()
    {
        await _db.Envs.AddAsync(new EnvModel { Id = "env-dup", Name = "wt_dup", Value = "v1", Enable = true, CreateTime = DateTime.Now });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        await EnableWritePermissions(env: true);

        var result = await InvokeWriteAsync("save_env", """{"name":"wt_dup","value":"v1"}""");

        Assert.Contains("同名同值的环境变量已存在", result);
        Assert.Single(await _db.Envs.AsNoTracking().Where(n => n.Name == "wt_dup").ToListAsync());
    }

    [Fact]
    public async Task SaveEnv_AuditRemarkMasksValue()
    {
        await EnableWritePermissions(env: true);
        DrainLogs();
        await InvokeWriteAsync("save_env", """{"name":"wt_mask","value":"plain-secret-value"}""");
        var audit = TakeLogs().Single(n => n.Title == "AI 写操作：新增环境变量");
        // 明文值绝不进系统日志（t_ai_step 轨迹才是另一落点）；remark 只记变量名 + 值长度
        Assert.DoesNotContain("plain-secret-value", audit.Remark);
        Assert.Contains("wt_mask (len=18)", audit.Remark);
    }

    [Fact]
    public async Task DeleteEnv_DeletesWithBackup()
    {
        await _db.Envs.AddAsync(new EnvModel { Id = "env-del", Name = "wt_del", Value = "v", Enable = true, CreateTime = DateTime.Now });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        await EnableWritePermissions(env: true);
        DrainLogs();

        var result = await InvokeWriteAsync("delete_env", """{"ids":["env-del"]}""");

        Assert.Contains("已删除 1 条环境变量", result);
        Assert.False(await _db.Envs.AsNoTracking().AnyAsync(n => n.Id == "env-del"));
        // EnvService.Delete 自带备份（logs/deleteEnvs）+ 操作日志，另有本工具的 AI 助手审计
        Assert.True(Directory.GetFiles(Path.Combine("logs", "deleteEnvs")).Length > 0);
        Assert.Contains(TakeLogs(), n => n.Title == "AI 写操作：删除环境变量" && n.Remark.Contains("wt_del"));
    }

    [Fact]
    public async Task DeleteEnv_OverLimit_Rejected()
    {
        await EnableWritePermissions(env: true);
        var ids = string.Join(",", Enumerable.Range(0, 51).Select(n => $"\"e-{n}\""));
        var result = await InvokeWriteAsync("delete_env", $$"""{"ids":[{{ids}}]}""");
        Assert.Contains($"最多删除 {AgentWriteTools.MaxBatchIds}", result);
    }

    // ==================================================================== 7. CustomData 五工具

    [Fact]
    public async Task SaveCustomDataTitle_UpsertKeepsUnpassedTitles()
    {
        await EnableWritePermissions(customData: true);
        await InvokeWriteAsync("save_custom_data_title",
            """{"type":"wt_seen","typeName":"测试类型","titles":{"title1":"键","title2":"值"}}""");
        // 二次保存仅传 title2：title1/typeName 保留原值（读-合并-写）
        var result = await InvokeWriteAsync("save_custom_data_title", """{"type":"wt_seen","titles":{"title2":"新列名"}}""");
        Assert.Contains("已更新数据类型", result);
        var title = await _db.CustomDataTitles.AsNoTracking().SingleAsync(n => n.Type == "wt_seen");
        Assert.Equal("测试类型", title.TypeName);
        Assert.Equal("键", title.Title1);
        Assert.Equal("新列名", title.Title2);
    }

    [Fact]
    public async Task DeleteCustomDataTitle_ClearsDataRowsAndMenu()
    {
        await _db.CustomDataTitles.AddAsync(new CustomDataTitleModel { Id = "cdt-1", Type = "wt_del", TypeName = "待删类型", Title1 = "键" });
        _db.CustomDatas.Add(new CustomDataModel { Id = "cd-1", Type = "wt_del", Data1 = "a", CreateTime = DateTime.Now });
        _db.CustomDatas.Add(new CustomDataModel { Id = "cd-2", Type = "wt_del", Data1 = "b", CreateTime = DateTime.Now });
        _db.Menus.Add(new MenuModel { Name = "待删类型", Path = "/custom-data/wt_del", ParentName = "customData" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        await EnableWritePermissions(customData: true);
        DrainLogs();

        var result = await InvokeWriteAsync("delete_custom_data_title", """{"type":"wt_del"}""");

        Assert.Contains("已删除数据类型", result);
        Assert.Contains("2 行", result);
        Assert.False(await _db.CustomDataTitles.AsNoTracking().AnyAsync(n => n.Type == "wt_del"));
        Assert.False(await _db.CustomDatas.AsNoTracking().AnyAsync(n => n.Type == "wt_del"));
        Assert.False(await _db.Menus.AsNoTracking().AnyAsync(n => n.Path == "/custom-data/wt_del"));
        // 标题定义 JSON 记入操作日志（数据不做全量备份，定义可从日志追溯）
        var audit = TakeLogs().Single(n => n.Title == "AI 写操作：删除数据类型");
        Assert.Contains("待删类型", audit.Remark);
    }

    [Fact]
    public async Task SaveCustomData_RowsLimitAndInsert()
    {
        await EnableWritePermissions(customData: true);
        var rows = string.Join(",", Enumerable.Range(0, 201).Select(n => $$"""{"data1":"r{{n}}"}"""));
        var over = await InvokeWriteAsync("save_custom_data", $$"""{"type":"wt_rows","rows":[{{rows}}]}""");
        Assert.Contains($"最多 {AgentWriteTools.MaxCustomDataRows} 行", over);

        var ok = await InvokeWriteAsync("save_custom_data", """{"type":"wt_rows","rows":[{"data1":"x"},{"data1":"y"}]}""");
        Assert.Contains("已向类型 wt_rows 新增 2 行", ok);
        Assert.Equal(2, await _db.CustomDatas.AsNoTracking().CountAsync(n => n.Type == "wt_rows"));
    }

    [Fact]
    public async Task UpdateCustomData_MergesOnlyPassedColumns()
    {
        await _db.CustomDatas.AddAsync(new CustomDataModel { Id = "cd-upd", Type = "wt_upd", Data1 = "a", Data2 = "b", CreateTime = DateTime.Now });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        await EnableWritePermissions(customData: true);

        await InvokeWriteAsync("update_custom_data", """{"rows":[{"id":"cd-upd","data1":"a2"}]}""");

        var row = await _db.CustomDatas.AsNoTracking().SingleAsync(n => n.Id == "cd-upd");
        Assert.Equal("a2", row.Data1);
        // 未传列保留原值（EF 整行 Update 会把未设值列抹 null——必须先合并）
        Assert.Equal("b", row.Data2);
    }

    [Fact]
    public async Task DeleteCustomData_MutexClearLimitAndBackup()
    {
        await EnableWritePermissions(customData: true);
        // 互斥：ids 与 clear 同传报参数错误
        var mutex = await InvokeWriteAsync("delete_custom_data", """{"ids":["x"],"clear":true}""");
        Assert.Contains("互斥", mutex);
        // clear 模式缺 type
        var noType = await InvokeWriteAsync("delete_custom_data", """{"clear":true}""");
        Assert.Contains("必须同时传 type", noType);

        await _db.CustomDatas.AddRangeAsync(
            new CustomDataModel { Id = "cd-m1", Type = "wt_mux", Data1 = "m1", CreateTime = DateTime.Now },
            new CustomDataModel { Id = "cd-m2", Type = "wt_mux", Data1 = "m2", CreateTime = DateTime.Now });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        DrainLogs();

        // ids 模式：删前文件备份
        var byIds = await InvokeWriteAsync("delete_custom_data", """{"ids":["cd-m1"]}""");
        Assert.Contains("已删除 1 行", byIds);
        Assert.False(await _db.CustomDatas.AsNoTracking().AnyAsync(n => n.Id == "cd-m1"));
        var backupDir = new DirectoryInfo(Path.Combine("logs", AgentWriteTools.DeleteCustomDataLogDir));
        var backup = backupDir.GetFiles().OrderByDescending(n => n.Name).First();
        Assert.Contains("m1", await File.ReadAllTextAsync(backup.FullName));
        File.Delete(backup.FullName);

        // clear 模式：整类型清空，返回清空条数
        var cleared = await InvokeWriteAsync("delete_custom_data", """{"type":"wt_mux","clear":true}""");
        Assert.Contains("已清空类型 wt_mux 的全部 1 行", cleared);
        Assert.False(await _db.CustomDatas.AsNoTracking().AnyAsync(n => n.Type == "wt_mux"));
        Assert.Contains(TakeLogs(), n => n.Title == "AI 写操作：清空自定义数据");
    }

    [Fact]
    public async Task DeleteCustomData_OverLimit_Rejected()
    {
        await EnableWritePermissions(customData: true);
        var ids = string.Join(",", Enumerable.Range(0, 201).Select(n => $"\"c-{n}\""));
        var result = await InvokeWriteAsync("delete_custom_data", $$"""{"ids":[{{ids}}]}""");
        Assert.Contains($"最多删除 {AgentWriteTools.MaxCustomDataRows} 行", result);
    }

    // ==================================================================== 8. 系统提示词

    [Fact]
    public void BuildSystemPrompt_AllOff_KeepsNoWriteDiscipline()
    {
        var prompt = _service.BuildSystemPrompt(new AiSettingModel());
        Assert.Contains("你没有写文件的权限", prompt);
        Assert.DoesNotContain("写权限工具", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_WriteEnabled_SwitchesDiscipline()
    {
        var permissions = new AiAgentWritePermissions
        {
            TriggerType = AiRunTrigger.Manual, AllowTaskManage = true, AllowEnvManage = true
        };
        var prompt = _service.BuildSystemPrompt(new AiSettingModel(), permissions);
        Assert.Contains("写权限工具", prompt);
        Assert.Contains("save_task / delete_task", prompt);
        Assert.Contains("不可逆", prompt);
        Assert.DoesNotContain("你没有写文件的权限", prompt);
        // 脚本内容修改仍走提案
        Assert.Contains("propose_fix", prompt);
    }

    // ==================================================================== 9. 写操作计数上限

    [Fact]
    public async Task Loop_ExceedsWriteOpsPerRun_TerminatesRun()
    {
        await EnableWritePermissions(env: true);
        var conversation = await _service.EnsureDefaultConversationAsync();
        var provider = await _providerService.SaveProviderAsync(new Entities.DTOs.AiProviderSaveModel
        {
            Name = "Mock", BaseUrl = "https://mock.local", ApiKey = "sk-mock-1234567890"
        });
        await _providerService.SaveModelAsync(new AiModelModel { ProviderId = provider.Id, ModelId = "mock-model", IsDefault = true });
        var setting = await _providerService.GetSettingAsync();
        setting.MaxRounds = 20;
        await _db.SaveChangesAsync();
        var run = new AiRunModel
        {
            ConversationId = conversation.Id, TriggerType = AiRunTrigger.Manual,
            Status = AiRunStatus.Running, CreateTime = DateTime.Now
        };
        _db.AiRuns.Add(run);
        await _db.SaveChangesAsync();
        for (var i = 0; i < 20; i++)
        {
            _llm.Script.Enqueue(ToolResponse("save_env", """{"name":"wt_loop","value":"v1"}"""));
        }

        DrainLogs();
        await _service.RunLoopAsync(run.Id, 0, new CancellationTokenSource());

        var saved = await _db.AiRuns.AsNoTracking().FirstAsync(n => n.Id == run.Id);
        Assert.Equal(AiRunStatus.Failed, saved.Status);
        var message = await _db.AiMessages.AsNoTracking()
            .Where(n => n.ConversationId == conversation.Id).OrderByDescending(n => n.Seq).FirstAsync();
        Assert.Contains("写操作次数上限", message.Content);
        // 第 11 次写调用即超限终止（上限 10）
        var writeCalls = await _db.AiSteps.AsNoTracking()
            .Where(n => n.RunId == run.Id && n.Kind == "tool_call" && n.Name == "save_env").CountAsync();
        Assert.Equal(AgentWriteTools.MaxWriteOpsPerRun + 1, writeCalls);
    }

    // ==================================================================== 测试工具

    private static void WriteScript(string fileName, string content)
    {
        var path = Path.Combine("scripts", "quantum", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ScriptPath(string fileName) => Path.Combine("scripts", "quantum", fileName);

    /// <summary>种一个任务（含会话名/Manager 等编辑用例需要守住的原值）。</summary>
    private async Task<TaskModel> SeedTaskAsync(string id, string name, string fileName, bool enable,
        string sessionName = null, string remark = null, bool manager = false, string communicationTypes = null,
        bool enablePush = false, int dayLimit = 0)
    {
        var task = new TaskModel
        {
            Id = id, Name = name, Command = "cmd-" + id, FileName = fileName, Enable = enable,
            SessionName = sessionName, Remark = remark, Manager = manager, CommunicationTypes = communicationTypes,
            EnablePush = enablePush, DayLimit = dayLimit, CreateTime = DateTime.Now
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return task;
    }

    private async Task EnableWritePermissions(bool script = false, bool task = false, bool env = false, bool customData = false)
    {
        _db.AiSettings.Add(new AiSettingModel
        {
            Id = AiSettingModel.DefaultId,
            Enable = true,
            AllowScriptDelete = script,
            AllowTaskManage = task,
            AllowEnvManage = env,
            AllowCustomDataManage = customData,
            CreateTime = DateTime.Now
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private static AiSettingModel AllOnSetting(bool onlyEnv = false)
        => new()
        {
            AllowScriptDelete = !onlyEnv, AllowTaskManage = !onlyEnv,
            AllowEnvManage = true, AllowCustomDataManage = !onlyEnv
        };

    private AgentToolbox BuildToolbox(AiAgentWritePermissions permissions)
        => new(_db, false, "conv-write", _proposalService, _testRunService, AiTestRunNotifyMode.Silent, _writeTools, permissions);

    /// <summary>以 Manual 触发 + 指定开关的权限快照构造 toolbox 并调用工具。</summary>
    private Task<string> InvokeWriteAsync(string tool, string argsJson)
        => BuildToolbox(AiAgentWritePermissions.Resolve(SettingSnapshot(), AiRunTrigger.Manual)).InvokeAsync(tool, JObject.Parse(argsJson));

    /// <summary>从测试库取当前设置行（未种时为全关默认）。</summary>
    private AiSettingModel SettingSnapshot()
        => _db.AiSettings.AsNoTracking().FirstOrDefault(n => n.Id == AiSettingModel.DefaultId) ?? new AiSettingModel();

    private static void DrainLogs()
    {
        while (LogServiceHelper.Logs.TryDequeue(out _))
        {
        }
    }

    private static List<LogModel> TakeLogs()
    {
        var logs = new List<LogModel>();
        while (LogServiceHelper.Logs.TryDequeue(out var log))
        {
            logs.Add(log);
        }
        return logs;
    }

    private static LlmChatResponse ToolResponse(string name, string arguments) => new()
    {
        ToolCalls = [new LlmToolCall { Id = "call_" + Guid.NewGuid().ToString("N")[..6], Name = name, Arguments = arguments }],
        PromptTokens = 10,
        CompletionTokens = 5
    };

    /// <summary>按脚本回放的假 LLM（与 AgentServiceTests 同款）。</summary>
    private sealed class FakeWriteLlm : ILlmClient
    {
        public Queue<LlmChatResponse> Script { get; } = new();

        public Task<List<string>> ListModelsAsync(AiProviderModel provider, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
            => Task.FromResult(Script.Count > 0 ? Script.Dequeue() : new LlmChatResponse { Content = "（脚本已耗尽）" });
    }
}
