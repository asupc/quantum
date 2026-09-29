using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 单用户任务与日志查询回归。
/// CacheManager 为进程级静态缓存：用例内显式 Set 播种，避免依赖真实库/CWD。
/// </summary>
[Collection("ConstsState")]
public class PermissionLayeringTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;

    public PermissionLayeringTests()
    {
        (_connection, _db) = AppTestDb.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static TaskModel MakeTask(string name)
    {
        return new TaskModel
        {
            Id = Guid.NewGuid().ToString().Replace("-", ""),
            Name = name,
            Enable = true,
            CreateTime = DateTime.Now
        };
    }

    [Fact]
    public async Task TaskList_ReturnsAllTasks()
    {
        _db.Tasks.Add(MakeTask("任务一"));
        _db.Tasks.Add(MakeTask("任务二"));
        await _db.SaveChangesAsync();

        var taskService = new TaskService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance), new ScriptVersionService(_db));

        var all = await taskService.GetPageAsync(new TaskQuery());
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(2, all.Data.Count);
    }

    [Fact]
    public async Task LogQuery_LogTypes_Restricts_Visible_Types()
    {
        _db.Logs.Add(new LogModel { Id = "log-sys", Title = "系统错误", LogType = LogType.系统日志, CreateTime = DateTime.Now, Success = true, Operator = "System" });
        _db.Logs.Add(new LogModel { Id = "log-task", Title = "任务执行", LogType = LogType.任务日志, CreateTime = DateTime.Now, Success = true, Operator = "System" });
        _db.Logs.Add(new LogModel { Id = "log-cmd", Title = "指令触发", LogType = LogType.指令触发, CreateTime = DateTime.Now, Success = true, Operator = "user" });
        await _db.SaveChangesAsync();

        var logsService = new LogsService(_db);
        var query = new LogQuery { LogTypes = [LogType.任务日志, LogType.指令触发] };
        var page = await logsService.GetPageAsync(query);

        Assert.Equal(2, page.TotalCount);
        Assert.DoesNotContain(page.Data, n => n.LogType == LogType.系统日志);
    }
}

