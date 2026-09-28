using System.Collections.Concurrent;

namespace Quantum.Application.Channels;

/// <summary>首期单进程/单副本下每平台串行化解绑与实际发信；多副本发布前必须改为 DB 租约+fencing。</summary>
public sealed class ChannelAccountGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(string platform, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(platform, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
