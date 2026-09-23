using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Quantum.Application;

/// <summary>
/// App WebSocket 连接注册表（/ws/app）：单管理员多端连接（多设备/多开并存）。
/// 每个连接配一把信号量串行化 SendAsync（WebSocket 不允许并发写）；
/// 下行推送一律广播（SendToAllAsync）——所有连接同属唯一管理员。
/// </summary>
public class AppWebSocketManager
{
    private readonly List<AppConnection> _connections = new();
    private readonly object _lock = new();
    private readonly ILogger<AppWebSocketManager> _logger;

    public AppWebSocketManager(ILogger<AppWebSocketManager> logger)
    {
        _logger = logger;
    }

    private sealed class AppConnection
    {
        public required WebSocket Socket { get; init; }
        public required SemaphoreSlim SendLock { get; init; }
        public long LastPongTicks;
        public DateTime LastPong
        {
            get => new DateTime(Volatile.Read(ref LastPongTicks), DateTimeKind.Utc);
            set => Volatile.Write(ref LastPongTicks, value.Ticks);
        }
    }

    public bool IsOnline()
    {
        lock (_lock)
        {
            return _connections.Any(n => n.Socket.State == WebSocketState.Open);
        }
    }

    public void Add(WebSocket socket)
    {
        var connection = new AppConnection
        {
            Socket = socket,
            SendLock = new SemaphoreSlim(1, 1),
            LastPong = DateTime.UtcNow
        };
        lock (_lock)
        {
            _connections.Add(connection);
        }
        _logger.LogInformation("App WS connected: current connections={Count}", ConnectionCount());
    }

    public void Remove(WebSocket socket)
    {
        lock (_lock)
        {
            _connections.RemoveAll(n => n.Socket == socket);
        }
        _logger.LogInformation("App WS removed: remaining connections={Count}", ConnectionCount());
    }

    public int ConnectionCount()
    {
        lock (_lock)
        {
            return _connections.Count;
        }
    }

    /// <summary>
    /// 记录心跳：收到该连接的任何帧（ping 或消息）即视为存活。
    /// </summary>
    public void MarkAlive(WebSocket socket)
    {
        lock (_lock)
        {
            foreach (var n in _connections.Where(n => n.Socket == socket))
            {
                n.LastPong = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// 踢掉超过 timeout 无任何帧的连接，返回被踢连接供外层关闭。
    /// </summary>
    public List<WebSocket> CollectTimeoutSockets(TimeSpan timeout)
    {
        var dead = new List<WebSocket>();
        lock (_lock)
        {
            foreach (var connection in _connections.Where(n => DateTime.UtcNow - n.LastPong > timeout).ToList())
            {
                dead.Add(connection.Socket);
                _connections.Remove(connection);
                _logger.LogInformation("App WS heartbeat timeout, kicking connection");
            }
        }
        return dead;
    }

    /// <summary>
    /// 单连接发送超时（WaitAsync + SendAsync 合计上限）：移动端息屏/弱网 TCP 零窗口时 SendAsync
    /// 会无限挂起并持有 SendLock，且该连接只要持续回 ping 就不会被心跳踢除——超时必须主动踢。
    /// §5-3：由可变全局静态降为管理器实例属性——每个 manager 独享，单测在自有实例上缩短即可，
    /// 不再把 80ms 残值泄漏给并行/后续用例。
    /// </summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 向所有在线连接广播一条已序列化的 JSON 文本帧；返回是否至少送达一个连接。
    /// </summary>
    public async Task<bool> SendToAllAsync(string json, CancellationToken cancellationToken = default)
    {
        List<AppConnection> snapshot;
        lock (_lock)
        {
            snapshot = _connections.ToList();
        }
        if (snapshot.Count == 0)
        {
            return false;
        }
        var bytes = Encoding.UTF8.GetBytes(json);
        var delivered = false;
        foreach (var connection in snapshot)
        {
            if (connection.Socket.State != WebSocketState.Open)
            {
                continue;
            }
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(SendTimeout);
                await connection.SendLock.WaitAsync(timeoutCts.Token);
                try
                {
                    await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeoutCts.Token);
                    delivered = true;
                }
                finally
                {
                    connection.SendLock.Release();
                }
            }
            catch (Exception e)
            {
                // 超时/发送失败：Abort 让对端读循环立即退出（零窗口连接 SendAsync 挂起，仅移出注册表救不了它）
                _logger.LogWarning(e, "App WS push failed (send timeout {Timeout}s), dropping connection", SendTimeout.TotalSeconds);
                connection.Socket.Abort();
                Remove(connection.Socket);
            }
        }
        return delivered;
    }

    /// <summary>
    /// 向指定连接发送一条已序列化的 JSON 文本帧（复用该连接的 SendLock，与推送路径互斥——
    /// WebSocket 不允许并发写，中间件下行帧绕过此锁会与随机时刻的推送撞车抛 InvalidOperationException）。
    /// §1-3 三态：连接从未注册返回 <see cref="SendToOneResult.NotFound"/>（未注册连接不会被推送命中，调用方直发安全）；
    /// 已注册但套接字非 Open 或发送抛异常返回 <see cref="SendToOneResult.SendFailed"/>（后者已被本管理器 Abort+Remove 处置，
    /// 调用方**不得**再对该套接字裸发）；发送成功返回 <see cref="SendToOneResult.Sent"/>。
    /// </summary>
    public async Task<SendToOneResult> SendToOneAsync(WebSocket socket, string json, CancellationToken cancellationToken = default)
    {
        AppConnection hit;
        lock (_lock)
        {
            hit = _connections.FirstOrDefault(n => n.Socket == socket);
        }
        if (hit == null)
        {
            return SendToOneResult.NotFound;
        }
        if (hit.Socket.State != WebSocketState.Open)
        {
            return SendToOneResult.SendFailed;
        }
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(SendTimeout);
            await hit.SendLock.WaitAsync(timeoutCts.Token);
            try
            {
                await hit.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeoutCts.Token);
            }
            finally
            {
                hit.SendLock.Release();
            }
            return SendToOneResult.Sent;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "App WS frame send failed (send timeout {Timeout}s), dropping connection", SendTimeout.TotalSeconds);
            hit.Socket.Abort();
            Remove(hit.Socket);
            return SendToOneResult.SendFailed;
        }
    }
}

/// <summary>§1-3：SendToOneAsync 的三态结果，区分「从未注册（可直发）」与「已被管理器处置（禁再裸发）」。</summary>
public enum SendToOneResult
{
    /// <summary>已注册且发送成功。</summary>
    Sent,
    /// <summary>从未注册的连接（首帧鉴权前）：调用方直发安全。</summary>
    NotFound,
    /// <summary>已注册但套接字非 Open，或发送抛异常且已被 Abort+Remove：调用方不得再裸发。</summary>
    SendFailed
}
