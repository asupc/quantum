using Microsoft.Extensions.DependencyInjection;
using Quantum.Entities.Model;
using Quartz;

namespace Quantum.Application;

/// <summary>
/// 定时任务触发点。
/// 每次触发自建作用域解析 TaskService（不再经 JobDataMap 携带 Scoped 服务），
/// 并等待脚本执行完成后才返回——QuartzHostedService 的 WaitForJobsToComplete 因此才真正生效，
/// 停机时不会把运行中的脚本进程一起带走。
/// </summary>
/// <remarks>
/// JobKey 按任务 Id+FileName 隔离（JobHelper.CreateQuartzJob），本特性只阻止**同一任务**并发堆叠
/// （不检查取消令牌的挂死脚本曾按 cron 频率无限叠加执行，每份占线程/HttpClient/DI scope/增长中的日志缓冲）；
/// 不同任务之间不受影响，仍并行。
/// </remarks>
[DisallowConcurrentExecution]
public class TaskJob : IJob
{
    private readonly IServiceScopeFactory scopeFactory;

    public TaskJob(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var taskId = context.MergedJobDataMap.GetString("TaskId");
        if (string.IsNullOrEmpty(taskId))
        {
            return;
        }
        using var scope = scopeFactory.CreateScope();
        var taskService = scope.ServiceProvider.GetRequiredService<TaskService>();
        await taskService.ExecTaskAsync([taskId], cancellationToken);
    }
}
