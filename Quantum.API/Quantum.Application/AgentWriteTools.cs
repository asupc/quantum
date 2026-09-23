using System.Text;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// AI 写权限快照（2026-09-21 新增，AI 助手写权限工具扩展计划）：
/// 每次 run 启动时由 <see cref="AgentService"/> 按当时设置与触发源解析并快照进 toolbox；
/// 工具执行前还会按库中最新设置二次校验（防 run 进行中开关被关）。
/// 触发源取白名单：仅 <see cref="AiRunTrigger.Manual"/>（用户在会话里主动发起）才可能拿到写权限，
/// AutoFailure/Retry 及未来新增触发源一律视为全关——失败自动分析的上下文正是失败任务日志，
/// 是提示注入浓度最高的入口，结构性排除比逐个黑名单可靠。
/// </summary>
public sealed class AiAgentWritePermissions
{
    public string TriggerType { get; init; }

    public bool AllowScriptDelete { get; init; }

    public bool AllowTaskManage { get; init; }

    public bool AllowEnvManage { get; init; }

    public bool AllowCustomDataManage { get; init; }

    /// <summary>任一写开关开启（list_tasks 的装配条件——守住「全关时工具集与现状完全一致」的不变量）</summary>
    public bool AnyEnabled => AllowScriptDelete || AllowTaskManage || AllowEnvManage || AllowCustomDataManage;

    /// <summary>按设置与触发源解析权限：非 Manual 触发恒返回全关快照。</summary>
    public static AiAgentWritePermissions Resolve(AiSettingModel setting, string triggerType)
    {
        if (setting == null || triggerType != AiRunTrigger.Manual)
        {
            return new AiAgentWritePermissions { TriggerType = triggerType };
        }
        return new AiAgentWritePermissions
        {
            TriggerType = triggerType,
            AllowScriptDelete = setting.AllowScriptDelete,
            AllowTaskManage = setting.AllowTaskManage,
            AllowEnvManage = setting.AllowEnvManage,
            AllowCustomDataManage = setting.AllowCustomDataManage
        };
    }

    /// <summary>单个工具是否放行（写工具执行前二次校验用）。</summary>
    public bool IsAllowed(string toolName) => toolName switch
    {
        "delete_script" => AllowScriptDelete,
        "save_task" or "delete_task" => AllowTaskManage,
        "save_env" or "delete_env" => AllowEnvManage,
        "save_custom_data_title" or "delete_custom_data_title" or "save_custom_data"
            or "update_custom_data" or "delete_custom_data" => AllowCustomDataManage,
        "list_tasks" => AnyEnabled,
        _ => false
    };

    /// <summary>已开启的写能力描述（系统提示词纪律段用，向模型列举它拥有的权限）。</summary>
    public string DescribeEnabled()
    {
        var parts = new List<string>();
        if (AllowScriptDelete) parts.Add("delete_script（删除脚本文件）");
        if (AllowTaskManage) parts.Add("save_task / delete_task（任务的添加/修改/删除）");
        if (AllowEnvManage) parts.Add("save_env / delete_env（环境变量的添加/编辑/删除）");
        if (AllowCustomDataManage) parts.Add("save_custom_data_title / delete_custom_data_title / save_custom_data / update_custom_data / delete_custom_data（自定义数据类型与数据）");
        return string.Join("；", parts);
    }
}

/// <summary>
/// AI 写权限工具集（2026-09-21 新增，AI 助手写权限工具扩展计划 §4.2）：
/// 10 个写工具 + 1 个只读辅助（list_tasks），全部按 t_ai_setting 四开关动态装配、默认全关——
/// 全关时 <see cref="AgentToolbox"/> 下发给 LLM 的工具集与扩展前完全一致。
/// 安全设计（计划 §3.1）：仅 Manual 触发的 run 装配；执行前按库中最新开关 + 触发源二次校验；
/// 所有删除先留底后删除（脚本走版本表 force 留底、env 走既有备份目录、任务/CustomData 行删除前 JSON 备份）；
/// 「编辑」一律服务端读现状按 JObject 键存在性合并（杜绝全字段覆盖清空）；
/// 每次执行落 LogType.AI助手 双轨审计（t_ai_step 轨迹由 AgentService 记）。
/// </summary>
public class AgentWriteTools
{
    /// <summary>delete_task / delete_env 单次批量上限</summary>
    internal const int MaxBatchIds = 50;

    /// <summary>CustomData 行操作单次批量上限（新增/更新/删除）</summary>
    internal const int MaxCustomDataRows = 200;

    /// <summary>单次 run 写操作总次数上限（AgentService 计数，超限终止 run——防模型失控循环连写）</summary>
    internal const int MaxWriteOpsPerRun = 10;

    /// <summary>任务删除备份目录（相对 logs/，对齐 EnvService 的 deleteEnvs 模式）</summary>
    internal const string DeleteTasksLogDir = "deleteTasks";

    /// <summary>CustomData 行删除备份目录</summary>
    internal const string DeleteCustomDataLogDir = "deleteCustomData";

    readonly IQuantumDbContext _db;
    readonly TaskService _taskService;
    readonly EnvService _envService;
    readonly CustomDataService _customDataService;
    readonly CustomDataTitleService _customDataTitleService;
    readonly ScriptVersionService _scriptVersionService;

    public AgentWriteTools(IQuantumDbContext db, TaskService taskService, EnvService envService,
        CustomDataService customDataService, CustomDataTitleService customDataTitleService,
        ScriptVersionService scriptVersionService)
    {
        _db = db;
        _taskService = taskService;
        _envService = envService;
        _customDataService = customDataService;
        _customDataTitleService = customDataTitleService;
        _scriptVersionService = scriptVersionService;
    }

    // ==================================================================== 工具声明

    private static readonly LlmTool DeleteScriptTool = new()
    {
        Name = "delete_script",
        Description = "删除一个任务脚本文件（高危、不可直接撤销）。删除前自动在脚本版本表留底（AgentDelete 来源行），可从脚本版本页回滚恢复；被任何任务（含已禁用）引用的脚本会被拒绝。作用域限 scripts/quantum。",
        ParametersJson = """{"type":"object","properties":{"fileName":{"type":"string","description":"脚本相对路径（如 B站任务.cs）"},"reason":{"type":"string","description":"删除原因（记入操作日志）"}},"required":["fileName"]}"""
    };

    private static readonly LlmTool SaveTaskTool = new()
    {
        Name = "save_task",
        Description = "新增或编辑任务。编辑：传 id，只覆盖本次显式传入的字段（未传字段保留原值；enable=false/dayLimit=0 这类显式值会生效）；新增：不传 id，name 与 fileName 必填且脚本必须真实存在。taskSubs 传入即全量替换，不传则保留现有子任务。改脚本内容请继续走 propose_fix 提案，本工具只管任务配置。",
        ParametersJson = """{"type":"object","properties":{"id":{"type":"string","description":"任务 Id（编辑必填；新增时必须不传）"},"name":{"type":"string","description":"任务名称"},"command":{"type":"string","description":"触发指令（传 null 清空）"},"commandEnv":{"type":"string","description":"触发指令消息的环境变量名称"},"cron":{"type":"string","description":"定时表达式（传 null 清空=不定时）"},"fileName":{"type":"string","description":"脚本文件相对路径（换绑时必须真实存在于 scripts/quantum 下）"},"enable":{"type":"boolean","description":"启用状态"},"enableRegex":{"type":"boolean","description":"指令按正则匹配"},"dayLimit":{"type":"integer","description":"每日执行次数上限（0=不限）"},"enablePush":{"type":"boolean","description":"开启推送"},"pushGroup":{"type":"boolean","description":"允许群消息通知"},"revocation":{"type":"boolean","description":"撤回群消息"},"waitTime":{"type":"integer","description":"最长执行分钟数（0=默认 60）"},"taskStartNotify":{"type":"string","description":"任务开始通知文本"},"taskEndNotify":{"type":"string","description":"任务完成通知文本"},"sessionName":{"type":"string","description":"会话名（传 null 清空=不归组）"},"textToPicture":{"type":"boolean","description":"文字转图片"},"enableProxy":{"type":"boolean","description":"开启代理"},"remark":{"type":"string","description":"备注"},"taskSubs":{"type":"array","description":"子任务全量列表（传入即整体替换，不传保留现有）","items":{"type":"object","properties":{"name":{"type":"string"},"command":{"type":"string"},"commandEnv":{"type":"string"},"sort":{"type":"integer"},"enableRegex":{"type":"boolean"},"revocation":{"type":"boolean"},"waitTime":{"type":"integer"},"remark":{"type":"string"}}}}},"required":[]}"""
    };

    private static readonly LlmTool DeleteTaskTool = new()
    {
        Name = "delete_task",
        Description = "删除任务（连带子任务与定时调度，删除前完整配置自动 JSON 备份到 logs/deleteTasks）。先用 list_tasks 拿 Id，单次最多 50 个。",
        ParametersJson = """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"description":"任务 Id 列表（上限 50）"}},"required":["ids"]}"""
    };

    private static readonly LlmTool ListTasksTool = new()
    {
        Name = "list_tasks",
        Description = "列出全部任务的摘要清单（Id、名称、指令、Cron、启用、绑定脚本、会话名）。修改/删除任务前先用它拿到任务 Id。",
        ParametersJson = """{"type":"object","properties":{}}"""
    };

    private static readonly LlmTool SaveEnvTool = new()
    {
        Name = "save_env",
        Description = "新增或编辑环境变量。编辑：传 id，只覆盖显式传入的字段；新增：不传 id。同账号多凭据就加多条同名变量（平台执行时按 & 合并投递）；同名同值的新增会被跳过。名称规则：字母开头、仅含字母数字下划线、最长 64。",
        ParametersJson = """{"type":"object","properties":{"name":{"type":"string","description":"变量名"},"value":{"type":"string","description":"变量值"},"remark":{"type":"string","description":"备注"},"enable":{"type":"boolean","description":"是否启用"},"id":{"type":"string","description":"编辑时传该条 Id（不传即新增）"}},"required":["name","value"]}"""
    };

    private static readonly LlmTool DeleteEnvTool = new()
    {
        Name = "delete_env",
        Description = "删除环境变量（删除前自动 JSON 备份到 logs/deleteEnvs 并记操作日志，可恢复）。先用 list_envs 拿 Id，单次最多 50 条。",
        ParametersJson = """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"description":"变量 Id 列表（上限 50）"}},"required":["ids"]}"""
    };

    private static readonly LlmTool SaveCustomDataTitleTool = new()
    {
        Name = "save_custom_data_title",
        Description = "新增或编辑自定义数据类型（表头定义）。按 Type 幂等：存在即更新，未传的标题列保留原值；新建类型时 typeName 必填。会自动同步「数据管理」子菜单。",
        ParametersJson = """{"type":"object","properties":{"type":{"type":"string","description":"数据类型键（如 haojia_seen）"},"typeName":{"type":"string","description":"类型显示名"},"titles":{"type":"object","description":"列标题定义，仅传需变更的列","properties":{"title1":{"type":"string"},"title2":{"type":"string"},"title3":{"type":"string"},"title4":{"type":"string"},"title5":{"type":"string"},"title6":{"type":"string"},"title7":{"type":"string"},"title8":{"type":"string"},"title9":{"type":"string"},"title10":{"type":"string"},"title11":{"type":"string"},"title12":{"type":"string"},"title13":{"type":"string"},"title14":{"type":"string"},"title15":{"type":"string"}}},"hide":{"type":"boolean","description":"是否在菜单隐藏该类型"}},"required":["type"]}"""
    };

    private static readonly LlmTool DeleteCustomDataTitleTool = new()
    {
        Name = "delete_custom_data_title",
        Description = "删除一个自定义数据类型（含表头定义、菜单项）并连带清空该类型全部数据。高危：数据删除后无从恢复（标题定义会记入系统日志，数据不会备份）——需要留存务必提醒用户在删除前自行导出 CSV。如需清空数据但保留类型，用 delete_custom_data 的 clear 模式。",
        ParametersJson = """{"type":"object","properties":{"type":{"type":"string","description":"数据类型键"}},"required":["type"]}"""
    };

    private static readonly LlmTool SaveCustomDataTool = new()
    {
        Name = "save_custom_data",
        Description = "向某自定义数据类型批量新增数据行（每行 data1..data15 按需传）。单次最多 200 行。",
        ParametersJson = """{"type":"object","properties":{"type":{"type":"string","description":"数据类型键"},"rows":{"type":"array","maxItems":200,"description":"新增行列表","items":{"type":"object","properties":{"data1":{"type":"string"},"data2":{"type":"string"},"data3":{"type":"string"},"data4":{"type":"string"},"data5":{"type":"string"},"data6":{"type":"string"},"data7":{"type":"string"},"data8":{"type":"string"},"data9":{"type":"string"},"data10":{"type":"string"},"data11":{"type":"string"},"data12":{"type":"string"},"data13":{"type":"string"},"data14":{"type":"string"},"data15":{"type":"string"}}}}},"required":["type","rows"]}"""
    };

    private static readonly LlmTool UpdateCustomDataTool = new()
    {
        Name = "update_custom_data",
        Description = "按 Id 批量编辑自定义数据行：每行必带 id，仅覆盖显式传入的 data 列，未传列保留原值。单次最多 200 行。",
        ParametersJson = """{"type":"object","properties":{"rows":{"type":"array","maxItems":200,"description":"编辑行列表（每行必带 id）","items":{"type":"object","properties":{"id":{"type":"string"},"data1":{"type":"string"},"data2":{"type":"string"},"data3":{"type":"string"},"data4":{"type":"string"},"data5":{"type":"string"},"data6":{"type":"string"},"data7":{"type":"string"},"data8":{"type":"string"},"data9":{"type":"string"},"data10":{"type":"string"},"data11":{"type":"string"},"data12":{"type":"string"},"data13":{"type":"string"},"data14":{"type":"string"},"data15":{"type":"string"}}}}},"required":["rows"]}"""
    };

    private static readonly LlmTool DeleteCustomDataTool = new()
    {
        Name = "delete_custom_data",
        Description = "删除自定义数据行：按 ids 删除（删除前自动 JSON 备份到 logs/deleteCustomData，单次最多 200 条），或按 type + clear=true 整类型清空（高危：无备份、不可恢复）。两种模式互斥。",
        ParametersJson = """{"type":"object","properties":{"ids":{"type":"array","maxItems":200,"items":{"type":"string"},"description":"要删除的行 Id 列表"},"type":{"type":"string","description":"clear 模式必填：数据类型键"},"clear":{"type":"boolean","description":"true=整类型清空该类型全部数据（需同时传 type）"}}}"""
    };

    /// <summary>全部工具名（写工具 + list_tasks 辅助）——工具识别白名单</summary>
    internal static readonly HashSet<string> AllToolNames =
    [
        "delete_script", "save_task", "delete_task", "list_tasks",
        "save_env", "delete_env",
        "save_custom_data_title", "delete_custom_data_title", "save_custom_data",
        "update_custom_data", "delete_custom_data"
    ];

    /// <summary>计入单 run 写操作次数上限的工具（list_tasks 只读不计）</summary>
    internal static readonly HashSet<string> WriteToolNames =
    [
        "delete_script", "save_task", "delete_task",
        "save_env", "delete_env",
        "save_custom_data_title", "delete_custom_data_title", "save_custom_data",
        "update_custom_data", "delete_custom_data"
    ];

    /// <summary>按权限快照拼装工具声明（全关时返回空列表——工具完全不出现在下发给 LLM 的清单里）。</summary>
    public static List<LlmTool> BuildTools(AiAgentWritePermissions permissions)
    {
        var list = new List<LlmTool>();
        if (permissions == null)
        {
            return list;
        }
        if (permissions.AllowScriptDelete)
        {
            list.Add(DeleteScriptTool);
        }
        if (permissions.AllowTaskManage)
        {
            list.Add(SaveTaskTool);
            list.Add(DeleteTaskTool);
        }
        if (permissions.AllowEnvManage)
        {
            list.Add(SaveEnvTool);
            list.Add(DeleteEnvTool);
        }
        if (permissions.AllowCustomDataManage)
        {
            list.Add(SaveCustomDataTitleTool);
            list.Add(DeleteCustomDataTitleTool);
            list.Add(SaveCustomDataTool);
            list.Add(UpdateCustomDataTool);
            list.Add(DeleteCustomDataTool);
        }
        // list_tasks：任一写开关开启才装配（改任务前拿 Id 的正向入口；纯诊断场景 get_task_info/list_scripts 已够）
        if (permissions.AnyEnabled)
        {
            list.Add(ListTasksTool);
        }
        return list;
    }

    // ==================================================================== 执行

    /// <summary>
    /// 执行一个写工具。返回 null 表示该名字不在本工具集（上层回落到「不支持的工具」）。
    /// 双保险校验：即便旧工具列表已随首轮请求下发，执行前仍按库中最新设置 + 触发源白名单重新判定。
    /// </summary>
    public async Task<string> TryInvokeAsync(string name, JObject args, AiAgentWritePermissions snapshot,
        string conversationId, CancellationToken ct = default)
    {
        if (!AllToolNames.Contains(name))
        {
            return null;
        }
        args ??= [];
        var shortConversation = string.IsNullOrEmpty(conversationId) ? "-" : conversationId[..Math.Min(8, conversationId.Length)];
        var latestSetting = await _db.AiSettings.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == AiSettingModel.DefaultId, ct);
        var effective = AiAgentWritePermissions.Resolve(latestSetting, snapshot?.TriggerType);
        if (!effective.IsAllowed(name))
        {
            // 越权尝试值得留痕：可能是开关运行中被关、非 Manual 触发的 run，也可能是提示注入诱导
            LogServiceHelper.Warn($"AI 写操作被拒：{name}",
                $"会话 {shortConversation} 调用 {name}：写权限未开启或本次运行不允许写操作（触发源 {snapshot?.TriggerType ?? "-"}）",
                "AI", "AI", LogType.AI助手);
            return $"写操作已拒绝：{name} 需要对应的写权限开关开启，且本次运行必须由用户在会话里主动发起。请改用只读工具，或让用户在「供应商与模型」页确认写权限设置。";
        }
        // run 共享同一个 scoped DbContext：上次写操作的实体 SaveChanges 后仍被跟踪，
        // 底层服务（TaskService/CustomDataTitleService 等）按「分离态实体 + db.Update」写入，
        // 残留跟踪会让同一 run 的第二次同键写入撞「another instance with the same key」——先按域解除跟踪
        DetachFor(name);
        try
        {
            return name switch
            {
                "delete_script" => await DeleteScriptAsync(args, shortConversation, ct),
                "save_task" => await SaveTaskAsync(args, shortConversation, ct),
                "delete_task" => await DeleteTaskAsync(args, shortConversation, ct),
                "list_tasks" => await ListTasksAsync(ct),
                "save_env" => await SaveEnvAsync(args, shortConversation),
                "delete_env" => await DeleteEnvAsync(args, shortConversation),
                "save_custom_data_title" => await SaveCustomDataTitleAsync(args, shortConversation),
                "delete_custom_data_title" => await DeleteCustomDataTitleAsync(args, shortConversation),
                "save_custom_data" => await SaveCustomDataAsync(args, shortConversation),
                "update_custom_data" => await UpdateCustomDataAsync(args, shortConversation),
                "delete_custom_data" => await DeleteCustomDataAsync(args, shortConversation),
                _ => null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            LogServiceHelper.Warn($"AI 写操作失败：{name}",
                $"会话 {shortConversation} 调用 {name}：{e.Message}", "AI", "AI", LogType.AI助手);
            return $"操作失败：{e.Message}";
        }
    }

    // ==================================================================== A. 脚本删除

    /// <summary>
    /// 删除脚本（计划 §4.2 A）：作用域恒为 scripts/quantum、引用护栏、版本表 force 留底、留底失败即拒删。
    /// </summary>
    private async Task<string> DeleteScriptAsync(JObject args, string conversation, CancellationToken ct)
    {
        var fileName = Text(args, "fileName");
        var reason = Text(args, "reason");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "fileName 不能为空";
        }
        var normalized = fileName.Replace('\\', '/');
        if (normalized.TrimStart('/').StartsWith(AgentTestRunService.StagingDir + "/", StringComparison.OrdinalIgnoreCase))
        {
            return "该目录是 AI 试运行的影子文件区，不允许删除。";
        }
        var path = SafeFile.Resolve("./scripts/quantum", fileName);
        if (path == null)
        {
            return $"脚本路径非法：{fileName}";
        }
        if (!File.Exists(path))
        {
            return $"脚本不存在：{fileName}（可用 list_scripts 确认文件名）";
        }

        // 引用护栏：被任务引用（含已禁用）即拒绝。EF 翻译不了 OrdinalIgnoreCase，任务量小拉全表内存比对（与 list_scripts 口径一致，防大小写差异绕过）
        var tasks = await _db.Tasks.AsNoTracking().ToListAsync(ct);
        var referrers = tasks
            .Where(n => string.Equals((n.FileName ?? string.Empty).Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (referrers.Count > 0)
        {
            var detail = string.Join("、", referrers.Select(n => $"{n.Name}{(n.Enable ? string.Empty : "(已禁用)")}"));
            LogServiceHelper.Warn("AI 写操作被拒：删除脚本",
                $"会话 {conversation} 删除脚本 {normalized}：被 {referrers.Count} 个任务引用（{detail}）", "AI", "AI", LogType.AI助手);
            return $"该脚本被以下任务引用，请先让用户解绑或删除任务后再删：{detail}";
        }

        // 先留底后删除：force 绕过 RecordAsync 哈希去重（被删内容通常即最新版本）；返回 null = 留底失败（DB/IO 异常被吞），此时不得删除
        var content = await File.ReadAllTextAsync(path, ct);
        var version = await _scriptVersionService.RecordQuietlyAsync(normalized, content, ScriptVersionSource.AgentDelete,
            string.IsNullOrWhiteSpace(reason) ? "AI 删除留底" : $"AI 删除留底：{reason}", "AI", force: true);
        if (version == null)
        {
            LogServiceHelper.Warn("AI 写操作被拒：删除脚本",
                $"会话 {conversation} 删除脚本 {normalized}：版本留底失败，已拒绝删除", "AI", "AI", LogType.AI助手);
            return "版本留底失败，已拒绝删除（脚本未动）。请稍后重试或让用户手动删除。";
        }

        _taskService.DeleteScript(fileName);
        LogServiceHelper.Info("AI 写操作：删除脚本",
            $"会话 {conversation} 删除脚本 {normalized}：已删除，内容已留底（版本行 {version.Id[..8]}，AgentDelete 来源）", "AI", "AI", LogType.AI助手);
        return $"已删除 {normalized}，删除前内容已留底（AgentDelete 版本行，可从脚本版本页回滚恢复）。注意：本地恢复也可从顶层 scripts 同步源重新同步。";
    }

    // ==================================================================== B. 任务管理

    /// <summary>
    /// 新增/编辑任务（计划 §4.2 B）：编辑按 JObject 键存在性合并（bool/int 不可空，「非 null」判不出未传）；
    /// manager/communicationTypes 不暴露、恒回填库中原值；taskSubs 不传时回填现有子任务（防全删重插清空）。
    /// </summary>
    private async Task<string> SaveTaskAsync(JObject args, string conversation, CancellationToken ct)
    {
        var id = Text(args, "id");
        var hasId = !string.IsNullOrWhiteSpace(id);
        if (!hasId)
        {
            return await AddTaskAsync(args, conversation);
        }

        var existing = await _db.Tasks.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (existing == null)
        {
            return $"任务不存在（Id={id}），请用 list_tasks 确认后重试。";
        }

        // 读现状回填全部字段（含不暴露的 Manager/CommunicationTypes），再按原始键存在性覆盖显式传入的字段
        var save = new TaskSaveModel
        {
            Id = existing.Id,
            Name = existing.Name,
            Command = existing.Command,
            CommandEnv = existing.CommandEnv,
            Cron = existing.Cron,
            FileName = existing.FileName,
            TextToPicture = existing.TextToPicture,
            EnableRegex = existing.EnableRegex,
            Enable = existing.Enable,
            DayLimit = existing.DayLimit,
            EnablePush = existing.EnablePush,
            PushGroup = existing.PushGroup,
            Revocation = existing.Revocation,
            Manager = existing.Manager,
            WaitTime = existing.WaitTime,
            TaskStartNotify = existing.TaskStartNotify,
            TaskEndNotify = existing.TaskEndNotify,
            Remark = existing.Remark,
            CommunicationTypes = existing.CommunicationTypes,
            EnableProxy = existing.EnableProxy,
            SessionName = existing.SessionName
        };
        var changed = new List<string>();
        void Override(string key, Action set)
        {
            if (!args.ContainsKey(key))
            {
                return;
            }
            set();
            changed.Add(char.ToUpperInvariant(key[0]) + key[1..]);
        }

        Override("name", () => save.Name = Text(args, "name"));
        Override("command", () => save.Command = Text(args, "command"));
        Override("commandEnv", () => save.CommandEnv = Text(args, "commandEnv"));
        Override("cron", () => save.Cron = Text(args, "cron"));
        Override("fileName", () => save.FileName = Text(args, "fileName"));
        Override("enable", () => save.Enable = Bool(args, "enable") ?? save.Enable);
        Override("enableRegex", () => save.EnableRegex = Bool(args, "enableRegex") ?? save.EnableRegex);
        Override("dayLimit", () => save.DayLimit = Int(args, "dayLimit") ?? save.DayLimit);
        Override("enablePush", () => save.EnablePush = Bool(args, "enablePush") ?? save.EnablePush);
        Override("pushGroup", () => save.PushGroup = Bool(args, "pushGroup") ?? save.PushGroup);
        Override("revocation", () => save.Revocation = Bool(args, "revocation") ?? save.Revocation);
        Override("waitTime", () => save.WaitTime = Int(args, "waitTime") ?? save.WaitTime);
        Override("taskStartNotify", () => save.TaskStartNotify = Text(args, "taskStartNotify"));
        Override("taskEndNotify", () => save.TaskEndNotify = Text(args, "taskEndNotify"));
        Override("sessionName", () => save.SessionName = Text(args, "sessionName"));
        Override("textToPicture", () => save.TextToPicture = Bool(args, "textToPicture") ?? save.TextToPicture);
        Override("enableProxy", () => save.EnableProxy = Bool(args, "enableProxy") ?? save.EnableProxy);
        Override("remark", () => save.Remark = Text(args, "remark"));

        // 换绑脚本：与新增同规，必须真实存在（ValidateScriptFileName 只验形状，服务层不兜底）
        if (args.ContainsKey("fileName") && !string.IsNullOrWhiteSpace(save.FileName))
        {
            var error = ValidateScriptExists(save.FileName);
            if (error != null)
            {
                return error;
            }
        }

        // taskSubs：传入即全量替换（UpdateAsync 本就是全删重插语义）；不传必须回填库中现有子任务，否则会被清空。
        // TaskSubs 是 [NotMapped]，GetByIdAsync 不填充——按 TaskId 从 CacheManager 取（与 UpdateAsync 自身取数口径一致）
        if (args.ContainsKey("taskSubs"))
        {
            save.TaskSubs = ParseSubs(args["taskSubs"]);
            changed.Add("TaskSubs");
        }
        else
        {
            save.TaskSubs = CacheManager.Get<TaskSubModel>()
                .Where(n => n.TaskId == existing.Id)
                .OrderBy(n => n.Sort)
                .Select(n => new TaskSubSaveModel
                {
                    Name = n.Name,
                    EnableRegex = n.EnableRegex,
                    Command = n.Command,
                    Sort = n.Sort,
                    CommandEnv = n.CommandEnv,
                    Revocation = n.Revocation,
                    WaitTime = n.WaitTime,
                    Remark = n.Remark
                }).ToList();
        }

        await _taskService.UpdateAsync(save);
        LogServiceHelper.Info("AI 写操作：编辑任务",
            $"会话 {conversation} 编辑任务 {existing.Name}（{existing.Id}）：{(changed.Count == 0 ? "无字段变化" : string.Join("、", changed))}", "AI", "AI", LogType.AI助手);
        return $"已更新任务「{existing.Name}」：{(changed.Count == 0 ? "无字段变化（未传入任何可修改字段）" : string.Join("、", changed))}";
    }

    private async Task<string> AddTaskAsync(JObject args, string conversation)
    {
        var name = Text(args, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "新增任务必须提供 name（任务名称）。";
        }
        var fileName = Text(args, "fileName");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "新增任务必须提供 fileName（scripts/quantum 下的脚本相对路径）。";
        }
        var error = ValidateScriptExists(fileName);
        if (error != null)
        {
            return error;
        }
        var save = new TaskSaveModel
        {
            Name = name,
            Command = Text(args, "command"),
            CommandEnv = Text(args, "commandEnv"),
            Cron = Text(args, "cron"),
            FileName = fileName,
            TextToPicture = Bool(args, "textToPicture") ?? false,
            EnableRegex = Bool(args, "enableRegex") ?? false,
            Enable = Bool(args, "enable") ?? true,
            DayLimit = Int(args, "dayLimit") ?? 0,
            EnablePush = Bool(args, "enablePush") ?? false,
            PushGroup = Bool(args, "pushGroup") ?? false,
            Revocation = Bool(args, "revocation") ?? false,
            // AI 不可制造管理员专属任务；CommunicationTypes 是已移除旧通道的遗留字段，恒 null
            Manager = false,
            WaitTime = Int(args, "waitTime") ?? 0,
            TaskStartNotify = Text(args, "taskStartNotify"),
            TaskEndNotify = Text(args, "taskEndNotify"),
            Remark = Text(args, "remark"),
            CommunicationTypes = null,
            EnableProxy = Bool(args, "enableProxy") ?? false,
            SessionName = Text(args, "sessionName"),
            TaskSubs = args.ContainsKey("taskSubs") ? ParseSubs(args["taskSubs"]) : null
        };
        await _taskService.AddAsync(save);
        LogServiceHelper.Info("AI 写操作：新增任务",
            $"会话 {conversation} 新增任务 {save.Name}：绑定脚本 {save.FileName}，启用 {save.Enable}", "AI", "AI", LogType.AI助手);
        return $"已新增任务「{save.Name}」（绑定 {save.FileName}）。请向用户确认指令/Cron 等关键配置是否符合预期。";
    }

    /// <summary>fileName 换绑/新增时的存在性校验（ValidateScriptFileName 只验路径形状不查文件存在，服务层不兜底）。</summary>
    private static string ValidateScriptExists(string fileName)
    {
        var path = SafeFile.Resolve("./scripts/quantum", fileName);
        if (path == null)
        {
            return $"脚本路径非法：{fileName}";
        }
        if (!File.Exists(path))
        {
            return $"脚本文件不存在：{fileName}（必须位于 scripts/quantum 下且真实存在，可用 list_scripts 确认）。";
        }
        return null;
    }

    /// <summary>
    /// 删除任务（计划 §4.2 B）：删除前完整 TaskModel（含子任务集合）JSON 备份到 logs/deleteTasks。
    /// </summary>
    private async Task<string> DeleteTaskAsync(JObject args, string conversation, CancellationToken ct)
    {
        var ids = StringList(args, "ids");
        if (ids.Count == 0)
        {
            return "ids 不能为空";
        }
        if (ids.Count > MaxBatchIds)
        {
            return $"单次最多删除 {MaxBatchIds} 个任务，请分批操作（本次收到 {ids.Count} 个）。";
        }
        var tasks = await _db.Tasks.AsNoTracking().Where(n => ids.Contains(n.Id)).ToListAsync(ct);
        if (tasks.Count == 0)
        {
            return "没有找到对应 Id 的任务（可能已被删除），请用 list_tasks 确认。";
        }
        // 子任务组装：TaskSubs 是 [NotMapped]，GetByIdAsync 不填充——按 TaskId 直查 t_task_sub，备份才完整
        var foundIds = tasks.Select(n => n.Id).ToList();
        var subs = await _db.TaskSubs.AsNoTracking().Where(n => foundIds.Contains(n.TaskId)).ToListAsync(ct);
        foreach (var task in tasks)
        {
            task.TaskSubs = subs.Where(n => n.TaskId == task.Id).OrderBy(n => n.Sort).ToList();
        }
        var backup = Path.Combine("logs", DeleteTasksLogDir, $"{DateTime.Now:yyyyMMddHHmmssfff}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await File.WriteAllTextAsync(backup, string.Join(Environment.NewLine, tasks.Select(JsonConvert.SerializeObject)), ct);

        await _taskService.DeleteAsync(string.Join(",", foundIds));
        var names = string.Join("、", tasks.Select(n => n.Name));
        LogServiceHelper.Info("AI 写操作：删除任务",
            $"会话 {conversation} 删除任务 {names}：{tasks.Count} 个（含子任务 {subs.Count} 条），备份 {Path.GetFileName(backup)}", "AI", "AI", LogType.AI助手);
        return $"已删除 {tasks.Count} 个任务：{names}。删除前完整配置已备份到 logs/{DeleteTasksLogDir}/{Path.GetFileName(backup)}。";
    }

    /// <summary>任务摘要清单（只读辅助；AI 改任务前需要拿到 Id）。</summary>
    private async Task<string> ListTasksAsync(CancellationToken ct)
    {
        var tasks = await _db.Tasks.AsNoTracking().OrderBy(n => n.Name).ToListAsync(ct);
        if (tasks.Count == 0)
        {
            return "(平台没有任何任务)";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"共 {tasks.Count} 个任务（Id | 名称 | 指令 | Cron | 启用 | 脚本 | 会话名）：");
        foreach (var task in tasks)
        {
            sb.AppendLine($"{task.Id} | {task.Name} | {task.Command ?? "-"} | {task.Cron ?? "-"} | {(task.Enable ? "启用" : "禁用")} | {task.FileName} | {task.SessionName ?? "-"}");
        }
        return AgentToolbox.Truncate(sb.ToString());
    }

    // ==================================================================== C. 环境变量

    /// <summary>
    /// 新增/编辑环境变量（计划 §4.2 C）。编辑走「查库→改→存→失效」（CacheManager 写路径约定，
    /// 不复用 EnvService.Save 的缓存匹配更新——缓存陈旧时会把编辑错走成新增重复行）；
    /// 新增走 EnvService.Save（同名多值并存语义）。
    /// </summary>
    private async Task<string> SaveEnvAsync(JObject args, string conversation)
    {
        var name = Text(args, "name");
        var hasValue = args.ContainsKey("value");
        var value = Text(args, "value");
        if (!TryGetId(args, out var id))
        {
            // 新增：name/value 必填
            if (string.IsNullOrWhiteSpace(name))
            {
                return "新增环境变量必须提供 name。";
            }
            if (!hasValue)
            {
                return "新增环境变量必须提供 value。";
            }
            if (await _db.Envs.AsNoTracking().AnyAsync(n => n.Name == name && n.Value == value))
            {
                return $"同名同值的环境变量已存在，未重复写入：{name}（如需更新请用 list_envs 查到该条 Id 后按 Id 编辑）。";
            }
            var model = new EnvModelPostModel
            {
                Name = name,
                Value = value,
                Remark = Text(args, "remark"),
                Enable = Bool(args, "enable") ?? true,
                UpdateTime = DateTime.Now
            };
            await _envService.Save([model]);
            LogServiceHelper.Info("AI 写操作：新增环境变量",
                $"会话 {conversation} 新增环境变量 {DescribeEnv(name, value)}", "AI", "AI", LogType.AI助手);
            return $"已新增环境变量 {name}（值长度 {value?.Length ?? 0}）。同名多值并存：同账号多凭据就加多条同名变量，平台执行时按 & 合并投递。";
        }

        // 编辑：读现状合并（名称变更同样过正则校验）
        var entity = await _db.Envs.FirstOrDefaultAsync(n => n.Id == id);
        if (entity == null)
        {
            return $"环境变量不存在（Id={id}），请用 list_envs 确认。";
        }
        var changed = new List<string>();
        if (args.ContainsKey("name") && !string.IsNullOrWhiteSpace(name))
        {
            entity.Name = name;
            changed.Add("Name");
        }
        if (hasValue)
        {
            entity.Value = value;
            changed.Add("Value");
        }
        if (args.ContainsKey("remark"))
        {
            entity.Remark = Text(args, "remark");
            changed.Add("Remark");
        }
        if (args.ContainsKey("enable"))
        {
            entity.Enable = Bool(args, "enable") ?? entity.Enable;
            changed.Add("Enable");
        }
        if (!RegexHelper.Code(entity.Name))
        {
            return "环境变量名称不合法：只能字母开头、仅含字母数字下划线、最长 64。";
        }
        entity.UpdateTime = DateTime.Now;
        await _db.SaveChangesAsync();
        CacheManager.Refresh<EnvModel>();
        LogServiceHelper.Info("AI 写操作：编辑环境变量",
            $"会话 {conversation} 编辑环境变量 {DescribeEnv(entity.Name, entity.Value)}：{string.Join("、", changed)}", "AI", "AI", LogType.AI助手);
        return $"已更新环境变量 {entity.Name}（{string.Join("、", changed)}）。";
    }

    /// <summary>env 审计脱敏：只记变量名 + 值长度，明文值不进系统日志。</summary>
    private static string DescribeEnv(string name, string value)
        => $"{name} (len={value?.Length ?? 0})";

    private async Task<string> DeleteEnvAsync(JObject args, string conversation)
    {
        var ids = StringList(args, "ids");
        if (ids.Count == 0)
        {
            return "ids 不能为空";
        }
        if (ids.Count > MaxBatchIds)
        {
            return $"单次最多删除 {MaxBatchIds} 条环境变量，请分批操作（本次收到 {ids.Count} 条）。";
        }
        var envs = await _db.Envs.AsNoTracking().Where(n => ids.Contains(n.Id)).ToListAsync();
        if (envs.Count == 0)
        {
            return "没有找到对应 Id 的环境变量，请用 list_envs 确认。";
        }
        // EnvService.Delete 自带删前 JSON 备份（logs/deleteEnvs）+ 操作日志
        await _envService.Delete(ids);
        var names = string.Join("、", envs.Select(n => n.Name));
        LogServiceHelper.Info("AI 写操作：删除环境变量",
            $"会话 {conversation} 删除环境变量 {names}：{envs.Count} 条（已自动备份到 logs/deleteEnvs）", "AI", "AI", LogType.AI助手);
        return $"已删除 {envs.Count} 条环境变量：{names}。删除前已自动备份到 logs/deleteEnvs，误删可从备份恢复。";
    }

    // ==================================================================== D. CustomData

    /// <summary>新增/编辑自定义数据类型表头（按 Type upsert，未传标题列保留原值）。</summary>
    private async Task<string> SaveCustomDataTitleAsync(JObject args, string conversation)
    {
        var type = Text(args, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return "type 不能为空";
        }
        var existing = await _db.CustomDataTitles.AsNoTracking().FirstOrDefaultAsync(n => n.Type == type);
        var isNew = existing == null;
        var model = existing ?? new CustomDataTitleModel { Type = type };
        var changed = new List<string>();
        if (args.ContainsKey("typeName"))
        {
            model.TypeName = Text(args, "typeName");
            changed.Add("TypeName");
        }
        if (isNew && string.IsNullOrWhiteSpace(model.TypeName))
        {
            return "新增类型必须提供 typeName（类型显示名）。";
        }
        if (args.ContainsKey("hide"))
        {
            model.Hide = Bool(args, "hide") ?? model.Hide;
            changed.Add("Hide");
        }
        var titles = args["titles"] as JObject;
        if (titles != null)
        {
            for (var i = 1; i <= 15; i++)
            {
                var key = $"title{i}";
                if (!titles.ContainsKey(key))
                {
                    continue;
                }
                typeof(CustomDataTitleModel).GetProperty($"Title{i}")?.SetValue(model, Str(titles, key));
                changed.Add($"Title{i}");
            }
        }
        await _customDataTitleService.AddOrUpdate(model);
        LogServiceHelper.Info("AI 写操作：保存数据类型",
            $"会话 {conversation} {(isNew ? "新增" : "编辑")}数据类型 {type}（{model.TypeName}）：{string.Join("、", changed)}", "AI", "AI", LogType.AI助手);
        return $"已{(isNew ? "新增" : "更新")}数据类型 {type}（{model.TypeName}）：{string.Join("、", changed)}。";
    }

    /// <summary>
    /// 删除自定义数据类型（计划 §4.2 D）：既有 DeleteAsync 的 deleteData 是死参数（数据恒被清空），
    /// 工具不暴露该参数；删除前把标题定义 JSON + 数据条数记操作日志（数据不做全量备份，删除后无从恢复）。
    /// </summary>
    private async Task<string> DeleteCustomDataTitleAsync(JObject args, string conversation)
    {
        var type = Text(args, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return "type 不能为空";
        }
        var title = await _db.CustomDataTitles.AsNoTracking().FirstOrDefaultAsync(n => n.Type == type);
        if (title == null)
        {
            return $"数据类型不存在：{type}";
        }
        var count = await _db.CustomDatas.AsNoTracking().CountAsync(n => n.Type == type);
        LogServiceHelper.Info("AI 写操作：删除数据类型",
            $"会话 {conversation} 删除数据类型 {type}（{title.TypeName}）：定义 {JsonConvert.SerializeObject(title)}；连带清空数据 {count} 行（无备份）", "AI", "AI", LogType.AI助手);
        await _customDataTitleService.DeleteAsync(type, deleteData: true);
        return $"已删除数据类型 {type}（{title.TypeName}）及其全部 {count} 行数据。高危操作：标题定义已记入系统日志，数据无备份、删除后无从恢复。";
    }

    /// <summary>批量新增数据行（Type 必填，rows 上限 200）。</summary>
    private async Task<string> SaveCustomDataAsync(JObject args, string conversation)
    {
        var type = Text(args, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return "type 不能为空";
        }
        var rows = args["rows"] as JArray;
        if (rows == null || rows.Count == 0)
        {
            return "rows 不能为空";
        }
        if (rows.Count > MaxCustomDataRows)
        {
            return $"单次最多 {MaxCustomDataRows} 行，请分批操作（本次收到 {rows.Count} 行）。";
        }
        var models = rows.Select(r => new CustomDataModel
        {
            Type = type,
            Data1 = Str(r, "data1"),
            Data2 = Str(r, "data2"),
            Data3 = Str(r, "data3"),
            Data4 = Str(r, "data4"),
            Data5 = Str(r, "data5"),
            Data6 = Str(r, "data6"),
            Data7 = Str(r, "data7"),
            Data8 = Str(r, "data8"),
            Data9 = Str(r, "data9"),
            Data10 = Str(r, "data10"),
            Data11 = Str(r, "data11"),
            Data12 = Str(r, "data12"),
            Data13 = Str(r, "data13"),
            Data14 = Str(r, "data14"),
            Data15 = Str(r, "data15")
        }).ToList();
        await _customDataService.AddAsync(models);
        LogServiceHelper.Info("AI 写操作：新增自定义数据",
            $"会话 {conversation} 类型 {type} 新增 {models.Count} 行", "AI", "AI", LogType.AI助手);
        return $"已向类型 {type} 新增 {models.Count} 行数据。";
    }

    /// <summary>按 Id 批量编辑数据行：读-合并-写（仅覆盖显式传入列，未传列保留——EF 整行 Update 会把未设值列抹 null）。</summary>
    private async Task<string> UpdateCustomDataAsync(JObject args, string conversation)
    {
        var rows = args["rows"] as JArray;
        if (rows == null || rows.Count == 0)
        {
            return "rows 不能为空";
        }
        if (rows.Count > MaxCustomDataRows)
        {
            return $"单次最多 {MaxCustomDataRows} 行，请分批操作（本次收到 {rows.Count} 行）。";
        }
        var merged = new List<CustomDataModel>();
        var missing = new List<string>();
        foreach (var row in rows)
        {
            var id = Str(row, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return "每行都必须带 id（按 Id 编辑）。";
            }
            // 取现状（含全部列值），仅覆盖显式传入列
            var current = await _db.CustomDatas.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id);
            if (current == null)
            {
                missing.Add(id);
                continue;
            }
            for (var i = 1; i <= 15; i++)
            {
                var key = $"data{i}";
                if (row is JObject obj && obj.ContainsKey(key))
                {
                    // JObject 键是 data1（小写 d），CLR 属性是 Data1——GetProperty 默认大小写敏感
                    typeof(CustomDataModel).GetProperty($"Data{i}")?.SetValue(current, Str(row, key));
                }
            }
            merged.Add(current);
        }
        if (missing.Count > 0)
        {
            return $"以下行 Id 不存在，本次未更新任何行：{string.Join("、", missing)}（可用 query_custom_data 核对）";
        }
        await _customDataService.UpdatesAsync(merged);
        LogServiceHelper.Info("AI 写操作：编辑自定义数据",
            $"会话 {conversation} 编辑 {merged.Count} 行（首个类型 {merged[0].Type}）", "AI", "AI", LogType.AI助手);
        return $"已更新 {merged.Count} 行数据（仅覆盖显式传入的列，其余列保留原值）。";
    }

    /// <summary>删除数据行（ids 模式，删前文件备份）或整类型清空（clear 模式，高危无备份）；两模式互斥。</summary>
    private async Task<string> DeleteCustomDataAsync(JObject args, string conversation)
    {
        var hasIds = args.ContainsKey("ids") && args["ids"] is JArray;
        var hasClear = args.ContainsKey("clear") && Bool(args, "clear") == true;
        if (hasIds && hasClear)
        {
            return "ids 与 clear 互斥：按行删除传 ids，整类型清空传 type + clear=true，不能同时传。";
        }
        if (hasIds)
        {
            var ids = StringList(args, "ids");
            if (ids.Count == 0)
            {
                return "ids 不能为空";
            }
            if (ids.Count > MaxCustomDataRows)
            {
                return $"单次最多删除 {MaxCustomDataRows} 行，请分批操作（本次收到 {ids.Count} 行）。";
            }
            var rows = await _db.CustomDatas.AsNoTracking().Where(n => ids.Contains(n.Id)).ToListAsync();
            if (rows.Count == 0)
            {
                return "没有找到对应 Id 的数据行（可能已被删除）。";
            }
            // 批量行留底走文件（对齐 env/tasks 模式），不往日志 remark 塞大 JSON
            var backup = Path.Combine("logs", DeleteCustomDataLogDir, $"{DateTime.Now:yyyyMMddHHmmssfff}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await File.WriteAllTextAsync(backup, string.Join(Environment.NewLine, rows.Select(JsonConvert.SerializeObject)));
            await _customDataService.DeleteAsync(ids);
            LogServiceHelper.Info("AI 写操作：删除自定义数据",
                $"会话 {conversation} 删除 {rows.Count} 行（类型 {rows[0].Type} 等），备份 {Path.GetFileName(backup)}", "AI", "AI", LogType.AI助手);
            return $"已删除 {rows.Count} 行数据，删除前行内容已备份到 logs/{DeleteCustomDataLogDir}/{Path.GetFileName(backup)}。";
        }
        if (hasClear)
        {
            var type = Text(args, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                return "clear 模式必须同时传 type。";
            }
            var count = await _db.CustomDatas.AsNoTracking().CountAsync(n => n.Type == type);
            LogServiceHelper.Warn("AI 写操作：清空自定义数据",
                $"会话 {conversation} 清空类型 {type} 全部数据：{count} 行（无备份、不可恢复）", "AI", "AI", LogType.AI助手);
            await _customDataService.ClearAsync(type);
            return $"已清空类型 {type} 的全部 {count} 行数据。高危操作：无备份、删除后无从恢复，请向用户明确汇报。";
        }
        return "参数不完整：按行删除传 ids，整类型清空传 type + clear=true。";
    }

    // ==================================================================== 辅助

    /// <summary>写工具执行前按域解除变更跟踪（run 共享 scoped DbContext，防同键双跟踪）。</summary>
    private void DetachFor(string name)
    {
        switch (name)
        {
            case "save_task" or "delete_task" or "list_tasks":
                DetachTracked<TaskModel>();
                DetachTracked<TaskSubModel>();
                break;
            case "save_env" or "delete_env":
                DetachTracked<EnvModel>();
                break;
            case "save_custom_data_title" or "delete_custom_data_title":
                DetachTracked<CustomDataTitleModel>();
                DetachTracked<MenuModel>();
                break;
            case "save_custom_data" or "update_custom_data" or "delete_custom_data":
                DetachTracked<CustomDataModel>();
                break;
        }
    }

    private void DetachTracked<T>() where T : BaseModel
    {
        foreach (var entry in _db.ChangeTracker.Entries<T>().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool TryGetId(JObject args, out string id)
    {
        id = Text(args, "id");
        return !string.IsNullOrWhiteSpace(id);
    }

    /// <summary>字符串列表参数（ids 类）：容忍 null 元素与空白。</summary>
    private static List<string> StringList(JObject args, string key)
        => args[key] is JArray arr
            ? arr.Where(n => n.Type == JTokenType.String).Select(n => n.Value<string>().Trim()).Where(n => n.Length > 0).ToList()
            : [];

    private static List<TaskSubSaveModel> ParseSubs(JToken token)
    {
        if (token is not JArray arr)
        {
            return null;
        }
        return arr.Select(r => new TaskSubSaveModel
        {
            Name = Str(r, "name"),
            EnableRegex = Bool(r, "enableRegex") ?? false,
            Command = Str(r, "command"),
            Sort = Int(r, "sort") ?? 0,
            CommandEnv = Str(r, "commandEnv"),
            Revocation = Bool(r, "revocation") ?? false,
            WaitTime = Int(r, "waitTime") ?? 0,
            Remark = Str(r, "remark")
        }).ToList();
    }

    /// <summary>取字符串值：字符串 trim、JSON null 返回 null（「未传」与「显式传 null 清空」由外层 ContainsKey 区分）、标量转字符串。</summary>
    private static string Str(JToken token, string key)
    {
        var value = token?[key];
        if (value == null || value.Type == JTokenType.Null)
        {
            return null;
        }
        return value.Type == JTokenType.String ? value.Value<string>()?.Trim() : value.ToString();
    }

    private static string Text(JObject args, string key) => Str(args, key);

    private static int? Int(JToken token, string key)
        => token?[key]?.Type is JTokenType.Integer or JTokenType.Float ? (int)token[key] : null;

    /// <summary>三态 bool：未传/JSON null → null（区分「未传」与「显式传 false」的关键）。</summary>
    private static bool? Bool(JToken token, string key)
    {
        var value = token?[key];
        if (value == null || value.Type == JTokenType.Null)
        {
            return null;
        }
        if (value.Type == JTokenType.Boolean)
        {
            return value.Value<bool>();
        }
        return value.Type == JTokenType.String && bool.TryParse(value.Value<string>(), out var parsed) ? parsed : null;
    }
}
