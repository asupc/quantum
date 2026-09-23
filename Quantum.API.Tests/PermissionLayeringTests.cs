using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// A5.0④ 权限分层的业务侧收敛：任务列表服务端 Manager 过滤、任务操作守卫、
/// 日志多类型可见性收敛、环境变量非管理员限权。
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

    private static TaskModel MakeTask(string name, bool manager)
    {
        return new TaskModel
        {
            Id = Guid.NewGuid().ToString().Replace("-", ""),
            Name = name,
            Manager = manager,
            Enable = true,
            CreateTime = DateTime.Now
        };
    }

    [Fact]
    public async Task TaskList_ServerSide_Filters_Manager_Tasks_For_Non_Manager()
    {
        _db.Tasks.Add(MakeTask("普通任务", manager: false));
        _db.Tasks.Add(MakeTask("管理员任务", manager: true));
        await _db.SaveChangesAsync();

        var taskService = new TaskService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance), new ScriptVersionService(_db));

        // 管理员视角：全部可见
        var all = await taskService.GetPageAsync(new TaskQuery());
        Assert.Equal(2, all.TotalCount);

        // 普通用户视角：Manager 任务被服务端过滤（列表 + 总数）
        var scoped = await taskService.GetPageAsync(new TaskQuery(), excludeManager: true);
        Assert.Single(scoped.Data);
        Assert.Equal(1, scoped.TotalCount);
        Assert.All(scoped.Data, n => Assert.False(n.Manager));
    }

    [Fact]
    public void EnsureAccessible_Blocks_Manager_Task_For_Non_Manager()
    {
        var managerTask = MakeTask("管理员任务", manager: true);
        var normalTask = MakeTask("普通任务", manager: false);
        CacheManager.Set(new List<TaskModel> { managerTask, normalTask });

        var taskService = new TaskService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance), new ScriptVersionService(_db));

        // 管理员放行
        taskService.EnsureAccessible([managerTask.Id], includeManager: true);
        // 普通任务对非管理员放行
        taskService.EnsureAccessible([normalTask.Id], includeManager: false);
        // 非管理员触碰 Manager 任务被拒
        Assert.Throws<BusinessException>(() => taskService.EnsureAccessible([managerTask.Id], includeManager: false));
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

