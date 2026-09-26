using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 一期 G2 权限边界回归（审核项 R-02）：任务日志/指令触发按「日志类型」放开给普通令牌，
/// 但内容可能属 Manager 任务——列表与详情都必须按任务归属再收敛一次，
/// 且任务被删除后仍要用运行记录的 Manager 快照兜住历史日志。
/// </summary>
[Collection("ConstsState")]
public class TaskRunAuthorizationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly LogsService _logs;

    public TaskRunAuthorizationTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _logs = new LogsService(_db);
        CacheManager.Set(new List<EnvModel>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SeedAsync()
    {
        _db.Tasks.AddRange(
            new TaskModel { Id = "T-PUB", Name = "普通任务", FileName = "public_task.cs", Manager = false, Enable = true },
            new TaskModel { Id = "T-SEC", Name = "管理员任务", FileName = "secret_task.cs", Manager = true, Enable = true });
        _db.Logs.AddRange(
            new LogModel
            {
                Id = "L-PUB", LogType = LogType.任务日志, Title = "普通任务", Operator = "System",
                Success = true, CreateTime = DateTime.Now,
                DirectoryName = TaskExcuteService.LogDirNameFrom("public_task.cs"), LogPath = "1.log"
            },
            new LogModel
            {
                Id = "L-SEC", LogType = LogType.任务日志, Title = "管理员任务", Operator = "System",
                Success = false, CreateTime = DateTime.Now,
                DirectoryName = TaskExcuteService.LogDirNameFrom("secret_task.cs"), LogPath = "2.log"
            });
        await _db.SaveChangesAsync();
    }

    private static LogQuery PublicTypes() => new()
    {
        PageIndex = 1,
        PageSize = 20,
        LogTypes = [LogType.任务日志, LogType.指令触发]
    };

    [Fact]
    public async Task LogsPage_NonManager_HidesManagerTaskLogs()
    {
        await SeedAsync();

        var page = await _logs.GetPageAsync(PublicTypes(), hideManagerTaskLogs: true);

        Assert.Equal(1, page.TotalCount);
        var row = Assert.Single(page.Data);
        Assert.Equal("L-PUB", row.Id);
    }

    [Fact]
    public async Task LogsPage_Manager_SeesBoth()
    {
        await SeedAsync();

        var page = await _logs.GetPageAsync(new LogQuery { PageIndex = 1, PageSize = 20 });

        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Details_NonManager_DetectsManagerTaskLogByDirectory()
    {
        await SeedAsync();

        Assert.True(await _logs.IsManagerTaskLogAsync(await _logs.GetMetaAsync("L-SEC")));
        Assert.False(await _logs.IsManagerTaskLogAsync(await _logs.GetMetaAsync("L-PUB")));
    }

    /// <summary>子目录脚本：写入侧与过滤侧必须共用同一净化口径，否则 Manager 日志按原文目录名漏网。</summary>
    [Fact]
    public async Task Details_SubDirectoryScript_StillHidden()
    {
        _db.Tasks.Add(new TaskModel { Id = "T-SUB", Name = "子目录任务", FileName = "grp/sub_task.cs", Manager = true, Enable = true });
        _db.Logs.Add(new LogModel
        {
            Id = "L-SUB", LogType = LogType.任务日志, Title = "子目录任务", Operator = "System",
            Success = true, CreateTime = DateTime.Now,
            DirectoryName = TaskExcuteService.LogDirNameFrom("grp/sub_task.cs"), LogPath = "3.log"
        });
        await _db.SaveChangesAsync();

        var page = await _logs.GetPageAsync(PublicTypes(), hideManagerTaskLogs: true);

        Assert.Equal(0, page.TotalCount);
        Assert.True(await _logs.IsManagerTaskLogAsync(await _logs.GetMetaAsync("L-SUB")));
    }

    /// <summary>任务被删除后目录名反查不到任务，历史日志改由运行记录的 Manager 快照兜底。</summary>
    [Fact]
    public async Task Details_AfterTaskDeleted_RunSnapshotStillHidesLog()
    {
        await SeedAsync();
        var run = new TaskRunModel
        {
            Id = "R-SEC", RootRunId = "R-SEC", Attempt = 1, TaskId = "T-SEC",
            TaskNameSnapshot = "管理员任务", ScriptFileSnapshot = "secret_task.cs",
            TriggerSource = TaskTriggerSource.Manual, Status = TaskRunStatus.Succeeded,
            ManagerSnapshot = true, LogId = "L-SEC"
        };
        _db.TaskRuns.Add(run);
        // 模拟任务已删除：目录名反查失效
        _db.Tasks.Remove(_db.Tasks.Local.First(n => n.Id == "T-SEC"));
        await _db.SaveChangesAsync();

        Assert.True(await _logs.IsManagerTaskLogAsync(await _logs.GetMetaAsync("L-SEC")));
    }

    [Fact]
    public async Task RunPage_NonManager_DoesNotSeeManagerRuns_EvenAfterTaskDeleted()
    {
        await SeedAsync();
        _db.TaskRuns.AddRange(
            new TaskRunModel
            {
                Id = "R-PUB", RootRunId = "R-PUB", TaskId = "T-PUB", Attempt = 1,
                TriggerSource = TaskTriggerSource.Manual, Status = TaskRunStatus.Succeeded,
                ManagerSnapshot = false
            },
            new TaskRunModel
            {
                Id = "R-SEC", RootRunId = "R-SEC", TaskId = "T-SEC", Attempt = 1,
                TriggerSource = TaskTriggerSource.Manual, Status = TaskRunStatus.Succeeded,
                ManagerSnapshot = true
            });
        await _db.SaveChangesAsync();

        var (rows, total) = await new TaskRunService(_db,
                new Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskRunService>(),
                new TaskAlertService(_db, new Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskAlertService>()))
            .GetPageAsync(null, null, 1, 20, isManager: false);

        Assert.Equal(1, total);
        Assert.Equal("R-PUB", Assert.Single(rows).Id);
    }
}
