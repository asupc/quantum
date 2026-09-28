using Microsoft.Extensions.DependencyInjection;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;

namespace Quantum.Application;

/// <summary>
/// 非 DI 入口的运行记录包装（一期 G2 入口矩阵收口）。
///
/// 聊天指令、外触内执、AI 影子试运行三条链路都不是构造注入的（经 root provider 或静态门面解析），
/// 拿不到 scoped 的 <see cref="TaskRunService"/>，故与 <see cref="AppPushDispatcher"/> 同套路：
/// 启动期注入 scopeFactory，调用处自建 scope。
///
/// 口径：这三条链路**只记录真实结果，不触发自动重试与告警**（重试/告警仅作用于有持久任务 Id 的
/// 手动/定时执行；影子试运行绝不进生产失败链）。日志行同样改由运行记录链路同步落库，
/// 不再走 3 秒批量队列。
/// </summary>
public static class TaskRunRecorder
{
    private static IServiceScopeFactory _scopeFactory;

    public static void Init(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 把一次已受理（Pending）的执行派到**独立作用域**里后台跑完。
    ///
    /// 必须自建 scope：受理发生在 HTTP 请求作用域内，请求一结束该 scope 的 DbContext 就被 Dispose，
    /// 直接捕获 <c>this</c> 里的 scoped 服务会让终态落库抛 ObjectDisposedException、
    /// 运行记录永久停在 Running（2026-09-26 隔离实例端到端冒烟实测）。
    /// 未注入容器时（单测直调）退回同步执行，避免静默不跑。
    /// </summary>
    public static void LaunchAcceptedRun(TaskRunModel run, Func<Task> inlineFallback, CancellationToken ct = default)
    {
        if (_scopeFactory == null)
        {
            _ = Task.Run(() => inlineFallback());
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TaskService>().ExecuteAcceptedRunAsync(run, ct);
            }
            catch (Exception e)
            {
                LogServiceHelper.Error("后台执行异常", $"RunId={run.Id}：{e.Message}", "Task");
            }
        });
    }

    /// <summary>
    /// 包装一次既有执行步骤：受理（Pending）→ 领取（Running）→ 跑脚本 → 终态与 t_log 同事务落库。
    /// 未注入 scopeFactory（如单测直调）时退化为「只跑脚本」，仍返回结构化终态，调用方结论不变。
    /// </summary>
    public static async Task<TaskExecutionResult> RunStepAsync(TaskCommandStep step, TaskTriggerSource source,
        string triggerRef, LogType logType, string operatorName = "System", CancellationToken ct = default)
    {
        if (_scopeFactory == null || step?.Task == null)
        {
            return await step.Run(ct);
        }

        var taskId = string.IsNullOrEmpty(step.Task.Id) ? null : step.Task.Id;
        TaskRunModel run;
        // 受理与领取共用同一存活作用域；不能把 scoped 服务带出 using 后再调用，
        // 否则每条指令都会在执行脚本前因 DbContext 已释放而中断。
        using (var scope = _scopeFactory.CreateScope())
        {
            var runs = scope.ServiceProvider.GetRequiredService<TaskRunService>();
            run = await runs.AcceptAsync(taskId, step.Task.Name, step.Task.FileName, source, triggerRef,
                step.Task.Manager);

            if (!await runs.ClaimAsync(run.Id))
            {
                // 领取失败说明该记录已被别处收口；本次仍照常执行，但不再写第二份终态
                return await step.Run(ct);
            }
        }

        var result = await step.Run(ct);
        using (var scope = _scopeFactory.CreateScope())
        {
            var completer = scope.ServiceProvider.GetRequiredService<TaskRunService>();
            await completer.CompleteAsync(run, result, logType, operatorName);
        }
        return result;
    }
}
