using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 单次执行终态。只有在脚本真正结束后才允许出现这里面的任何一个值——
/// 「HTTP 受理成功」「日志已排队」都不是终态。
/// </summary>
public enum TaskExecutionOutcome
{
    /// <summary>脚本正常返回且未收到取消信号</summary>
    Succeeded = 0,

    /// <summary>脚本抛出异常</summary>
    Failed = 1,

    /// <summary>脚本根本没跑起来：路径非法/文件缺失/扩展名不支持/读取失败/门禁拒绝/编译失败/加载失败</summary>
    Rejected = 2,

    /// <summary>协作取消生效（停机信号或 ForceEndTime），含脚本响应取消后自行返回</summary>
    Canceled = 3,

    /// <summary>进程重启后扫描到的残留 Running，真实结果不可知</summary>
    Interrupted = 4
}

/// <summary>
/// 受控失败码。数据库、通知与 AI 输入只携带码 + 安全摘要，不携带堆栈明文。
/// </summary>
public enum TaskFailureCode
{
    None = 0,
    InvalidScriptPath = 1,
    ScriptMissing = 2,
    UnsupportedScriptExtension = 3,
    ScriptReadFailed = 4,
    GateBlocked = 5,
    CompileFailed = 6,
    AssemblyLoadFailed = 7,
    ScriptException = 8,
    CanceledByShutdown = 9,
    CanceledByForceEndTime = 10,
    EngineFault = 11
}

/// <summary>执行来源。第一期自动重试只作用于有持久任务 Id 的 Manual/Cron。</summary>
public enum TaskTriggerSource
{
    /// <summary>Web/App 手动执行</summary>
    Manual = 0,

    /// <summary>Quartz 定时触发</summary>
    Cron = 1,

    /// <summary>聊天指令触发</summary>
    Command = 2,

    /// <summary>外触内执</summary>
    OpenTrigger = 3,

    /// <summary>AI 影子试运行（永不触发生产重试/告警）</summary>
    Shadow = 4,

    /// <summary>失败策略自动重试（沿用根执行的来源，单独留痕用）</summary>
    Retry = 5
}

/// <summary>运行记录状态机：Pending → Running → 终态（后五个）。</summary>
public enum TaskRunStatus
{
    /// <summary>已受理，尚未被后台领取</summary>
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Rejected = 4,
    Canceled = 5,

    /// <summary>进程重启后残留的 Running：真实结果不可知，绝不重放也不判成功</summary>
    Interrupted = 6
}

/// <summary>
/// 单次执行运行记录（一期 G2）：每次尝试一行，与 t_log 的「日志行」区分——
/// 本表是可索引、可分页、可判终态的执行事实，日志文件仍是全文载体。
/// TaskId 不做级联删除：任务删除后历史要按快照可读，但不得反查引用到别的任务。
/// </summary>
[Table("t_task_run")]
public class TaskRunModel : BaseModel
{
    /// <summary>根执行 Id（同一根执行的首次尝试 Id 等于自身；重试尝试共享根 Id）</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string RootRunId { get; set; }

    /// <summary>第几次尝试，1 起</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>关联任务 Id（可空：影子试运行/已删除任务只留快照）</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string TaskId { get; set; }

    /// <summary>任务名快照（任务删除或改名后仍能正确显示历史）</summary>
    public string TaskNameSnapshot { get; set; }

    /// <summary>脚本相对路径快照</summary>
    public string ScriptFileSnapshot { get; set; }

    /// <summary>本次执行的脚本内容哈希（无版本记录时为空）</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string ScriptHashSnapshot { get; set; }

    public TaskTriggerSource TriggerSource { get; set; }

    /// <summary>触发方脱敏标识（指令触发的用户 Id 哈希、外触的 secret 前缀等；绝不存令牌明文）</summary>
    public string TriggerRef { get; set; }

    public TaskRunStatus Status { get; set; } = TaskRunStatus.Pending;

    public TaskFailureCode FailureCode { get; set; } = TaskFailureCode.None;

    /// <summary>已脱敏、已截断的安全摘要（入库的唯一失败信息，堆栈只在日志文件里）</summary>
    public string SafeSummary { get; set; }

    /// <summary>执行侧日志目录名快照（与 t_log.DirectoryName 同口径，供日志未落库时兜底定位）</summary>
    public string LogDirectoryName { get; set; }

    /// <summary>执行侧日志文件名快照</summary>
    public string LogFileName { get; set; }

    /// <summary>关联 t_log 行 Id（预分配；日志行与本记录同事务落库后才算可用）</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string LogId { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>到期重试时刻（仅 Failed 且策略允许时非空）</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>任务 Manager 快照（R-02）：任务删除后仍能按管理员可见性拦截历史读取</summary>
    public bool ManagerSnapshot { get; set; }

    /// <summary>是否重试尝试（便于列表一眼区分，不依赖 Attempt 推断）</summary>
    public bool IsRetry { get; set; }

    /// <summary>取消/中止原因（如「脚本已变更，取消自动重试」）</summary>
    public string CancelReason { get; set; }

    /// <summary>因同任务并发而被顺延的次数（超过上限即放弃重试，见 R-04 互斥口径）</summary>
    public int RetryDeferrals { get; set; }

    /// <summary>创建（受理）时间，UTC；列表排序键</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 任务失败策略（一期 G3）：一任务一行，缺行等同于「全默认」——
/// 存量未配置的任务因此行为不变（只跑一次、不重试）。
/// </summary>
[Table("t_task_failure_policy")]
public class TaskFailurePolicyModel : BaseModel
{
    /// <summary>任务 Id（主键口径：一任务一条策略）</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string TaskId { get; set; }

    /// <summary>重试次数上限 0~3，默认 0（0 = 关闭自动重试）</summary>
    public int RetryCount { get; set; }

    /// <summary>退避基数秒 30~3600，默认 60；第 n 次重试按 min(基数 × 2^(n-1), 3600)</summary>
    public int BackoffSeconds { get; set; } = 60;

    /// <summary>连续最终失败达到该次数才告警，1~10，默认 1</summary>
    public int AlertAfterConsecutiveFailures { get; set; } = 1;

    /// <summary>是否发送恢复通知，默认关闭</summary>
    public bool SendRecovery { get; set; }

    /// <summary>告警冷却分钟 0~1440，默认 60</summary>
    public int CooldownMinutes { get; set; } = 60;

    /// <summary>策略总开关，默认关闭</summary>
    public bool Enabled { get; set; }

    /// <summary>更新时间</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>最后修改人</summary>
    public string UpdatedBy { get; set; }
}

/// <summary>告警事件类型</summary>
public enum TaskAlertType
{
    /// <summary>连续最终失败达到阈值，告警打开</summary>
    FailureOpened = 0,

    /// <summary>告警打开后首次成功，恢复</summary>
    Recovered = 1
}

/// <summary>告警事件投递状态</summary>
public enum TaskAlertDeliveryStatus
{
    /// <summary>待投递</summary>
    Pending = 0,

    /// <summary>已投递</summary>
    Sent = 1,

    /// <summary>投递失败（后台按幂等键重试）</summary>
    Failed = 2
}

/// <summary>
/// 任务告警状态（一期 G4）：连续失败计数与「是否处于告警中」的持久事实。
/// 必须在数据库里，否则重启后计数归零会造成告警风暴或漏报。
/// </summary>
[Table("t_task_alert_state")]
public class TaskAlertStateModel : BaseModel
{
    /// <summary>任务 Id（唯一）</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string TaskId { get; set; }

    /// <summary>连续最终失败次数（同一 RootRunId 的多次尝试只计一次）</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>告警是否处于打开状态</summary>
    public bool Open { get; set; }

    public DateTime? LastSentAtUtc { get; set; }

    /// <summary>最近一次终态运行 Id（幂等判定用）</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string LastFinalRunId { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 任务告警事件（一期 G4）：一条事件一行，投递状态随行记录，后台按事件幂等投递。
/// 唯一键 (TaskId, AlertType, RootRunId) 是「同一根执行不重复开/恢复」的硬约束。
/// </summary>
[Table("t_task_alert_event")]
public class TaskAlertEventModel : BaseModel
{
    [Column(TypeName = "nvarchar(64)")]
    public string TaskId { get; set; }

    /// <summary>触发事件的根执行 Id</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string RootRunId { get; set; }

    public TaskAlertType AlertType { get; set; }

    /// <summary>产生事件的那次运行 Id</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string RunId { get; set; }

    /// <summary>当时的连续失败次数（快照，便于回看）</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>投递用安全摘要（已脱敏）</summary>
    public string SafeSummary { get; set; }

    public TaskAlertDeliveryStatus DeliveryStatus { get; set; } = TaskAlertDeliveryStatus.Pending;

    /// <summary>投递尝试次数（超限后停投并留痕）</summary>
    public int DeliveryAttempts { get; set; }

    /// <summary>投递失败原因（脱敏）</summary>
    public string DeliveryError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? DeliveredAtUtc { get; set; }
}
