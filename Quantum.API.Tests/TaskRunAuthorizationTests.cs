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
/// 单用户历史查询：旧任务与已删除任务的运行、日志记录均可见。
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
            new TaskModel { Id = "T-PUB", Name = "任务一", FileName = "public_task.cs", Enable = true },
            new TaskModel { Id = "T-SEC", Name = "任务二", FileName = "secret_task.cs", Enable = true });
        _db.Logs.AddRange(
            new LogModel
            {
                Id = "L-PUB", LogType = LogType.任务日志, Title = "任务一", Operator = "System",
                Success = true, CreateTime = DateTime.Now,
                DirectoryName = TaskExcuteService.LogDirNameFrom("public_task.cs"), LogPath = "1.log"
            },
            new LogModel
            {
                Id = "L-SEC", LogType = LogType.任务日志, Title = "任务二", Operator = "System",
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
    public async Task LogsPage_ShowsAllTaskLogs()
    {
        await SeedAsync();

        var page = await _logs.GetPageAsync(PublicTypes());

        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Data, row => row.Id == "L-PUB");
        Assert.Contains(page.Data, row => row.Id == "L-SEC");
    }

    [Fact]
    public async Task LogsPage_ShowsBoth()
    {
        await SeedAsync();

        var page = await _logs.GetPageAsync(new LogQuery { PageIndex = 1, PageSize = 20 });

        Assert.Equal(2, page.TotalCount);
    }

    /// <summary>子目录脚本的日志仍出现在列表。</summary>
    [Fact]
    public async Task Details_SubDirectoryScript_Visible()
    {
        _db.Tasks.Add(new TaskModel { Id = "T-SUB", Name = "子目录任务", FileName = "grp/sub_task.cs",  Enable = true });
        _db.Logs.Add(new LogModel
        {
            Id = "L-SUB", LogType = LogType.任务日志, Title = "子目录任务", Operator = "System",
            Success = true, CreateTime = DateTime.Now,
            DirectoryName = TaskExcuteService.LogDirNameFrom("grp/sub_task.cs"), LogPath = "3.log"
        });
        await _db.SaveChangesAsync();

        var page = await _logs.GetPageAsync(PublicTypes());

        Assert.Equal("L-SUB", Assert.Single(page.Data).Id);
    }

    /// <summary>删除任务后保留的日志仍可查询。</summary>
    [Fact]
    public async Task Details_AfterTaskDeleted_LogStillVisible()
    {
        await SeedAsync();
        var run = new TaskRunModel
        {
            Id = "R-SEC", RootRunId = "R-SEC", Attempt = 1, TaskId = "T-SEC",
            TaskNameSnapshot = "任务二", ScriptFileSnapshot = "secret_task.cs",
            TriggerSource = TaskTriggerSource.Manual, Status = TaskRunStatus.Succeeded,
             LogId = "L-SEC"
        };
        _db.TaskRuns.Add(run);
        // 模拟任务已删除：目录名反查失效
        _db.Tasks.Remove(_db.Tasks.Local.First(n => n.Id == "T-SEC"));
        await _db.SaveChangesAsync();

        Assert.NotNull(await _logs.GetMetaAsync("L-SEC"));
        Assert.Contains((await _logs.GetPageAsync(PublicTypes())).Data, n => n.Id == "L-SEC");
    }

    [Fact]
    public async Task RunPage_ShowsAllRuns()
    {
        await SeedAsync();
        _db.TaskRuns.AddRange(
            new TaskRunModel
            {
                Id = "R-PUB", RootRunId = "R-PUB", TaskId = "T-PUB", Attempt = 1,
                TriggerSource = TaskTriggerSource.Manual, Status = TaskRunStatus.Succeeded
            },
            new TaskRunModel
            {
                Id = "R-SEC", RootRunId = "R-SEC", TaskId = "T-SEC", Attempt = 1,
                TriggerSource = TaskTriggerSource.Manual, Status = TaskRunStatus.Succeeded
            });
        await _db.SaveChangesAsync();

        var (rows, total) = await new TaskRunService(_db,
                new Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskRunService>(),
                new TaskAlertService(_db, new Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskAlertService>()))
            .GetPageAsync(null, null, 1, 20);

        Assert.Equal(2, total);
        Assert.Contains(rows, n => n.Id == "R-PUB");
        Assert.Contains(rows, n => n.Id == "R-SEC");
    }
}
