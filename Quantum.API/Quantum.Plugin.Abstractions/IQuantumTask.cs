namespace Quantum.Plugins;

/// <summary>任务契约。实现类需有无参构造，每次执行实例化新对象（无共享状态）。</summary>
public interface IQuantumTask
{
    /// <summary>每次任务执行调用一次。长循环请定期检查 ct（推荐 while(!ct.IsCancellationRequested)）。</summary>
    Task RunAsync(QuantumTaskContext ctx, CancellationToken ct);
}
