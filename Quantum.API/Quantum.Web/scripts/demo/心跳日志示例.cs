// 心跳日志示例：最小可用的 C# 源码任务。
// 演示：ctx.Log 实时日志 + 协作式取消（长循环务必检查 ct）。
// 契约：实现 Quantum.Plugins.IQuantumTask，需要公共无参构造（默认即可）。
using Quantum.Plugins;

public class HeartbeatTask : IQuantumTask
{
    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        ctx.Log($"任务「{ctx.TaskName}」启动，每 2 秒输出一次心跳，共 5 次。");
        for (var i = 1; i <= 5; i++)
        {
            // 协作取消三件套：循环条件检查 ct、延迟携带 ct
            if (ct.IsCancellationRequested)
            {
                ctx.Log("收到取消信号，心跳任务提前结束。");
                return;
            }
            ctx.Log($"心跳 #{i}：{DateTime.Now:HH:mm:ss}");
            await Task.Delay(2000, ct);
        }
        ctx.Log("心跳任务正常结束。");
    }
}
