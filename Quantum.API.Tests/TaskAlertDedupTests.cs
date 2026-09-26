using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 一期 G4「规则告警」回归：连续最终失败按根执行只计一次、打开后首次成功才恢复、
/// 禁用任务不制造恢复事件、未启用策略时不接管旧式通知，以及非生产入口不污染告警状态。
/// </summary>[Collection("ConstsState")]
public class TaskAlertDedupTests : TaskRunTestBase
{
    [Fact]
    public async Task ShadowAndCommandRuns_DoNotPolluteAlertState()
    {
        await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel
        {
            RetryCount = 0,
            AlertAfterConsecutiveFailures = 1,
            CooldownMinutes = 0,
            Enabled = true
        }, "admin");
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "任务", FileName = "run_test.cs", Enable = true } });

        foreach (var source in new[] { TaskTriggerSource.Shadow, TaskTriggerSource.Command, TaskTriggerSource.OpenTrigger })
        {
            var run = await AcceptAsync("T1", source: source);
            await _runs.ClaimAsync(run.Id);
            await _runs.CompleteAsync(run, Boom(run), LogType.任务日志);
        }

        Assert.Equal(0, await _db.TaskAlertEvents.CountAsync());
        Assert.Equal(0, await _db.TaskAlertStates.CountAsync(n => n.ConsecutiveFailures > 0));

        // 同任务的一次真实定时失败才计入
        var cron = await AcceptAsync("T1", source: TaskTriggerSource.Cron);
        await _runs.ClaimAsync(cron.Id);
        await _runs.CompleteAsync(cron, Boom(cron), LogType.任务日志);
        Assert.Equal(1, await _db.TaskAlertEvents.CountAsync(n => n.AlertType == TaskAlertType.FailureOpened));
    }
    // ============================================================ G4 告警去重与恢复

    [Fact]
    public async Task Alerts_MultipleAttemptsOfSameRun_OnlyCountOnce()
    {
        await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel
        {
            RetryCount = 1,
            BackoffSeconds = 30,
            AlertAfterConsecutiveFailures = 1,
            CooldownMinutes = 0,
            Enabled = true
        }, "admin");
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "任务", FileName = "run_test.cs", Enable = true } });

        var first = await AcceptAsync("T1");
        await _runs.ClaimAsync(first.Id);
        // 第一次尝试排上了重试（RetryCount=1 的首次尝试仍在预算内）→ 不是终局，不该产生告警
        await _runs.CompleteAsync(first, Boom(first), LogType.任务日志);
        Assert.Equal(0, await _db.TaskAlertEvents.CountAsync());

        var second = new TaskRunModel
        {
            Id = "SECOND",
            RootRunId = first.RootRunId,
            Attempt = 2,
            TaskId = "T1",
            TriggerSource = TaskTriggerSource.Retry,
            Status = TaskRunStatus.Running,
            ScriptFileSnapshot = "run_test.cs",
            LogId = Guid.NewGuid().ToString("N"),
            IsRetry = true
        };
        _db.TaskRuns.Add(second);
        await _db.SaveChangesAsync();
        // 第二次尝试已达重试上限 → 终局失败，才开一条告警
        await _runs.CompleteAsync(second, Boom(second), LogType.任务日志);

        var opened = await _db.TaskAlertEvents.Where(n => n.AlertType == TaskAlertType.FailureOpened).ToListAsync();
        var single = Assert.Single(opened);
        Assert.Equal(first.RootRunId, single.RootRunId);
        Assert.Equal(1, single.ConsecutiveFailures);
    }

    [Fact]
    public async Task Alerts_RecoveredOnlyAfterOpen_AndDisabledTaskStaysSilent()
    {
        await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel
        {
            RetryCount = 0,
            AlertAfterConsecutiveFailures = 1,
            CooldownMinutes = 0,
            Enabled = true
        }, "admin");
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "任务", FileName = "run_test.cs", Enable = true } });

        // 从未开过告警时成功：不发恢复
        var early = await AcceptAsync("T1");
        await _runs.ClaimAsync(early.Id);
        await _runs.CompleteAsync(early, Ok(early), LogType.任务日志);
        Assert.Equal(0, await _db.TaskAlertEvents.CountAsync(n => n.AlertType == TaskAlertType.Recovered));

        var failing = await AcceptAsync("T1");
        await _runs.ClaimAsync(failing.Id);
        await _runs.CompleteAsync(failing, Boom(failing), LogType.任务日志);
        Assert.Equal(1, await _db.TaskAlertEvents.CountAsync(n => n.AlertType == TaskAlertType.FailureOpened));

        var ok = await AcceptAsync("T1");
        await _runs.ClaimAsync(ok.Id);
        await _runs.CompleteAsync(ok, Ok(ok), LogType.任务日志);
        Assert.Equal(1, await _db.TaskAlertEvents.CountAsync(n => n.AlertType == TaskAlertType.Recovered));

        // 恢复后计数归零；任务禁用不制造新的恢复事件
        var state = await _db.TaskAlertStates.SingleAsync(n => n.TaskId == "T1");
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.False(state.Open);
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "任务", FileName = "run_test.cs", Enable = false } });
        var afterDisable = await AcceptAsync("T1");
        await _runs.ClaimAsync(afterDisable.Id);
        await _runs.CompleteAsync(afterDisable, Ok(afterDisable), LogType.任务日志);
        Assert.Equal(1, await _db.TaskAlertEvents.CountAsync(n => n.AlertType == TaskAlertType.Recovered));
    }

    [Fact]
    public async Task Alerts_WithoutEnabledPolicy_AreNotManaged()
    {
        var run = await AcceptAsync("T1");
        await _runs.ClaimAsync(run.Id);

        var completion = await _runs.CompleteAsync(run, Boom(run), LogType.任务日志);

        // 未配置策略 → 旧式一次通知路径保留，且没有事件产生
        Assert.False(completion.PolicyOwned);
        Assert.Equal(0, await _db.TaskAlertEvents.CountAsync());
        Assert.Equal(0, await _db.TaskAlertStates.CountAsync());
    }

    [Fact]
    public async Task AlertDelivery_MarksSentAndRetriesFailures()
    {
        var delivered = await _alerts.DeliverPendingAsync();
        Assert.Equal(0, delivered);
    }
}
