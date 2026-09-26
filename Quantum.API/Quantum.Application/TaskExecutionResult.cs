using System.Text;
using System.Text.RegularExpressions;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 任务执行结构化结果（一期 G1）：取代「写日志后 return」的隐式成功口径。
/// 由 <see cref="TaskExcuteService"/> 产出，所有入口（手动/定时/指令/外触/AI 影子试运行）消费同一份结论，
/// 不允许再出现按日志文本猜测结果的第二套判定。
/// </summary>
public sealed class TaskExecutionResult
{
    /// <summary>安全摘要入库/入通知长度上限（字符）</summary>
    public const int MaxSummaryLength = 512;

    public TaskExecutionOutcome Outcome { get; init; }

    public TaskFailureCode FailureCode { get; init; } = TaskFailureCode.None;

    /// <summary>已脱敏、已截断的人类可读摘要；仅用于列表/通知，详细堆栈只在受权限控制的日志文件里</summary>
    public string SafeSummary { get; init; }

    public DateTime StartedAtUtc { get; init; }

    public DateTime FinishedAtUtc { get; init; }

    /// <summary>日志目录名（t_log.DirectoryName 口径）</summary>
    public string LogDirectoryName { get; init; }

    /// <summary>日志文件名（t_log.LogPath 口径）</summary>
    public string LogFileName { get; init; }

    public bool IsSuccess => Outcome == TaskExecutionOutcome.Succeeded;

    /// <summary>是否属于「真实失败」：参与失败计数与告警。取消与被拒绝不计入重试，但被拒绝要让人看见。</summary>
    public bool IsFinalFailure => Outcome is TaskExecutionOutcome.Failed or TaskExecutionOutcome.Rejected;

    public TimeSpan Duration => FinishedAtUtc >= StartedAtUtc ? FinishedAtUtc - StartedAtUtc : TimeSpan.Zero;

    /// <summary>日志定位信息是否可用（供调用方决定是否暴露详情链接）</summary>
    public bool HasLogLocation => !string.IsNullOrEmpty(LogDirectoryName) && !string.IsNullOrEmpty(LogFileName);

    public static TaskExecutionResult Succeeded(DateTime startedAtUtc, DateTime finishedAtUtc,
        string logDirectoryName, string logFileName)
        => new()
        {
            Outcome = TaskExecutionOutcome.Succeeded,
            StartedAtUtc = startedAtUtc,
            FinishedAtUtc = finishedAtUtc,
            LogDirectoryName = logDirectoryName,
            LogFileName = logFileName
        };

    public static TaskExecutionResult Failed(TaskFailureCode code, string summary, DateTime startedAtUtc,
        DateTime finishedAtUtc, string logDirectoryName, string logFileName)
        => Build(TaskExecutionOutcome.Failed, code, summary, startedAtUtc, finishedAtUtc, logDirectoryName, logFileName);

    public static TaskExecutionResult Rejected(TaskFailureCode code, string summary, DateTime startedAtUtc,
        DateTime finishedAtUtc, string logDirectoryName, string logFileName)
        => Build(TaskExecutionOutcome.Rejected, code, summary, startedAtUtc, finishedAtUtc, logDirectoryName, logFileName);

    public static TaskExecutionResult Canceled(TaskFailureCode code, string summary, DateTime startedAtUtc,
        DateTime finishedAtUtc, string logDirectoryName, string logFileName)
        => Build(TaskExecutionOutcome.Canceled, code, summary, startedAtUtc, finishedAtUtc, logDirectoryName, logFileName);

    public static TaskExecutionResult Interrupted(string summary, DateTime startedAtUtc, DateTime finishedAtUtc,
        string logDirectoryName = null, string logFileName = null)
        => Build(TaskExecutionOutcome.Interrupted, TaskFailureCode.None, summary, startedAtUtc, finishedAtUtc,
            logDirectoryName, logFileName);

    private static TaskExecutionResult Build(TaskExecutionOutcome outcome, TaskFailureCode code, string summary,
        DateTime startedAtUtc, DateTime finishedAtUtc, string logDirectoryName, string logFileName)
        => new()
        {
            Outcome = outcome,
            FailureCode = code,
            SafeSummary = SanitizeSummary(summary),
            StartedAtUtc = startedAtUtc,
            FinishedAtUtc = finishedAtUtc,
            LogDirectoryName = logDirectoryName,
            LogFileName = logFileName
        };

    /// <summary>终态的短名，用于日志/通知标题，不含任何脚本原文</summary>
    public string OutcomeLabel => Outcome switch
    {
        TaskExecutionOutcome.Succeeded => "成功",
        TaskExecutionOutcome.Failed => "执行异常",
        TaskExecutionOutcome.Rejected => "已拒绝执行",
        TaskExecutionOutcome.Canceled => "已取消",
        _ => "中断/未知"
    };

    /// <summary>终态对应的日志级别</summary>
    public LogSeverity Severity => Outcome switch
    {
        TaskExecutionOutcome.Succeeded => LogSeverity.Info,
        TaskExecutionOutcome.Canceled => LogSeverity.Warn,
        _ => LogSeverity.Error
    };

    /// <summary>
    /// 把终态回填到 t_log 行（手动/定时/指令/外触四条链路共用同一份口径）。
    /// **必须在执行结束后调用、且必须在入队之前调用**：日志线程每 3 秒排空一次，
    /// 先入队会让落库线程捞到初始的 Success 默认值。
    /// </summary>
    public void ApplyToLog(LogModel log)
    {
        if (log == null)
        {
            return;
        }

        log.Success = IsSuccess;
        log.Severity = Severity;
        log.Module = "Task";
        log.ElapsedMs = (long)Duration.TotalMilliseconds;
        if (HasLogLocation)
        {
            // 日志定位以执行侧为准回填：调用方各自再算一遍 LogDirNameFrom 就是第二套口径，早晚会分叉
            log.DirectoryName = LogDirectoryName;
            log.LogPath = LogFileName;
        }
        if (!IsSuccess)
        {
            log.Remark = $"{log.Remark} → {Outcome}/{FailureCode}：{SafeSummary}";
        }
    }

    private static readonly Regex CredentialPrefixPattern =
        new(@"\b(Bearer|Basic|Digest|Token)\s+[A-Za-z0-9\-._~+/]{6,}={0,3}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>裸 JWT（三段式）常出现在被透传的请求头摘要里</summary>
    private static readonly Regex JwtPattern =
        new(@"\beyJ[A-Za-z0-9\-._~+/]{5,}(?:\.[A-Za-z0-9\-._~+/]{2,})+", RegexOptions.Compiled);

    private static readonly Regex SecretAssignPattern =
        new(@"(?<key>\w*(?:password|passwd|secret|token|cookie|apikey|api_key|accesskey|access_key|authorization)\w*)\s*[:=]\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]{1,512})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 摘要脱敏：控制字符压成空格 → 遮蔽凭据前缀/JWT/密钥型赋值 → 遮蔽当前启用的环境变量明文值 → 截断。
    /// 环境变量明文兜底是关键一条：脚本异常消息常把 ctx.Env 读到的值直接拼进去。
    /// </summary>
    public static string SanitizeSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }

        var flat = builder.ToString().Trim();
        flat = CredentialPrefixPattern.Replace(flat, m => $"{m.Groups[1].Value} ***");
        flat = JwtPattern.Replace(flat, "***");
        flat = SecretAssignPattern.Replace(flat, m => $"{m.Groups["key"].Value}=***");
        flat = RedactEnvValues(flat);
        return flat.Length > MaxSummaryLength ? flat[..MaxSummaryLength] : flat;
    }

    private static string RedactEnvValues(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            foreach (var env in CacheManager.Get<EnvModel>())
            {
                if (!env.Enable || string.IsNullOrEmpty(env.Value) || env.Value.Length < 8)
                {
                    continue;
                }

                text = text.Replace(env.Value, "***", StringComparison.Ordinal);
            }
        }
        catch
        {
            // 缓存不可用（如单测未预热）时不阻断脱敏主链路，前两道正则仍然生效
        }

        return text;
    }
}
