using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 单次运行记录服务（一期 G2/G3）：负责 t_task_run 的受理、领取、终态落库、到期重试领取与查询。
///
/// 三条硬口径：
/// 1. **受理即写 Pending 并返回 RunId**，后台再领取为 Running（数据库条件更新，天然幂等）；
/// 2. **终态与 t_log 行同事务同步落库**（日志行 Id 预分配），不走 3 秒批量队列——
///    否则会出现「记录说失败、日志说成功」或详情链接指向还不存在的行（审核项 R-01）；
/// 3. 数据库写失败绝不降级成成功：留系统错误并停用该条自动重试。
/// </summary>
public class TaskRunService
{
    /// <summary>列表分页上限（防大页拖库）</summary>
    public const int MaxPageSize = 100;

    /// <summary>重试退避上限秒数</summary>
    public const int MaxBackoffSeconds = 3600;

    /// <summary>同任务在跑时，到期重试最多顺延几次后放弃</summary>
    internal const int MaxConcurrencyDeferrals = 20;

    /// <summary>同任务在跑时的顺延步长（测试可临时调小）</summary>
    internal static TimeSpan ConcurrencyDeferral = TimeSpan.FromSeconds(30);

    /// <summary>启动扫描判定 Running 残留的宽限期（避开正在执行、尚未写完终态的窗口）</summary>
    internal static TimeSpan RunningGracePeriod = TimeSpan.FromMinutes(2);

    /// <summary>RetryCount 上限：0~3，0 即关闭自动重试</summary>
    public const int MaxRetryCount = 3;

    private readonly IQuantumDbContext _db;
    private readonly ILogger<TaskRunService> _log;
    private readonly TaskAlertService _alerts;

    public TaskRunService(IQuantumDbContext db, ILogger<TaskRunService> log, TaskAlertService alerts)
    {
        _db = db;
        _log = log;
        _alerts = alerts;
    }

    /// <summary>条件批量更新（ExecuteUpdate 需要 IQueryable 接收者，统一走这里避免各处写错形态）。</summary>
    private Task<int> UpdateRunsAsync(Expression<Func<TaskRunModel, bool>> predicate,
        Action<UpdateSettersBuilder<TaskRunModel>> setters)
        => _db.TaskRuns.Where(predicate).ExecuteUpdateAsync(setters);

    // ------------------------------------------------------------------ 受理与领取

    /// <summary>
    /// 受理一次执行：写入 Pending 运行行并立即返回（含预分配的 LogId）。
    /// 只有这一步落库成功，调用方才被允许把 RunId 暴露给客户端。
    /// </summary>
    public async Task<TaskRunModel> AcceptAsync(string taskId, string taskName, string scriptFile,
        TaskTriggerSource source, string triggerRef, string scriptHash = null)
    {
        var run = new TaskRunModel
        {
            Id = NewRunId(),
            TaskId = taskId,
            TaskNameSnapshot = Truncate(taskName, 200),
            ScriptFileSnapshot = Truncate(scriptFile, 500),
            ScriptHashSnapshot = Truncate(scriptHash, 64),
            TriggerSource = source,
            TriggerRef = Truncate(RedactRef(triggerRef), 200),
            Status = TaskRunStatus.Pending,
            LogId = NewRunId(),
            IsRetry = source == TaskTriggerSource.Retry
        };
        run.RootRunId = run.Id;

        await using var tx = await _db.Database.BeginTransactionAsync();
        _db.TaskRuns.Add(run);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return run;
    }

    /// <summary>
    /// 条件领取：Pending → Running。返回 false 表示已被其他领取者拿走或状态已变（重复投递的自然防线）。
    /// </summary>
    public async Task<bool> ClaimAsync(string runId)
        => await UpdateRunsAsync(n => n.Id == runId && n.Status == TaskRunStatus.Pending,
            n => n.SetProperty(p => p.Status, TaskRunStatus.Running)
                .SetProperty(p => p.StartedAtUtc, DateTime.UtcNow)) > 0;

    // ------------------------------------------------------------------ 终态落库

    /// <summary>
    /// 写终态：运行行与对应 t_log 行同事务提交，并驱动失败策略与告警判定。
    /// </summary>
    public async Task<TaskRunCompletion> CompleteAsync(TaskRunModel run, TaskExecutionResult result,
        LogType logType, string operatorName = "System", string remark = null)
    {
        var now = DateTime.UtcNow;
        run.Status = result.Outcome switch
        {
            TaskExecutionOutcome.Succeeded => TaskRunStatus.Succeeded,
            TaskExecutionOutcome.Failed => TaskRunStatus.Failed,
            TaskExecutionOutcome.Rejected => TaskRunStatus.Rejected,
            TaskExecutionOutcome.Canceled => TaskRunStatus.Canceled,
            _ => TaskRunStatus.Interrupted
        };
        run.FailureCode = result.FailureCode;
        run.SafeSummary = Truncate(result.SafeSummary, TaskExecutionResult.MaxSummaryLength);
        run.StartedAtUtc = result.StartedAtUtc == default ? run.StartedAtUtc : result.StartedAtUtc;
        run.FinishedAtUtc = result.FinishedAtUtc == default ? now : result.FinishedAtUtc;
        run.LogDirectoryName = result.LogDirectoryName ?? run.LogDirectoryName;
        run.LogFileName = result.LogFileName ?? run.LogFileName;

        var log = new LogModel
        {
            Id = run.LogId,
            CreateTime = DateTime.Now,
            LogType = logType,
            Operator = operatorName,
            Remark = remark ?? $"执行脚本任务 RunId={run.Id}",
            Title = run.TaskNameSnapshot
        };
        // 终态→日志行的映射只此一处口径（Success/Severity/Module/耗时/日志定位/结果后缀全在 ApplyToLog 里）
        result.ApplyToLog(log);

        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            _db.Logs.Add(log);
            _db.TaskRuns.Update(run);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception e)
        {
            // 计划 §3.3：数据库写失败时执行结果不得默默当成功
            _log.LogError(e, "运行记录终态落库失败（RunId={RunId}），该条自动重试已停用", run.Id);
            LogServiceHelper.Error("运行记录落库失败", $"RunId={run.Id}：{e.Message}", "Task");
            DetachTracked();
            return TaskRunCompletion.NotScheduled;
        }

        var scheduled = await ApplyPolicyAfterTerminalAsync(run, result);
        var alert = await _alerts.EvaluateAsync(run, result, !scheduled);
        return new TaskRunCompletion(scheduled, alert.PolicyOwned);
    }

    /// <summary>终态后按策略决定是否排重试；返回 true 表示已排上下一次尝试。</summary>
    private async Task<bool> ApplyPolicyAfterTerminalAsync(TaskRunModel run, TaskExecutionResult result)
    {
        // 第一期：自动重试只作用于有持久任务 Id 的手动/定时执行，且必须显式 opt-in；
        // Rejected/Canceled/Interrupted 一律不重试（只有脚本异常 Failed 才可能是偶发）
        if (run.TaskId == null || result.Outcome != TaskExecutionOutcome.Failed
            || run.TriggerSource is not (TaskTriggerSource.Manual or TaskTriggerSource.Cron or TaskTriggerSource.Retry))
        {
            return false;
        }

        var policy = await _db.TaskFailurePolicies.AsNoTracking().FirstOrDefaultAsync(n => n.TaskId == run.TaskId);
        if (policy == null || !policy.Enabled || policy.RetryCount <= 0)
        {
            return false;
        }

        var attemptsSoFar = await _db.TaskRuns.CountAsync(n => n.RootRunId == run.RootRunId);
        if (attemptsSoFar > policy.RetryCount)
        {
            run.CancelReason = $"已达重试上限 {policy.RetryCount} 次";
            await UpdateRunsAsync(n => n.Id == run.Id,
                n => n.SetProperty(p => p.CancelReason, run.CancelReason));
            return false;
        }

        // 脚本/任务已变化则取消重试：绝不能悄悄执行另一个版本
        var task = CacheManager.Get<TaskModel>().FirstOrDefault(n => n.Id == run.TaskId);
        if (task == null)
        {
            run.CancelReason = "任务已删除，取消自动重试";
        }
        else if (!task.Enable)
        {
            run.CancelReason = "任务已禁用，取消自动重试";
        }
        else if (!string.Equals(task.FileName, run.ScriptFileSnapshot, StringComparison.Ordinal))
        {
            run.CancelReason = "脚本已变更，取消自动重试";
        }

        if (run.CancelReason != null)
        {
            await UpdateRunsAsync(n => n.Id == run.Id,
                n => n.SetProperty(p => p.CancelReason, run.CancelReason));
            return false;
        }

        var backoff = Math.Min(MaxBackoffSeconds,
            policy.BackoffSeconds * (int)Math.Pow(2, Math.Max(0, attemptsSoFar - 1)));
        var nextAt = DateTime.UtcNow.AddSeconds(backoff);
        await UpdateRunsAsync(n => n.Id == run.Id,
            n => n.SetProperty(p => p.NextAttemptAtUtc, nextAt));
        run.NextAttemptAtUtc = nextAt;
        return true;
    }

    private void DetachTracked()
    {
        foreach (var entry in _db.ChangeTracker.Entries().Where(n =>
                     n.Entity is TaskRunModel or LogModel).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    // ------------------------------------------------------------------ 到期重试

    /// <summary>
    /// 领取一批到期的待重试记录，为每条派生新的尝试行（同根执行链、Attempt+1）。
    /// 同任务已有非终态记录时顺延，超过顺延上限即放弃——这是重试不与手动/Cron 重入的落地口径（R-04）。
    /// </summary>
    public async Task<List<TaskRunModel>> ClaimDueRetriesAsync(int take = 10)
    {
        var now = DateTime.UtcNow;
        var due = await _db.TaskRuns.AsNoTracking()
            .Where(n => n.Status == TaskRunStatus.Failed && n.NextAttemptAtUtc != null && n.NextAttemptAtUtc <= now)
            .OrderBy(n => n.NextAttemptAtUtc)
            .Take(take)
            .ToListAsync();

        var claimed = new List<TaskRunModel>();
        foreach (var run in due)
        {
            var overlap = await _db.TaskRuns.AnyAsync(n => n.TaskId == run.TaskId
                && n.Id != run.Id
                && (n.Status == TaskRunStatus.Running || n.Status == TaskRunStatus.Pending));
            if (overlap)
            {
                await DeferOrAbandonAsync(run, "同任务仍在执行，顺延重试");
                continue;
            }

            var task = CacheManager.Get<TaskModel>().FirstOrDefault(n => n.Id == run.TaskId);
            if (task == null)
            {
                await AbandonAsync(run.Id, "任务已删除，取消重试");
                continue;
            }

            if (!task.Enable)
            {
                await AbandonAsync(run.Id, "任务已禁用，取消重试");
                continue;
            }

            if (!string.Equals(task.FileName, run.ScriptFileSnapshot, StringComparison.Ordinal))
            {
                await AbandonAsync(run.Id, "脚本已变更，取消重试");
                continue;
            }

            // 条件更新占住这条待重试记录：两个轮询循环同时派生时只有一个能把排程清空
            var moved = await _db.TaskRuns
                .Where(r => r.Id == run.Id && r.Status == TaskRunStatus.Failed && r.NextAttemptAtUtc == run.NextAttemptAtUtc)
                .ExecuteUpdateAsync(n => n.SetProperty(p => p.NextAttemptAtUtc, (DateTime?)null));
            if (moved == 0)
            {
                continue;
            }

            var attempt = await _db.TaskRuns.CountAsync(n => n.RootRunId == run.RootRunId);
            var next = new TaskRunModel
            {
                Id = NewRunId(),
                RootRunId = run.RootRunId,
                Attempt = attempt + 1,
                TaskId = run.TaskId,
                TaskNameSnapshot = run.TaskNameSnapshot,
                ScriptFileSnapshot = run.ScriptFileSnapshot,
                ScriptHashSnapshot = run.ScriptHashSnapshot,
                TriggerSource = TaskTriggerSource.Retry,
                TriggerRef = $"retry-of:{run.Id}",
                // 新尝试仍走 Pending → Running 的同一领取口径，不给重试开第二条状态路径
                Status = TaskRunStatus.Pending,
                LogId = NewRunId(),
                IsRetry = true
            };
            _db.TaskRuns.Add(next);
            await _db.SaveChangesAsync();
            claimed.Add(next);
        }

        return claimed;
    }

    /// <summary>
    /// 同任务在跑时的顺延：推后 NextAttemptAtUtc 并累计次数，超过上限即放弃本条重试并留原因。
    /// </summary>
    private async Task DeferOrAbandonAsync(TaskRunModel run, string reason)
    {
        var deferrals = run.RetryDeferrals + 1;
        if (deferrals > MaxConcurrencyDeferrals)
        {
            await AbandonAsync(run.Id, $"{reason}（顺延 {deferrals} 次后放弃）");
            return;
        }

        await UpdateRunsAsync(n => n.Id == run.Id,
            n => n.SetProperty(p => p.NextAttemptAtUtc, DateTime.UtcNow.Add(ConcurrencyDeferral))
                .SetProperty(p => p.RetryDeferrals, deferrals));
    }

    /// <summary>彻底放弃某条待重试（任务删除/禁用/脚本变更）：清空排程并留原因。</summary>
    private async Task AbandonAsync(string runId, string reason)
    {
        await UpdateRunsAsync(n => n.Id == runId,
            n => n.SetProperty(p => p.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(p => p.CancelReason, Truncate(reason, 200)));
    }

    // ------------------------------------------------------------------ 启动恢复

    /// <summary>
    /// 进程启动恢复：超过宽限期仍挂在 Running 的记录判为 Interrupted（真实结果不可知，既不重放也不判成功）。
    /// 传 <paramref name="startedBeforeUtc"/>（本进程启动时刻）时改为只看该时刻：早于本次启动的 Running 行
    /// 不可能还在本进程里跑，无需等宽限期即可判中断；本进程自己启动的行绝不触碰（长任务跑超宽限期是正常态）。
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(TimeSpan? grace = null, DateTime? startedBeforeUtc = null)
    {
        var cutoff = startedBeforeUtc ?? DateTime.UtcNow.Subtract(grace ?? RunningGracePeriod);
        var affected = await UpdateRunsAsync(
            n => n.Status == TaskRunStatus.Running && n.StartedAtUtc < cutoff,
            n => n.SetProperty(p => p.Status, TaskRunStatus.Interrupted)
                .SetProperty(p => p.FinishedAtUtc, DateTime.UtcNow)
                .SetProperty(p => p.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(p => p.SafeSummary, "进程重启后残留的运行记录，真实结果不可知"));
        if (affected > 0)
        {
            LogServiceHelper.Warn($"启动恢复：{affected} 条运行记录判为中断", "进程重启时仍处于 Running 且超过宽限期", "Task");
        }

        return affected;
    }

    // ------------------------------------------------------------------ 查询与权限

    /// <summary>
    /// 执行历史分页。
    /// </summary>
    public async Task<(List<TaskRunModel> Rows, int Total)> GetPageAsync(string taskId, TaskRunStatus? status,
        int page, int pageSize, int days = 90)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));

        var query = _db.TaskRuns.AsNoTracking().Where(n => n.CreatedAtUtc >= since);
        if (!string.IsNullOrEmpty(taskId))
        {
            query = query.Where(n => n.TaskId == taskId);
        }
        if (status.HasValue)
        {
            query = query.Where(n => n.Status == status.Value);
        }

        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();
        return (rows, total);
    }

    /// <summary>
    /// 单条详情。
    /// </summary>
    public async Task<TaskRunModel> GetAsync(string runId)
    {
        if (string.IsNullOrEmpty(runId))
        {
            return null;
        }

        return await _db.TaskRuns.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == runId);
    }

    /// <summary>同一根执行的全部尝试（详情时间轴用）。</summary>
    public async Task<List<TaskRunModel>> GetChainAsync(string rootRunId)
        => await _db.TaskRuns.AsNoTracking()
            .Where(n => n.RootRunId == rootRunId)
            .OrderBy(n => n.Attempt)
            .ToListAsync();

    /// <summary>关联日志行是否仍在库里（false 时详情只显示「日志已清理」占位，不拼任意路径）。</summary>
    public async Task<bool> HasLogAsync(string logId)
        => !string.IsNullOrEmpty(logId) && await _db.Logs.AnyAsync(n => n.Id == logId);

    // ------------------------------------------------------------------ 失败策略

    /// <summary>读策略（无行即返回全默认值，不写库——存量任务因此行为不变）。</summary>
    public async Task<TaskFailurePolicyModel> GetPolicyAsync(string taskId)
    {
        var policy = await _db.TaskFailurePolicies.AsNoTracking().FirstOrDefaultAsync(n => n.TaskId == taskId);
        return policy ?? new TaskFailurePolicyModel
        {
            TaskId = taskId,
            RetryCount = 0,
            BackoffSeconds = 60,
            AlertAfterConsecutiveFailures = 1,
            SendRecovery = false,
            CooldownMinutes = 60,
            Enabled = false
        };
    }

    /// <summary>
    /// 写策略。所有上限在此处服务端校验并 clamp（UI 只辅助），权限判定在控制器侧 [LoggedInUser]。
    /// </summary>
    public async Task<TaskFailurePolicyModel> SavePolicyAsync(string taskId, TaskFailurePolicyModel input,
        string updatedBy)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            throw new BusinessException("缺少任务 Id");
        }

        var existing = await _db.TaskFailurePolicies.FirstOrDefaultAsync(n => n.TaskId == taskId);
        var target = existing ?? new TaskFailurePolicyModel { TaskId = taskId };

        target.RetryCount = Math.Clamp(input.RetryCount, 0, MaxRetryCount);
        target.BackoffSeconds = Math.Clamp(input.BackoffSeconds <= 0 ? 60 : input.BackoffSeconds, 30, MaxBackoffSeconds);
        target.AlertAfterConsecutiveFailures = Math.Clamp(
            input.AlertAfterConsecutiveFailures <= 0 ? 1 : input.AlertAfterConsecutiveFailures, 1, 10);
        target.SendRecovery = input.SendRecovery;
        target.CooldownMinutes = Math.Clamp(input.CooldownMinutes, 0, 1440);
        target.Enabled = input.Enabled;
        target.UpdatedAtUtc = DateTime.UtcNow;
        target.UpdatedBy = Truncate(updatedBy, 100);

        if (existing == null)
        {
            _db.TaskFailurePolicies.Add(target);
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        // 停用策略立即取消该任务后续待重试，避免「关了开关还在偷偷跑」
        if (!target.Enabled)
        {
            await CancelPendingRetriesAsync(taskId, "策略已关闭，取消待重试");
        }

        return target;
    }

    /// <summary>清空某任务全部待重试排程（禁用/删除/改策略时调用）。</summary>
    public async Task<int> CancelPendingRetriesAsync(string taskId, string reason)
        => await UpdateRunsAsync(
            n => n.TaskId == taskId && n.NextAttemptAtUtc != null && n.Status == TaskRunStatus.Failed,
            n => n.SetProperty(p => p.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(p => p.CancelReason, Truncate(reason, 200)));

    /// <summary>任务删除时保留历史记录（不级联），但必须先取消其待重试排程。</summary>
    public Task<int> CancelRetriesForDeletedTaskAsync(string taskId)
        => CancelPendingRetriesAsync(taskId, "任务已删除，取消待重试");

    /// <summary>
    /// 已派生但不再安全执行的 Pending 尝试行收口为 Canceled（任务被删/禁用/脚本变更），
    /// 让它以确定的终态结束，而不是留在待领取状态被反复扫描。
    /// </summary>
    public async Task AbandonPendingRetryAsync(TaskRunModel run, string reason)
    {
        await UpdateRunsAsync(n => n.Id == run.Id && n.Status == TaskRunStatus.Pending,
            n => n.SetProperty(p => p.Status, TaskRunStatus.Canceled)
                .SetProperty(p => p.FinishedAtUtc, DateTime.UtcNow)
                .SetProperty(p => p.CancelReason, Truncate($"{reason}，重试已取消", 200))
                .SetProperty(p => p.SafeSummary, Truncate($"{reason}，重试已取消", TaskExecutionResult.MaxSummaryLength)));
    }

    // ------------------------------------------------------------------ 工具

    private static string NewRunId() => Guid.NewGuid().ToString().ToUpper().Replace("-", "");

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    /// <summary>触发方标识脱敏：只留不可逆短摘要，绝不把令牌/用户原文入库。</summary>
    private static string RedactRef(string triggerRef)
    {
        if (string.IsNullOrEmpty(triggerRef))
        {
            return null;
        }

        if (triggerRef.Length <= 24)
        {
            return triggerRef;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(triggerRef));
        return $"ref:{Convert.ToHexString(hash)[..16]}";
    }
}

/// <summary>
/// 终态落库结论。
/// </summary>
/// <param name="Scheduled">是否排上了下一次自动重试</param>
/// <param name="PolicyOwned">该任务的通知是否已由启用中的失败策略接管——
/// true 时调用方必须跳过旧的「一次失败一条通知」路径（审核项 R-03 的兼容口径）</param>
public record TaskRunCompletion(bool Scheduled, bool PolicyOwned)
{
    public static readonly TaskRunCompletion NotScheduled = new(false, false);
}

/// <summary>受理回执：一次批量执行里每项的 TaskId 与其 RunId（新端点 execute-runs 的返回体）。</summary>
/// <param name="TaskId">任务 Id</param>
/// <param name="RunId">受理时生成的运行 Id</param>
public record TaskRunReceipt(string TaskId, string RunId);
