using log4net;
using Microsoft.Extensions.Hosting;

namespace Quantum.Application;

/// <summary>
/// 会话步骤定时巡检（§1-5）：每 5 分钟按 ForceEndTime 清扫过期的多步骤等待会话，
/// 不再依赖「有新消息进来才顺手清理」——长期静默时过期会话也能被回收。
/// </summary>
public class SessionStepCleanupService : BackgroundService
{
    private static readonly ILog Log = LogManager.GetLogger("NETCoreRepository", typeof(SessionStepCleanupService));
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var removed = MemoryObjectCache.SweepExpiredSteps();
                    if (removed > 0)
                    {
                        Log.Info($"定时巡检清理过期会话步骤 {removed} 条。");
                    }
                }
                catch (Exception e)
                {
                    Log.Error("定时巡检清理会话步骤失败", e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主正常停机
        }
    }
}
