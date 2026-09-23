using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// AI Agent 会话（2026-09-20 新增，AI 脚本修复 Agent 计划阶段三）：
/// 与任务会话（t_chat_session/t_chat_message）完全分离——Agent 消息需要代码块/差异/按钮渲染，
/// 且不应污染会话合并、未读计数与来源会话迁移逻辑。
/// </summary>
[Table("t_ai_conversation")]
public class AiConversationModel : BaseModel
{
    /// <summary>会话标题（首条用户消息前 20 字自动生成，可改）</summary>
    public string Title { get; set; }

    /// <summary>
    /// 本会话是否允许把环境变量「值」发给模型（默认 false：只给变量名与备注；
    /// 值里是 Cookie/密码一类的凭据，需要时由用户显式开启并留痕）
    /// </summary>
    public bool AllowEnvValues { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? UpdateTime { get; set; }

    /// <summary>最后一条消息时间（会话列表排序用）</summary>
    public DateTime? LastMessageTime { get; set; }
}

/// <summary>
/// AI Agent 消息（面向用户的消息流：Web 页面渲染的就是它；工具调用轨迹落 t_ai_step）
/// </summary>
[Table("t_ai_message")]
public class AiMessageModel : BaseModel
{
    public string ConversationId { get; set; }

    /// <summary>会话内自增序号（前端按 afterSeq 增量轮询）</summary>
    public long Seq { get; set; }

    /// <summary>user / assistant / status</summary>
    public string Role { get; set; }

    /// <summary>text / proposal / test_result / error</summary>
    public string Kind { get; set; }

    public string Content { get; set; }

    /// <summary>结构化载荷（提案卡/试运行结果等，JSON）</summary>
    public string Payload { get; set; }

    /// <summary>所属运行 Id（用户消息为空）</summary>
    public string RunId { get; set; }

    public DateTime CreateTime { get; set; }
}

/// <summary>
/// 一次 Agent 运行（一轮对话触发的完整工具循环）
/// </summary>
[Table("t_ai_run")]
public class AiRunModel : BaseModel
{
    public string ConversationId { get; set; }

    /// <summary>触发方式（见 <see cref="AiRunTrigger"/>）</summary>
    public string TriggerType { get; set; }

    /// <summary>目标脚本（相对 scripts/quantum 根；由用户消息解析或显式指定，可空）</summary>
    public string TargetFile { get; set; }

    public string ProviderId { get; set; }

    public string Model { get; set; }

    /// <summary>Running / Succeeded / Failed / WaitingConfirm / Cancelled</summary>
    public string Status { get; set; }

    /// <summary>已用轮数（一轮 = 一次模型请求）</summary>
    public int Rounds { get; set; }

    /// <summary>
    /// 提示 token（取自响应 usage）：只用于校准上下文估算器与单次运行排查，
    /// 不做聚合统计、不做限额（2026-09-20 用户决定）
    /// </summary>
    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public long DurationMs { get; set; }

    public string Error { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? FinishTime { get; set; }
}

/// <summary>
/// Agent 运行的过程轨迹（工具调用/结果/模型文本），只供页面「过程面板」展开查看，不推给 App
/// </summary>
[Table("t_ai_step")]
public class AiStepModel : BaseModel
{
    public string RunId { get; set; }

    public int Seq { get; set; }

    /// <summary>assistant_text / tool_call / tool_result / error</summary>
    public string Kind { get; set; }

    /// <summary>工具名（Kind=tool_* 时）</summary>
    public string Name { get; set; }

    /// <summary>工具参数或模型原文</summary>
    public string Arguments { get; set; }

    /// <summary>工具结果（截断入库）</summary>
    public string Result { get; set; }

    public long DurationMs { get; set; }

    public DateTime CreateTime { get; set; }
}

/// <summary>
/// 待确认的脚本修复提案（Agent 的唯一产出形态：不落盘，等用户点「应用」）
/// </summary>
[Table("t_ai_proposal")]
public class AiProposalModel : BaseModel
{
    public string ConversationId { get; set; }

    /// <summary>承载提案卡的消息 Id</summary>
    public string MessageId { get; set; }

    public string FileName { get; set; }

    /// <summary>生成提案时的脚本哈希：应用前比对，变了即判过期（防覆盖人工改动）</summary>
    public string BaseHash { get; set; }

    public string NewContent { get; set; }

    public string NewHash { get; set; }

    /// <summary>改动摘要（模型给出的说明）</summary>
    public string Summary { get; set; }

    /// <summary>生成时的门禁/编译诊断（JSON：Blocked/Errors/Warnings）</summary>
    public string Diagnostics { get; set; }

    /// <summary>试运行状态（见 <see cref="AiTestStatus"/>）</summary>
    public string TestStatus { get; set; }

    /// <summary>试运行日志的 t_log Id（走既有日志详情端点查看全文）</summary>
    public string TestRunLogId { get; set; }

    /// <summary>Pending / Applied / Discarded / Stale</summary>
    public string Status { get; set; }

    /// <summary>应用后生成的脚本版本 Id</summary>
    public string AppliedVersionId { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? AppliedTime { get; set; }
}

/// <summary>Agent 运行触发方式</summary>
public static class AiRunTrigger
{
    /// <summary>用户在会话里发起</summary>
    public const string Manual = "manual";

    /// <summary>任务执行失败自动发起（阶段五，默认关）</summary>
    public const string AutoFailure = "auto";

    public const string Retry = "retry";
}

/// <summary>Agent 运行状态</summary>
public static class AiRunStatus
{
    public const string Running = "Running";

    public const string Succeeded = "Succeeded";

    public const string Failed = "Failed";

    /// <summary>已产出提案，等用户确认</summary>
    public const string WaitingConfirm = "WaitingConfirm";

    public const string Cancelled = "Cancelled";
}

/// <summary>提案试运行状态</summary>
public static class AiTestStatus
{
    public const string None = "None";

    public const string Running = "Running";

    public const string Passed = "Passed";

    public const string Failed = "Failed";

    public const string Timeout = "Timeout";
}

/// <summary>提案状态</summary>
public static class AiProposalStatus
{
    public const string Pending = "Pending";

    public const string Applied = "Applied";

    /// <summary>用户忽略</summary>
    public const string Discarded = "Discarded";

    /// <summary>基线脚本已被改动（防覆盖）</summary>
    public const string Stale = "Stale";
}

/// <summary>Agent 消息角色/类型</summary>
public static class AiMessageRole
{
    public const string User = "user";

    public const string Assistant = "assistant";

    /// <summary>运行状态行（开始分析/调用工具/试运行中等）</summary>
    public const string Status = "status";
}

public static class AiMessageKind
{
    public const string Text = "text";

    public const string Proposal = "proposal";

    public const string TestResult = "test_result";

    public const string Error = "error";
}
