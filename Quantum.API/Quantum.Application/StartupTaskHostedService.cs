using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quantum.Entities.Model;
using Quantum.Data;

namespace Quantum.Application;

/// <summary>
/// 启动期异步注册（定时任务注册）。
/// 用 IHostedService 承载替代 Configure 里的裸 Task.Run：
/// 作用域规范（自建 scope，不捕获根容器 Scoped 实例）、异常进宿主日志、随宿主启停。
/// </summary>
public class StartupTaskHostedService : IHostedService
{
    private readonly IServiceScopeFactory scopeFactory;

    public StartupTaskHostedService(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        // Quartz 单调度器：JobHelper 与托管服务共享同一个 DI 工厂。
        // 必须在注册定时任务前初始化（托管服务先于 Configure 中间件管线执行）。
        JobHelper.Init(scope.ServiceProvider.GetRequiredService<ISchedulerFactory>());

        var tasks = CacheManager.Get<TaskModel>().Where(n => n.Enable && !string.IsNullOrEmpty(n.Cron));
        foreach (var task in tasks)
        {
            if (CronExpression.TryParse(task.Cron, out _))
            {
                await task.CreateQuartzJob();
                Console.WriteLine("创建定时任务：" + task.Name);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
