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
        TaskRunService runs;
        using (var scope = _scopeFactory.CreateScope())
        {
            runs = scope.ServiceProvider.GetRequiredService<TaskRunService>();
            run = await runs.AcceptAsync(taskId, step.Task.Name, step.Task.FileName, source, triggerRef,
                step.Task.Manager);
        }

        if (!await runs.ClaimAsync(run.Id))
        {
            // 领取失败说明该记录已被别处收口；本次仍照常执行，但不再写第二份终态
            return await step.Run(ct);
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
