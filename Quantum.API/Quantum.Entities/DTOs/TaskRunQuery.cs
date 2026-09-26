using Quantum.Entities.Model;

namespace Quantum.Entities.DTOs;

/// <summary>执行历史分页查询条件</summary>
public class TaskRunQuery
{
    /// <summary>按任务过滤（可空）</summary>
    public string TaskId { get; set; }

    /// <summary>按终态过滤（可空）</summary>
    public TaskRunStatus? Status { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 20;

    /// <summary>回溯天数（默认 90 天，与服务端保留口径一致）</summary>
    public int Days { get; set; } = 90;
}

/// <summary>执行历史单行（不含 SafeSummary 之外的任何脚本原文）</summary>
public class TaskRunRow
{
    public string Id { get; set; }
    public string RootRunId { get; set; }
    public int Attempt { get; set; }
    public string TaskId { get; set; }
    public string TaskName { get; set; }
    public string ScriptFile { get; set; }
    public string TriggerSource { get; set; }
    public string Status { get; set; }
    public string FailureCode { get; set; }
    public string SafeSummary { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsRetry { get; set; }
    public string CancelReason { get; set; }

    /// <summary>耗时毫秒（起止时间齐了才算得出来，Pending/Running 为 null）</summary>
    public long? ElapsedMs => StartedAtUtc != null && FinishedAtUtc != null
        ? (long)(FinishedAtUtc.Value - StartedAtUtc.Value).TotalMilliseconds
        : null;

    public static TaskRunRow From(TaskRunModel run) => new()
    {
        Id = run.Id,
        RootRunId = run.RootRunId,
        Attempt = run.Attempt,
        TaskId = run.TaskId,
        TaskName = run.TaskNameSnapshot,
        ScriptFile = run.ScriptFileSnapshot,
        TriggerSource = run.TriggerSource.ToString(),
        Status = run.Status.ToString(),
        FailureCode = run.FailureCode.ToString(),
        SafeSummary = run.SafeSummary,
        StartedAtUtc = run.StartedAtUtc,
        FinishedAtUtc = run.FinishedAtUtc,
        NextAttemptAtUtc = run.NextAttemptAtUtc,
        CreatedAtUtc = run.CreatedAtUtc,
        IsRetry = run.IsRetry,
        CancelReason = run.CancelReason
    };
}

/// <summary>执行详情：单行 + 同一根执行的全部尝试 + 日志可达性</summary>
public class TaskRunDetail
{
    public TaskRunRow Run { get; set; }

    /// <summary>时间轴：同一根执行（含每次重试）按 Attempt 升序</summary>
    public List<TaskRunRow> Attempts { get; set; } = [];

    /// <summary>关联日志行是否仍可读（false 表示日志已清理或尚未落库，前端只显示占位）</summary>
    public bool LogAvailable { get; set; }

    /// <summary>受限日志行 Id：详情接口只回这个 Id，不回任意文件路径</summary>
    public string LogId { get; set; }
}

/// <summary>失败策略读写体（服务端逐项 clamp，UI 仅辅助）</summary>
public class TaskFailurePolicyDto
{
    public string TaskId { get; set; }

    /// <summary>重试次数 0~3（0 即关闭自动重试）</summary>
    public int RetryCount { get; set; }

    /// <summary>退避基数秒 30~3600</summary>
    public int BackoffSeconds { get; set; } = 60;

    /// <summary>连续最终失败达该次数才告警，1~10</summary>
    public int AlertAfterConsecutiveFailures { get; set; } = 1;

    /// <summary>是否发送恢复通知</summary>
    public bool SendRecovery { get; set; }

    /// <summary>告警冷却分钟 0~1440</summary>
    public int CooldownMinutes { get; set; } = 60;

    /// <summary>策略总开关（默认关闭）</summary>
    public bool Enabled { get; set; }
}

/// <summary>批量执行受理回执项（新端点 execute-runs 的返回体）</summary>
public class TaskExecuteReceipt
{
    public string TaskId { get; set; }
    public string RunId { get; set; }
}
