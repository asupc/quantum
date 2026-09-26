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
/// 一期 G2「运行记录可观测」回归：受理→领取→终态生命周期、日志同事务落库、
/// 唯一约束、分页与服务端权限过滤，以及 §3.6 保留期清理。
/// </summary>[Collection("ConstsState")]
public class TaskRunPersistenceTests : TaskRunTestBase
{
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
