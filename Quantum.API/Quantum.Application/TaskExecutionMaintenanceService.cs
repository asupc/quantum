using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Quantum.Application;

/// <summary>
/// 运行记录后台维护（一期 G2/G3/G4）：启动恢复 → 到期重试领取 → 告警事件投递 → 保留期清理。
///
/// 约束：本期按**单实例调度服务**部署（Quartz 以内存状态重建，无跨节点选主）。
/// 多副本部署不得开启本服务的重试领取，否则同一任务会被多个节点各跑一次。
/// </summary>
public class TaskExecutionMaintenanceService : BackgroundService
{
    /// <summary>轮询间隔（计划口径 5~15s）</summary>
    internal static TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>启动后首次扫描前的等待：给 Quartz/请求留出把 Running 写稳的时间，避免误判中断</summary>
    internal static TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>每清理一轮之后的间隔（限速，避免长事务占库）</summary>
    private static TimeSpan PruneInterval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TaskExecutionMaintenanceService> _log;
    private int _roundsSincePrune;

    public TaskExecutionMaintenanceService(IServiceScopeFactory scopeFactory,
        ILogger<TaskExecutionMaintenanceService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RecoverOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // 后台异常必须可观测，但不能让循环静默退出（退出就等于重试永久停摆）
                _log.LogError(e, "运行记录维护循环异常，下一轮继续");
                LogServiceHelper.Error("运行记录维护异常", e.Message, "Task");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>启动恢复：残留 Running 判 Interrupted（真实结果不可知，既不重放也不判成功）。</summary>
    private async Task RecoverOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var runService = scope.ServiceProvider.GetRequiredService<TaskRunService>();
        try
        {
            await runService.RecoverInterruptedAsync();
        }
        catch (Exception e)
        {
            _log.LogError(e, "启动恢复扫描失败");
        }
    }

    /// <summary>一轮维护：领取到期重试 → 投递告警事件 → 按轮次触发保留期清理。</summary>
    private async Task RunRoundAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var runService = scope.ServiceProvider.GetRequiredService<TaskRunService>();
        var alertService = scope.ServiceProvider.GetRequiredService<TaskAlertService>();

        var due = await runService.ClaimDueRetriesAsync();
        foreach (var run in due)
        {
            // 每次重试都是独立的后台执行任务：单条失败不影响其余领取
            _ = TaskExecutionRetryRunner.LaunchAsync(_scopeFactory, run, ct);
        }

        await alertService.DeliverPendingAsync();

        _roundsSincePrune++;
        if (_roundsSincePrune >= (int)(PruneInterval / PollInterval))
        {
            _roundsSincePrune = 0;
            await alertService.PruneAsync(QuantumRuntimeOptions.RunRetentionDays);
        }
    }
}

/// <summary>
/// 到期重试的实际执行器：为领取到的尝试行跑一次脚本并落终态。
/// 单独拆出来是因为后台循环的 scope 不能活到脚本结束——每次执行要自己的 scope（含独立 DI 的 ctx）。
/// </summary>
internal static class TaskExecutionRetryRunner
{
    public static async Task LaunchAsync(IServiceScopeFactory scopeFactory, Entities.Model.TaskRunModel run,
        CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TaskService>().ExecuteRetryAsync(run, ct);
        }
        catch (Exception e)
        {
            LogServiceHelper.Error("重试执行异常", $"RunId={run.Id}：{e.Message}", "Task");
        }
    }
}

/// <summary>
/// 运行记录相关的进程内可调项（本期不引入新配置文件项，保留静态口子给灰度与压测）。
/// </summary>
public static class QuantumRuntimeOptions
{
    /// <summary>执行记录保留天数（默认 90 天，清理不碰运行中/待重试的数据）</summary>
    public static int RunRetentionDays = 90;
}
