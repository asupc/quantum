using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// AI 供应商（2026-09-20 新增，AI 脚本修复 Agent 计划阶段二）：OpenAI 兼容协议的服务商配置。
/// ApiKey 明文入库（与平台现状 t_env.Value 同口径），但对外接口只回掩码。
/// </summary>
[Table("t_ai_provider")]
public class AiProviderModel : BaseModel
{
    /// <summary>显示名（如 DeepSeek）</summary>
    public string Name { get; set; }

    /// <summary>Base URL（如 https://api.deepseek.com/v1；裸域名保存时自动补 /v1）</summary>
    public string BaseUrl { get; set; }

    /// <summary>
    /// 协议方言（见 <see cref="AiProtocol"/>）：openai=chat/completions（默认，绝大多数供应商）、
    /// anthropic=Messages API、responses=OpenAI Responses API。上层（Agent/工具/提案）与协议无关。
    /// </summary>
    public string Protocol { get; set; } = AiProtocol.OpenAi;

    /// <summary>API 密钥（接口只回掩码，编辑留空表示不改）</summary>
    public string ApiKey { get; set; }

    /// <summary>是否走平台代理（容器内 HTTP_PROXY 等，与任务脚本 ctx.Http 的代理开关同源）</summary>
    public bool UsePlatformProxy { get; set; }

    /// <summary>单次请求超时（秒）。流式=openai 方言的空闲超时（响应头等待与相邻数据块间隔的上限，
    /// 长生成总时长不受限，由全局「单次运行时限」兜底）；非流式=请求总时长上限</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>网络异常/5xx/429 的重试次数（超时与 400 不重试）</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>是否启用</summary>
    public bool Enable { get; set; } = true;

    /// <summary>排序（小在前）</summary>
    public int Sort { get; set; }

    /// <summary>工具调用能力（见 <see cref="AiToolSupport"/>；探测结果，可手工覆盖）</summary>
    public int SupportsTools { get; set; }

    /// <summary>最近一次连通性测试时间</summary>
    public DateTime? LastTestTime { get; set; }

    /// <summary>最近一次测试是否通过</summary>
    public bool? LastTestOk { get; set; }

    /// <summary>最近一次测试的结论文案</summary>
    public string LastTestMessage { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? UpdateTime { get; set; }
}

/// <summary>
/// AI 模型（供应商下的具体模型；用户要求的「配置上下文大小」落在这里）
/// </summary>
[Table("t_ai_model")]
public class AiModelModel : BaseModel
{
    /// <summary>所属供应商 Id</summary>
    public string ProviderId { get; set; }

    /// <summary>模型标识（请求体 model 字段，如 deepseek-chat）</summary>
    public string ModelId { get; set; }

    /// <summary>显示名（留空则用 ModelId）</summary>
    public string DisplayName { get; set; }

    /// <summary>上下文窗口（token），上下文预算裁剪与超长判定用</summary>
    public int ContextWindow { get; set; } = 32768;

    /// <summary>单次输出上限（token）</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>采样温度</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>是否启用（未启用的模型不出现在 Agent 选择器里）</summary>
    public bool Enable { get; set; } = true;

    /// <summary>是否默认模型（全局仅一个）</summary>
    public bool IsDefault { get; set; }

    /// <summary>排序（小在前）</summary>
    public int Sort { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? UpdateTime { get; set; }
}

/// <summary>
/// AI 全局设置（单行，Id 固定 "default"；不比 appsettings——这些要能在 Web 上改且不重启）
/// </summary>
[Table("t_ai_setting")]
public class AiSettingModel : BaseModel
{
    /// <summary>总开关（关闭或没有可用供应商/模型时 AI 页面显示引导态）</summary>
    public bool Enable { get; set; } = true;

    /// <summary>Agent 单次运行最大轮数（一轮 = 一次模型请求）</summary>
    public int MaxRounds { get; set; } = 8;

    /// <summary>Agent 单次运行总时限（秒）</summary>
    public int RunTimeoutSeconds { get; set; } = 600;

    /// <summary>分析脚本失败时默认读取的日志尾部行数</summary>
    public int LogTailLines { get; set; } = 200;

    /// <summary>任务执行异常时是否自动发起分析（默认关：只通知，用户一键进入）</summary>
    public bool AutoAnalyzeOnFailure { get; set; }

    /// <summary>试运行推送策略（见 <see cref="AiTestRunNotifyMode"/>）</summary>
    public string TestRunNotifyMode { get; set; } = AiTestRunNotifyMode.Task;

    /// <summary>试运行前是否需要人工确认（默认关：Agent 自动试运行）</summary>
    public bool TestRunRequireConfirm { get; set; }

    /// <summary>脚本版本每文件保留数（0 表示用服务默认值）</summary>
    public int KeepVersionsPerFile { get; set; } = 30;

    /// <summary>是否把每次运行的完整请求/响应对落盘到 logs/ai（排查用，默认关）</summary>
    public bool SaveFullPrompt { get; set; }

    /// <summary>追加到系统提示的自定义要求（如「本平台脚本注释用中文」）</summary>
    public string SystemPromptExtra { get; set; }

    /// <summary>
    /// 【高危】允许 AI 直接删除任务脚本文件（默认关；删除前自动记 AgentDelete 版本留底，可从脚本版本页恢复）。
    /// 仅对用户在会话里主动发起（Manual 触发）的运行生效，失败自动分析等无人值守运行恒只读。
    /// </summary>
    public bool AllowScriptDelete { get; set; }

    /// <summary>
    /// 【高危】允许 AI 添加/修改/删除任务（默认关；编辑按字段合并不清空未传字段，删除前 JSON 备份到 logs/deleteTasks）。
    /// 仅对用户在会话里主动发起（Manual 触发）的运行生效。
    /// </summary>
    public bool AllowTaskManage { get; set; }

    /// <summary>
    /// 【高危】允许 AI 添加/编辑/删除环境变量（默认关；写入的值必然流经模型与供应商，请仅在对可信内容分析时开启）。
    /// 仅对用户在会话里主动发起（Manual 触发）的运行生效。
    /// </summary>
    public bool AllowEnvManage { get; set; }

    /// <summary>
    /// 【高危】允许 AI 增删改 CustomDataTitle 与 CustomData 数据（默认关；行删除有文件备份，标题删除与整类型清空无备份）。
    /// 仅对用户在会话里主动发起（Manual 触发）的运行生效。
    /// </summary>
    public bool AllowCustomDataManage { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? UpdateTime { get; set; }

    /// <summary>单行设置的固定主键</summary>
    public const string DefaultId = "default";
}

/// <summary>
/// 协议方言（供应商级可选，2026-09-20 追加）：
/// 三种方言只在 LlmClient 的「请求组装 + 响应解析」里分支，工具抽象（LlmTool/LlmToolCall）与
/// Agent 循环、工具集、提案链路全部方言无关。
/// </summary>
public static class AiProtocol
{
    /// <summary>OpenAI 兼容 chat/completions（默认；DeepSeek/通义/智谱/Kimi/OpenRouter/Ollama/绝大多数中转）</summary>
    public const string OpenAi = "openai";

    /// <summary>Anthropic Messages API（POST /v1/messages；x-api-key + anthropic-version）</summary>
    public const string Anthropic = "anthropic";

    /// <summary>OpenAI Responses API（POST /v1/responses；input/output 项式载荷）</summary>
    public const string Responses = "responses";

    /// <summary>取值归一化：未知/空 → openai（存量数据无需迁移即按默认协议工作）。</summary>
    public static string Normalize(string protocol)
    {
        return protocol switch
        {
            Anthropic => Anthropic,
            Responses => Responses,
            _ => OpenAi
        };
    }
}

/// <summary>供应商的工具调用能力三态</summary>
public static class AiToolSupport
{
    /// <summary>未知（未探测或探测无定论）：运行时按工具协议尝试</summary>
    public const int Unknown = 0;

    /// <summary>支持 function calling</summary>
    public const int Supported = 1;

    /// <summary>不支持：运行时回退 JSON 协议</summary>
    public const int Unsupported = 2;
}

/// <summary>试运行推送策略</summary>
public static class AiTestRunNotifyMode
{
    /// <summary>按任务配置（EnablePush 沿用任务值，默认）</summary>
    public const string Task = "task";

    /// <summary>强制开启推送（让用户务必看到通知效果）</summary>
    public const string Force = "force";

    /// <summary>静默（不发通知，只留日志）</summary>
    public const string Silent = "silent";
}
