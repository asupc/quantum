using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quantum.Data;
using Quantum.Entities.Model;

namespace Quantum.Application;

/// <summary>
/// 任务告警规则（一期 G4）：连续最终失败达阈值开告警、打开后首次成功发恢复，
/// 同一根执行只计一次，去重靠 t_task_alert_event 的唯一键 (TaskId, AlertType, RootRunId)。
///
/// 与存量口径的边界（审核项 R-03）：**未配置或策略未启用的任务，仍由旧的「一次失败一条通知」路径发**；
/// 策略一旦启用，旧路径必须让位（<see cref="EvaluateAsync"/> 返回 PolicyOwned=true），
/// 失败通知只能由最终失败事件投递，避免同一次失败发两条。
/// </summary>
public class TaskAlertService
{
    /// <summary>单条告警事件的最大投递尝试次数，超限停投并留痕</summary>
    public const int MaxDeliveryAttempts = 5;

    private readonly IQuantumDbContext _db;
    private readonly ILogger<TaskAlertService> _log;

    public TaskAlertService(IQuantumDbContext db, ILogger<TaskAlertService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// 一次执行链走到终局后评估告警。
    /// </summary>
    /// <param name="isFinalOfChain">本次是否为该根执行的最后一次尝试（排上了重试就不是）</param>
    public async Task<AlertEvaluation> EvaluateAsync(TaskRunModel run, TaskExecutionResult result, bool isFinalOfChain)
    {
        if (run?.TaskId == null)
        {
            return AlertEvaluation.NotManaged;
        }

        var policy = await _db.TaskFailurePolicies.AsNoTracking().FirstOrDefaultAsync(n => n.TaskId == run.TaskId);
        if (policy == null || !policy.Enabled)
        {
            return AlertEvaluation.NotManaged;
        }

        var state = await _db.TaskAlertStates.FirstOrDefaultAsync(n => n.TaskId == run.TaskId);
        if (state == null)
        {
            state = new TaskAlertStateModel { TaskId = run.TaskId };
            _db.TaskAlertStates.Add(state);
        }

        if (result.IsSuccess)
        {
            // 恢复只在「此前确实处于打开状态」时发，避免成功一次刷一条
            if (state.Open)
            {
                await CreateEventAsync(state, run, TaskAlertType.Recovered,
                    $"任务「{run.TaskNameSnapshot}」已恢复成功（此前连续失败 {state.ConsecutiveFailures} 次）",
                    policy.SendRecovery);
            }

            state.ConsecutiveFailures = 0;
            state.Open = false;
        }
        else if (result.Outcome == TaskExecutionOutcome.Failed && isFinalOfChain)
        {
            state.ConsecutiveFailures += 1;
            var cooldownPassed = state.LastSentAtUtc == null
                || DateTime.UtcNow >= state.LastSentAtUtc.Value.AddMinutes(policy.CooldownMinutes);
            if (state.ConsecutiveFailures >= policy.AlertAfterConsecutiveFailures && cooldownPassed)
            {
                await CreateEventAsync(state, run, TaskAlertType.FailureOpened,
                    $"任务「{run.TaskNameSnapshot}」连续失败 {state.ConsecutiveFailures} 次（{result.OutcomeLabel}）：{result.SafeSummary}",
                    send: true);
                state.Open = true;
            }
        }

        state.LastFinalRunId = run.Id;
        state.UpdatedAtUtc = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException e)
        {
            // 唯一键撞车=同一根执行已被另一个链路评估过，属正常去重，不是故障
            _log.LogInformation(e, "告警事件去重生效（TaskId={TaskId}, RunId={RunId}）", run.TaskId, run.Id);
            DetachAlertEntities();
            return new AlertEvaluation(true, false);
        }

        return new AlertEvaluation(true, true);
    }

    private async Task CreateEventAsync(TaskAlertStateModel state, TaskRunModel run, TaskAlertType type,
        string summary, bool send)
    {
        var exists = await _db.TaskAlertEvents.AnyAsync(n =>
            n.TaskId == run.TaskId && n.AlertType == type && n.RootRunId == run.RootRunId);
        if (exists)
        {
            return;
        }

        _db.TaskAlertEvents.Add(new TaskAlertEventModel
        {
            TaskId = run.TaskId,
            RootRunId = run.RootRunId,
            RunId = run.Id,
            AlertType = type,
            ConsecutiveFailures = state.ConsecutiveFailures,
            SafeSummary = summary?.Length > 512 ? summary[..512] : summary,
            DeliveryStatus = send ? TaskAlertDeliveryStatus.Pending : TaskAlertDeliveryStatus.Sent,
            DeliveredAtUtc = send ? null : DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });
        state.LastSentAtUtc = DateTime.UtcNow;
    }

    private void DetachAlertEntities()
    {
        foreach (var entry in _db.ChangeTracker.Entries().Where(n =>
                     n.Entity is TaskAlertEventModel or TaskAlertStateModel).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// 投递一批待发改告警事件（后台循环调用）。以事件 Id 为幂等键，投递成功才标 Sent；
    /// 标记失败会由下一轮重投——**不承诺网络层 exactly-once**，客户端按 MsgId 去重。
    /// </summary>
    public async Task<int> DeliverPendingAsync(int take = 20)
    {
        var now = DateTime.UtcNow;
        // 首发事件立即投递；失败重投按尝试次数线性退避，避免下游不可用时打成风暴
        var events = await _db.TaskAlertEvents
            .Where(n => n.DeliveryStatus == TaskAlertDeliveryStatus.Pending
                        || (n.DeliveryStatus == TaskAlertDeliveryStatus.Failed
                            && n.DeliveryAttempts < MaxDeliveryAttempts
                            && n.CreatedAtUtc.AddSeconds(n.DeliveryAttempts * 60) <= now))
            .OrderBy(n => n.CreatedAtUtc)
            .Take(take)
            .ToListAsync();

        var delivered = 0;
        foreach (var alertEvent in events)
        {
            alertEvent.DeliveryAttempts += 1;
            try
            {
                var sessionKey = alertEvent.TaskId;
                await AppPushDispatcher.SendNotificationAsync(
                    alertEvent.AlertType == TaskAlertType.Recovered ? "任务已恢复" : "任务失败告警",
                    alertEvent.SafeSummary,
                    "task",
                    $"quantum://task/{alertEvent.TaskId}/runs/{alertEvent.RunId}",
                    sessionKey);
                alertEvent.DeliveryStatus = TaskAlertDeliveryStatus.Sent;
                alertEvent.DeliveredAtUtc = DateTime.UtcNow;
                alertEvent.DeliveryError = null;
                delivered++;
            }
            catch (Exception e)
            {
                alertEvent.DeliveryStatus = TaskAlertDeliveryStatus.Failed;
                alertEvent.DeliveryError = TaskExecutionResult.SanitizeSummary(e.Message);
                _log.LogWarning(e, "告警事件投递失败（EventId={Id}，第 {Attempts} 次）", alertEvent.Id, alertEvent.DeliveryAttempts);
            }

            await _db.SaveChangesAsync();
        }

        return delivered;
    }

    /// <summary>清理：执行记录与事件按保留天数批量限速删除，不碰正在运行/待重试的数据。</summary>
    public async Task<int> PruneAsync(int runRetentionDays, int batchSize = 500)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(runRetentionDays, 1, 3650));
        var terminal = new[]
        {
            TaskRunStatus.Succeeded, TaskRunStatus.Failed, TaskRunStatus.Rejected,
            TaskRunStatus.Canceled, TaskRunStatus.Interrupted
        };

        var victims = await _db.TaskRuns.Where(n => n.CreatedAtUtc < cutoff && terminal.Contains(n.Status))
            .OrderBy(n => n.CreatedAtUtc).Take(batchSize).ToListAsync();
        if (victims.Count == 0)
        {
            return 0;
        }

        var ids = victims.Select(n => n.Id).ToList();
        _db.TaskRuns.RemoveRange(victims);
        // 事件与状态保留不少于执行记录：只清理早于同一 cutoff 的事件
        var oldEvents = await _db.TaskAlertEvents.Where(n => n.CreatedAtUtc < cutoff).ToListAsync();
        if (oldEvents.Count > 0)
        {
            _db.TaskAlertEvents.RemoveRange(oldEvents);
        }

        await _db.SaveChangesAsync();
        LogServiceHelper.Info("运行记录清理", $"删除 {ids.Count} 条执行记录、{oldEvents.Count} 条告警事件（早于 {cutoff:yyyy-MM-dd} UTC）", "Task");
        return ids.Count;
    }
}

/// <summary>
/// 告警评估结论。
/// </summary>
/// <param name="PolicyOwned">该任务是否已由启用的策略接管通知（true 时旧的「一次失败一条通知」路径必须让位）</param>
/// <param name="EventCreated">本次是否写入/推进了告警状态</param>
public record AlertEvaluation(bool PolicyOwned, bool EventCreated)
{
    public static readonly AlertEvaluation NotManaged = new(false, false);
}
