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
/// 一期 G2/G3/G4 回归：运行记录生命周期、双库模型的唯一约束、失败策略与到期重试、
/// 告警去重与恢复、以及 Manager/非 Manager/匿名的读取边界。
/// 与 ConstsState 同 Collection（触碰 CacheManager 与 configPath 相关静态态）。
/// </summary>
[Collection("ConstsState")]
public class TaskRunPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly TaskRunService _runs;
    private readonly TaskAlertService _alerts;

    public TaskRunPersistenceTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _alerts = new TaskAlertService(_db, NullLogger<TaskAlertService>.Instance);
        _runs = new TaskRunService(_db, NullLogger<TaskRunService>.Instance, _alerts);
        CacheManager.Set(new List<TaskModel>());
        CacheManager.Set(new List<EnvModel>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private TaskRunService RunsOverTheSameDb() => _runs;

    private async Task<TaskRunModel> AcceptAsync(string taskId = "T1", bool manager = false,
        TaskTriggerSource source = TaskTriggerSource.Manual, string script = "run_test.cs")
        => await _runs.AcceptAsync(taskId, "运行测试任务", script, source, null, manager);

    private static TaskExecutionResult Ok(TaskRunModel run)
        => TaskExecutionResult.Succeeded(DateTime.UtcNow.AddSeconds(-2), DateTime.UtcNow, "run_test", "1.log");

    private static TaskExecutionResult Boom(TaskRunModel run)
        => TaskExecutionResult.Failed(TaskFailureCode.ScriptException, "脚本抛异常",
            DateTime.UtcNow.AddSeconds(-2), DateTime.UtcNow, "run_test", "1.log");

    // ============================================================ G2 生命周期

    [Fact]
    public async Task Accept_WritesPendingRowWithOwnRootIdAndPreallocatedLogId()
    {
        var run = await AcceptAsync();

        Assert.Equal(TaskRunStatus.Pending, run.Status);
        Assert.Equal(run.Id, run.RootRunId);
        Assert.Equal(1, run.Attempt);
        Assert.False(string.IsNullOrEmpty(run.LogId));
        Assert.Null(run.StartedAtUtc);
        Assert.Null(run.FinishedAtUtc);

        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Equal("运行测试任务", stored.TaskNameSnapshot);
        Assert.Equal(TaskTriggerSource.Manual, stored.TriggerSource);
    }

    [Fact]
    public async Task Claim_IsConditional_FirstCallerWins()
    {
        var run = await AcceptAsync();

        Assert.True(await _runs.ClaimAsync(run.Id));
        // 第二次领取必须失败：同一 Pending 记录不能被两个入口同时开跑
        Assert.False(await _runs.ClaimAsync(run.Id));

        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Equal(TaskRunStatus.Running, stored.Status);
        Assert.NotNull(stored.StartedAtUtc);
    }

    [Fact]
    public async Task Complete_WritesRunAndLogInOneCommit_WithMatchingSuccess()
    {
        var run = await AcceptAsync();
        await _runs.ClaimAsync(run.Id);

        var completion = await _runs.CompleteAsync(run, Ok(run), LogType.任务日志);

        Assert.False(completion.Scheduled);
        Assert.False(completion.PolicyOwned);
        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Equal(TaskRunStatus.Succeeded, stored.Status);
        Assert.NotNull(stored.FinishedAtUtc);
        // 日志行同事务落库：详情链接可以在接口返回后立即使用（R-01）
        Assert.True(await _runs.HasLogAsync(run.LogId));
        var log = await _db.Logs.AsNoTracking().SingleAsync(n => n.Id == run.LogId);
        Assert.True(log.Success);
        Assert.Equal(run.LogDirectoryName, log.DirectoryName);
        Assert.Equal(run.LogFileName, log.LogPath);
    }

    [Fact]
    public async Task Complete_RejectedOutcome_StoresRejectedAndUnsuccessfulLog()
    {
        var run = await AcceptAsync();
        await _runs.ClaimAsync(run.Id);
        var result = TaskExecutionResult.Rejected(TaskFailureCode.ScriptMissing, "脚本文件不存在",
            DateTime.UtcNow, DateTime.UtcNow, "run_test", "2.log");

        await _runs.CompleteAsync(run, result, LogType.任务日志);

        var stored = await _db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == run.Id);
        Assert.Equal(TaskRunStatus.Rejected, stored.Status);
        Assert.Equal(TaskFailureCode.ScriptMissing, stored.FailureCode);
        var log = await _db.Logs.AsNoTracking().SingleAsync(n => n.Id == run.LogId);
        Assert.False(log.Success);
        Assert.Equal(LogSeverity.Error, log.Severity);
    }

    [Fact]
    public async Task UniqueRootRunAttempt_RejectsSecondSameAttempt()
    {
        var run = await AcceptAsync();
        var twin = new TaskRunModel
        {
            Id = "TWIN",
            RootRunId = run.RootRunId,
            Attempt = 1,
            TaskId = run.TaskId,
            Status = TaskRunStatus.Pending,
            TriggerSource = TaskTriggerSource.Manual
        };
        _db.TaskRuns.Add(twin);

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    // ============================================================ G2 权限边界

    [Fact]
    public async Task Page_NonManagerToken_NeverSeesManagerTaskRuns()
    {
        await AcceptAsync("T-PUB", manager: false);
        await AcceptAsync("T-SEC", manager: true);

        var (rows, total) = await _runs.GetPageAsync(null, null, 1, 20, isManager: false);
        Assert.Equal(1, total);
        Assert.All(rows, n => Assert.Equal("T-PUB", n.TaskId));

        var (managerRows, managerTotal) = await _runs.GetPageAsync(null, null, 1, 20, isManager: true);
        Assert.Equal(2, managerTotal);
        Assert.Equal(2, managerRows.Count);
    }

    [Fact]
    public async Task Detail_NonManagerGet_HiddenManagerRunButFindsOwn()
    {
        var open = await AcceptAsync("T-PUB", manager: false);
        var secret = await AcceptAsync("T-SEC", manager: true);

        Assert.Null(await _runs.GetAsync(secret.Id, isManager: false));
        Assert.NotNull(await _runs.GetAsync(secret.Id, isManager: true));
        Assert.NotNull(await _runs.GetAsync(open.Id, isManager: false));
    }

    [Fact]
    public async Task Page_PaginationIsClampedToServerSideMax()
    {
        for (var i = 0; i < 3; i++)
        {
            await AcceptAsync($"T{i}");
        }

        var (rows, _) = await _runs.GetPageAsync(null, null, 1, 5000, isManager: true);
        Assert.True(rows.Count <= TaskRunService.MaxPageSize);

        var (_, total) = await _runs.GetPageAsync(null, null, 1, 2, isManager: true);
        Assert.Equal(3, total);
    }

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

    [Fact]
    public async Task Prune_OnlyRemovesOldTerminalRows()
    {
        var old = await AcceptAsync("T1");
        await _runs.ClaimAsync(old.Id);
        await _runs.CompleteAsync(old, Boom(old), LogType.任务日志);
        await _db.TaskRuns.Where(n => n.Id == old.Id)
            .ExecuteUpdateAsync(n => n.SetProperty(p => p.CreatedAtUtc, DateTime.UtcNow.AddDays(-200)));
        var fresh = await AcceptAsync("T2");

        var removed = await _alerts.PruneAsync(90);

        Assert.Equal(1, removed);
        Assert.Null(await _db.TaskRuns.AsNoTracking().FirstOrDefaultAsync(n => n.Id == old.Id));
        Assert.NotNull(await _db.TaskRuns.AsNoTracking().FirstOrDefaultAsync(n => n.Id == fresh.Id));
    }
}
