using Quantum.Entities.Model;

namespace Quantum.Entities.DTOs;

/// <summary>
/// AI 供应商列表项（密钥只回掩码，明文走显式 reveal 动作）
/// </summary>
public class AiProviderItem
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string BaseUrl { get; set; }

    /// <summary>协议方言（见 AiProtocol：openai / anthropic / responses）</summary>
    public string Protocol { get; set; }

    /// <summary>掩码后的密钥（如 sk-1****abcd）</summary>
    public string ApiKeyMasked { get; set; }

    /// <summary>是否已配置密钥</summary>
    public bool HasApiKey { get; set; }

    public bool UsePlatformProxy { get; set; }

    public int TimeoutSeconds { get; set; }

    public int MaxRetries { get; set; }

    public bool Enable { get; set; }

    public int Sort { get; set; }

    /// <summary>工具调用能力三态（见 AiToolSupport）</summary>
    public int SupportsTools { get; set; }

    public DateTime? LastTestTime { get; set; }

    public bool? LastTestOk { get; set; }

    public string LastTestMessage { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? UpdateTime { get; set; }

    /// <summary>该供应商下的模型（含禁用的，便于一次拉全）</summary>
    public List<AiModelModel> Models { get; set; } = [];
}

/// <summary>
/// AI 供应商保存入参（ApiKey 留空表示保持原密钥不变）
/// </summary>
public class AiProviderSaveModel
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string BaseUrl { get; set; }

    /// <summary>协议方言（不传按 openai；见 AiProtocol）</summary>
    public string Protocol { get; set; }

    public string ApiKey { get; set; }

    public bool UsePlatformProxy { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    public int MaxRetries { get; set; } = 1;

    public bool Enable { get; set; } = true;

    public int Sort { get; set; }

    /// <summary>工具能力三态手工覆盖（-1 表示不改动探测结果）</summary>
    public int SupportsTools { get; set; } = -1;
}

/// <summary>
/// 供应商连通性测试结果
/// </summary>
public class AiTestResult
{
    public bool Ok { get; set; }

    public long LatencyMs { get; set; }

    /// <summary>工具能力探测结论（见 AiToolSupport）</summary>
    public int SupportsTools { get; set; }

    /// <summary>拉取到的模型标识（部分供应商无 /models 端点则为空）</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>结论/错误文案</summary>
    public string Message { get; set; }
}

/// <summary>
/// 模型批量导入（从供应商 /models 勾选导入）
/// </summary>
public class AiModelImportModel
{
    public string ProviderId { get; set; }

    public List<string> ModelIds { get; set; } = [];
}

/// <summary>
/// Agent 会话发起一次运行
/// </summary>
public class AiChatRequest
{
    public string ConversationId { get; set; }

    /// <summary>用户输入（自然语言；可含脚本文件名）</summary>
    public string Content { get; set; }

    /// <summary>显式指定的目标脚本（相对 scripts/quantum 根；可空，留空则从 Content 里识别 .cs）</summary>
    public string TargetFile { get; set; }
}

/// <summary>
/// Agent 运行状态（前端轮询）
/// </summary>
public class AiRunView
{
    public string Id { get; set; }

    public string ConversationId { get; set; }

    public string Status { get; set; }

    public string TargetFile { get; set; }

    public string Model { get; set; }

    public int Rounds { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public long DurationMs { get; set; }

    public string Error { get; set; }

    /// <summary>最近一步的进度文案（如「调用 read_script」「试运行中」），供页面状态条展示</summary>
    public string Progress { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? FinishTime { get; set; }
}

/// <summary>
/// 提案卡载荷（挂在消息 Payload 上，前端渲染应用/试运行/忽略按钮）
/// </summary>
public class AiProposalView
{
    public string Id { get; set; }

    public string FileName { get; set; }

    public string Summary { get; set; }

    public string Status { get; set; }

    public string TestStatus { get; set; }

    public string TestRunLogId { get; set; }

    public string BaseHash { get; set; }

    public string NewHash { get; set; }

    /// <summary>门禁/编译诊断（JSON：Blocked/Errors/Warnings）</summary>
    public string Diagnostics { get; set; }

    public string AppliedVersionId { get; set; }

    public DateTime CreateTime { get; set; }
}
