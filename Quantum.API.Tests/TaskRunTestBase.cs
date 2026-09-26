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
/// 运行记录 / 失败策略 / 告警去重三类用例的共享夹具：每例一套进程内 SQLite
/// （xUnit 每条用例新建实例），构造与 Dispose 只写一份，避免按门禁拆成三个测试类后夹具各自漂移。
/// 与 ConstsState 同 Collection（触碰 CacheManager 静态态）。
/// </summary>
[Collection("ConstsState")]
public abstract class TaskRunTestBase : IDisposable
{
    protected readonly SqliteConnection _connection;
    protected readonly QuantumSqliteDbContext _db;
    protected readonly TaskRunService _runs;
    protected readonly TaskAlertService _alerts;

    protected TaskRunTestBase()
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

    protected TaskRunService RunsOverTheSameDb() => _runs;

    protected async Task<TaskRunModel> AcceptAsync(string taskId = "T1", bool manager = false,
        TaskTriggerSource source = TaskTriggerSource.Manual, string script = "run_test.cs")
        => await _runs.AcceptAsync(taskId, "运行测试任务", script, source, null, manager);

    protected static TaskExecutionResult Ok(TaskRunModel run)
        => TaskExecutionResult.Succeeded(DateTime.UtcNow.AddSeconds(-2), DateTime.UtcNow, "run_test", "1.log");

    protected static TaskExecutionResult Boom(TaskRunModel run)
        => TaskExecutionResult.Failed(TaskFailureCode.ScriptException, "脚本抛异常",
            DateTime.UtcNow.AddSeconds(-2), DateTime.UtcNow, "run_test", "1.log");
}
