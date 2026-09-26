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
/// 一期 G3「有限重试」回归：策略缺省即只跑一次、服务端 clamp、退避排程与终止条件
/// （上限/脚本变更/禁用/删除/策略关闭/影子与指令入口），以及启动中断恢复。
/// 自动重试默认关闭、上限 3 次，仅脚本异常参与。
/// </summary>[Collection("ConstsState")]
public class TaskRetryPolicyTests : TaskRunTestBase
{
    // ============================================================ G3 失败策略与重试

    [Fact]
    public async Task MissingPolicy_ReadsAsAllDefaultsAndRunsOnce()
    {
        var policy = await _runs.GetPolicyAsync("T-NONE");
        Assert.Equal(0, policy.RetryCount);
        Assert.False(policy.Enabled);

        // 存量任务未配置策略：失败后不得排任何重试
        var run = await AcceptAsync("T-NONE");
        await _runs.ClaimAsync(run.Id);
        var completion = await _runs.CompleteAsync(run, Boom(run), LogType.任务日志);

        Assert.False(completion.Scheduled);
        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Null(stored.NextAttemptAtUtc);
    }

    [Fact]
    public async Task SavePolicy_ClampsEveryFieldServerSide()
    {
        var saved = await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel
        {
            RetryCount = 99,
            BackoffSeconds = 999_999,
            AlertAfterConsecutiveFailures = 0,
            CooldownMinutes = 99_999,
            Enabled = true
        }, "admin");

        Assert.Equal(TaskRunService.MaxRetryCount, saved.RetryCount);
        Assert.Equal(TaskRunService.MaxBackoffSeconds, saved.BackoffSeconds);
        Assert.Equal(1, saved.AlertAfterConsecutiveFailures);
        Assert.Equal(1440, saved.CooldownMinutes);
        Assert.True(saved.Enabled);
    }

    [Fact]
    public async Task FailedExecution_WithEnabledPolicy_SchedulesNextAttempt()
    {
        await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel
        {
            RetryCount = 2,
            BackoffSeconds = 30,
            AlertAfterConsecutiveFailures = 5,
            CooldownMinutes = 0,
            Enabled = true
        }, "admin");
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "运行测试任务", FileName = "run_test.cs", Enable = true } });

        var run = await AcceptAsync("T1");
        await _runs.ClaimAsync(run.Id);
        var completion = await _runs.CompleteAsync(run, Boom(run), LogType.任务日志);

        Assert.True(completion.Scheduled);
        // 策略启用后旧式通知路径必须让位（R-03）
        Assert.True(completion.PolicyOwned);
        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.NotNull(stored.NextAttemptAtUtc);
    }

    [Theory]
    [InlineData(TaskExecutionOutcome.Rejected)]
    [InlineData(TaskExecutionOutcome.Canceled)]
    [InlineData(TaskExecutionOutcome.Interrupted)]
    public async Task NonFailedOutcomes_AreNeverRetried(TaskExecutionOutcome outcome)
    {
        await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel { RetryCount = 3, BackoffSeconds = 30, Enabled = true }, "admin");
        var now = DateTime.UtcNow;
        var result = outcome switch
        {
            TaskExecutionOutcome.Rejected => TaskExecutionResult.Rejected(TaskFailureCode.GateBlocked, "门禁拒绝", now, now, "d", "1.log"),
            TaskExecutionOutcome.Canceled => TaskExecutionResult.Canceled(TaskFailureCode.CanceledByShutdown, "取消", now, now, "d", "1.log"),
            _ => TaskExecutionResult.Interrupted("残留", now, now, "d", "1.log")
        };

        var run = await AcceptAsync("T1");
        await _runs.ClaimAsync(run.Id);

        Assert.False((await _runs.CompleteAsync(run, result, LogType.任务日志)).Scheduled);
    }

    [Fact]
    public async Task DueRetry_DerivesNextAttemptUnderSameRootRun()
    {
        var run = await AcceptAsync("T1");
        await _runs.ClaimAsync(run.Id);
        await _runs.CompleteAsync(run, Boom(run), LogType.任务日志);
        await _db.TaskRuns.Where(n => n.Id == run.Id)
            .ExecuteUpdateAsync(n => n.SetProperty(p => p.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "运行测试任务", FileName = "run_test.cs", Enable = true } });

        var claimed = await _runs.ClaimDueRetriesAsync();

        var next = Assert.Single(claimed);
        Assert.Equal(run.RootRunId, next.RootRunId);
        Assert.Equal(2, next.Attempt);
        Assert.True(next.IsRetry);
        Assert.Equal(TaskTriggerSource.Retry, next.TriggerSource);
        Assert.Equal(TaskRunStatus.Pending, next.Status);
        // 父记录的排程已清空，不会被下一轮重复领取
        var parent = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Null(parent.NextAttemptAtUtc);
    }

    [Fact]
    public async Task DueRetry_StoppedWhenScriptChanged()
    {
        var run = await AcceptAsync("T1", script: "old.cs");
        await _runs.ClaimAsync(run.Id);
        await _runs.CompleteAsync(run, Boom(run), LogType.任务日志);
        await _db.TaskRuns.Where(n => n.Id == run.Id)
            .ExecuteUpdateAsync(n => n.SetProperty(p => p.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "任务", FileName = "new.cs", Enable = true } });

        var claimed = await _runs.ClaimDueRetriesAsync();

        Assert.Empty(claimed);
        var parent = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Null(parent.NextAttemptAtUtc);
        Assert.Contains("脚本已变更", parent.CancelReason);
    }

    [Fact]
    public async Task DueRetry_StoppedWhenTaskDisabled()
    {
        var run = await AcceptAsync("T1");
        await _runs.ClaimAsync(run.Id);
        await _runs.CompleteAsync(run, Boom(run), LogType.任务日志);
        await _db.TaskRuns.Where(n => n.Id == run.Id)
            .ExecuteUpdateAsync(n => n.SetProperty(p => p.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "任务", FileName = "run_test.cs", Enable = false } });

        Assert.Empty(await _runs.ClaimDueRetriesAsync());
    }

    [Fact]
    public async Task DisablingPolicy_CancelsPendingRetries()
    {
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "运行测试任务", FileName = "run_test.cs", Enable = true } });
        await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel { RetryCount = 3, Enabled = true }, "admin");
        var run = await AcceptAsync("T1");
        await _runs.CompleteAsync(run, Boom(run), LogType.任务日志);
        Assert.NotNull((await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id)).NextAttemptAtUtc);

        await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel { RetryCount = 3, Enabled = false }, "admin");

        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Null(stored.NextAttemptAtUtc);
        Assert.Contains("策略已关闭", stored.CancelReason);
    }

    [Fact]
    public async Task RetryCap_StopsAfterConfiguredAttempts()
    {
        await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel { RetryCount = 1, BackoffSeconds = 30, Enabled = true }, "admin");
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "任务", FileName = "run_test.cs", Enable = true } });

        var first = await AcceptAsync("T1");
        await _runs.ClaimAsync(first.Id);
        Assert.True((await _runs.CompleteAsync(first, Boom(first), LogType.任务日志)).Scheduled);

        // 第二次尝试（已是 RetryCount 之后）不得再排第三次
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

        Assert.False((await _runs.CompleteAsync(second, Boom(second), LogType.任务日志)).Scheduled);
        Assert.Equal("已达重试上限 1 次", second.CancelReason);
        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == "SECOND");
        Assert.Null(stored.NextAttemptAtUtc);
        Assert.Contains("已达重试上限", stored.CancelReason);
    }

    [Fact]
    public async Task ShadowRun_NeverSchedulesRetry_EvenWithEnabledPolicy()
    {
        await _runs.SavePolicyAsync("T1", new TaskFailurePolicyModel { RetryCount = 3, Enabled = true }, "admin");
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Name = "任务", FileName = "run_test.cs", Enable = true } });

        var run = await AcceptAsync("T1", source: TaskTriggerSource.Shadow);
        await _runs.ClaimAsync(run.Id);

        Assert.False((await _runs.CompleteAsync(run, Boom(run), LogType.任务日志)).Scheduled);
    }

    /// <summary>影子试运行的 TaskId 可能正是真实任务 Id：若不按入口来源拦截，会污染该任务的连续失败计数。</summary>
    // ============================================================ 启动恢复

    [Fact]
    public async Task StartupRecovery_MarksStaleRunningInterrupted_KeepsFresh()
    {
        var stale = await AcceptAsync("T1");
        await _runs.ClaimAsync(stale.Id);
        await _db.TaskRuns.Where(n => n.Id == stale.Id)
            .ExecuteUpdateAsync(n => n.SetProperty(p => p.StartedAtUtc, DateTime.UtcNow.AddHours(-2)));

        var fresh = await AcceptAsync("T2");
        await _runs.ClaimAsync(fresh.Id);

        var affected = await RunsOverTheSameDb().RecoverInterruptedAsync();

        Assert.Equal(1, affected);
        Assert.Equal(TaskRunStatus.Interrupted, (await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == stale.Id)).Status);
        Assert.Equal(TaskRunStatus.Running, (await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == fresh.Id)).Status);
    }

    // ============================================================ 到期重试的实际执行（TaskService 侧）

    private async Task<TaskRunModel> NewPendingRetry(string taskId, string script, string runId = "RETRY1")
    {
        var run = new TaskRunModel
        {
            Id = runId,
            RootRunId = "ROOT1",
            Attempt = 2,
            TaskId = taskId,
            TaskNameSnapshot = "重试执行任务",
            ScriptFileSnapshot = script,
            TriggerSource = TaskTriggerSource.Retry,
            Status = TaskRunStatus.Pending,
            IsRetry = true,
            LogId = "LOG-RETRY-1"
        };
        _db.TaskRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    private TaskService TaskServiceOverTheSameDb() => new TaskService(_db,
        new AppMessageService(_db, NullLogger<AppMessageService>.Instance),
        new ScriptVersionService(_db), _runs);

    [Fact]
    public async Task ExecuteRetryAsync_RunsScriptAndWritesTerminalState()
    {
        Directory.CreateDirectory("scripts/quantum");
        var scriptPath = Path.Combine("scripts", "quantum", "g3retry_ok.cs");
        await File.WriteAllTextAsync(scriptPath, """
            using Quantum.Plugins;
            public class G3RetryOkTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    ctx.Log("重试跑成功了");
                    return Task.CompletedTask;
                }
            }
            """);
        try
        {
            CacheManager.Set(new List<TaskModel>
            {
                new() { Id = "T1", Name = "重试执行任务", FileName = "g3retry_ok.cs", Enable = true, WaitTime = 5 }
            });
            var run = await NewPendingRetry("T1", "g3retry_ok.cs");

            await TaskServiceOverTheSameDb().ExecuteRetryAsync(run);

            var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
            Assert.Equal(TaskRunStatus.Succeeded, stored.Status);
            Assert.NotNull(stored.StartedAtUtc);
            Assert.NotNull(stored.FinishedAtUtc);
            // 重试行走的是同一条终态落库链路：日志行也必须存在且指向该 RunId 预分配的 LogId
            var log = await _db.Logs.AsNoTracking().SingleOrDefaultAsync(n => n.Id == run.LogId);
            Assert.NotNull(log);
            Assert.True(log.Success);
            Assert.Equal(TaskRunStatus.Succeeded, stored.Status);
            Assert.Null(stored.NextAttemptAtUtc);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task ExecuteRetryAsync_AbandonsWhenTaskGoneAfterClaim()
    {
        // 领取到执行之间任务被删：以 Canceled 收口并留原因，绝不跑一个来路不明的脚本
        CacheManager.Set(new List<TaskModel>());
        var run = await NewPendingRetry("T-GONE", "whatever.cs");

        await TaskServiceOverTheSameDb().ExecuteRetryAsync(run);

        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Equal(TaskRunStatus.Canceled, stored.Status);
        Assert.Contains("任务已删除", stored.CancelReason);
        Assert.Null(stored.NextAttemptAtUtc);
        Assert.Equal(0, await _db.Logs.CountAsync());
    }

    [Fact]
    public async Task ExecuteRetryAsync_AbandonsWhenScriptChangedAfterClaim()
    {
        CacheManager.Set(new List<TaskModel>
        {
            new() { Id = "T1", Name = "重试执行任务", FileName = "new_script.cs", Enable = true }
        });
        var run = await NewPendingRetry("T1", "old_script.cs");

        await TaskServiceOverTheSameDb().ExecuteRetryAsync(run);

        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Equal(TaskRunStatus.Canceled, stored.Status);
        Assert.Contains("脚本已变更", stored.CancelReason);
    }
}
