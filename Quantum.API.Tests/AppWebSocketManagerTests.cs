using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// App WS 连接注册表（/ws/app）：注册/摘除、多设备计数、心跳超时收集、并发推送串行化、原始 JSON 帧广播。
/// </summary>
public class AppWebSocketManagerTests
{
    private static AppWebSocketManager CreateManager() => new(NullLogger<AppWebSocketManager>.Instance);

    [Fact]
    public void Add_MakesOnline_AndRemoveMakesOffline()
    {
        var manager = CreateManager();
        var socket = new FakeAppSocket();

        manager.Add(socket);
        Assert.True(manager.IsOnline());
        Assert.Equal(1, manager.ConnectionCount());

        manager.Remove(socket);
        Assert.False(manager.IsOnline());
        Assert.Equal(0, manager.ConnectionCount());
    }

    [Fact]
    public void Add_SupportsMultipleDevices()
    {
        var manager = CreateManager();
        var s1 = new FakeAppSocket();
        var s2 = new FakeAppSocket();

        manager.Add(s1);
        manager.Add(s2);
        Assert.Equal(2, manager.ConnectionCount());

        // 摘除其一（多设备语义）：另一台仍在线
        manager.Remove(s1);
        Assert.True(manager.IsOnline());
        Assert.Equal(1, manager.ConnectionCount());
    }

    [Fact]
    public async Task SendToAllAsync_NoConnection_ReturnsFalse()
    {
        var manager = CreateManager();
        Assert.False(await manager.SendToAllAsync("{\"type\":\"message\"}"));
    }

    [Fact]
    public async Task SendToAllAsync_DeliversRawJsonToAllDevices()
    {
        var manager = CreateManager();
        var s1 = new FakeAppSocket();
        var s2 = new FakeAppSocket();
        manager.Add(s1);
        manager.Add(s2);

        var delivered = await manager.SendToAllAsync("{\"type\":\"notify\",\"msgId\":\"m1\"}");

        Assert.True(delivered);
        Assert.Contains("\"type\":\"notify\"", s1.SentText.Replace(" ", ""));
        Assert.Contains("m1", s2.SentText);
    }

    [Fact]
    public async Task SendToAllAsync_ConcurrentSendsAreSerializedPerConnection()
    {
        var manager = CreateManager();
        var socket = new FakeAppSocket(delayPerSendMs: 5);
        manager.Add(socket);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => manager.SendToAllAsync("m" + Guid.NewGuid().ToString("N")[..4]))
            .ToList();
        await Task.WhenAll(tasks);

        Assert.False(socket.ConcurrentWriteDetected, "检测到对同一连接的并发写，信号量未生效");
        Assert.Equal(20, socket.SendCount);
    }

    [Fact]
    public void MarkAlive_UnknownSocket_IsNoOp()
    {
        var manager = CreateManager();
        manager.MarkAlive(new FakeAppSocket());
    }

    [Fact]
    public void CollectTimeoutSockets_KicksOnlySilentConnections()
    {
        var manager = CreateManager();
        var active = new FakeAppSocket();
        var silent = new FakeAppSocket();
        manager.Add(active);
        manager.Add(silent);

        MarkSilent(manager, silent, TimeSpan.FromSeconds(31));

        var dead = manager.CollectTimeoutSockets(TimeSpan.FromSeconds(30));

        Assert.Single(dead);
        Assert.Same(silent, dead[0]);
        Assert.Equal(1, manager.ConnectionCount());
    }

    [Fact]
    public async Task SendToAllAsync_HangingConnection_TimesOutDropsAndOthersStillDeliver()
    {
        // 零窗口/只 ping 不读的连接：SendAsync 无限挂起——超时须踢除（Abort+移出注册表），其余连接继续送达
        var manager = CreateManager();
        var hanging = new HangingAppSocket();
        var healthy = new FakeAppSocket();
        manager.Add(hanging);
        manager.Add(healthy);

        manager.SendTimeout = TimeSpan.FromMilliseconds(80);
        // 实例隔离：缩短某 manager 不应影响新建实例的默认值
        Assert.Equal(TimeSpan.FromSeconds(5), CreateManager().SendTimeout);

        var delivered = await manager.SendToAllAsync("{\"type\":\"notify\"}");
        Assert.True(delivered, "健康连接应正常送达");
        Assert.Equal(1, manager.ConnectionCount());
        Assert.Equal(WebSocketState.Aborted, hanging.State);
        Assert.Contains("notify", healthy.SentText);
    }

    [Fact]
    public async Task SendToOneAsync_NotRegistered_ReturnsNotFound()
    {
        // §1-3：从未注册（首帧鉴权前）的连接 → NotFound，调用方可安全直发，管理器不得写它
        var manager = CreateManager();
        var socket = new FakeAppSocket();

        var result = await manager.SendToOneAsync(socket, "{\"type\":\"pong\"}");

        Assert.Equal(SendToOneResult.NotFound, result);
        Assert.Equal(0, socket.SendCount);
    }

    [Fact]
    public async Task SendToOneAsync_RegisteredOpen_SendsUnderLockAndReturnsSent()
    {
        var manager = CreateManager();
        var socket = new FakeAppSocket();
        manager.Add(socket);

        var result = await manager.SendToOneAsync(socket, "{\"type\":\"message\",\"msgId\":\"m9\"}");

        Assert.Equal(SendToOneResult.Sent, result);
        Assert.Equal(1, socket.SendCount);
        Assert.Contains("m9", socket.SentText);
    }

    [Fact]
    public async Task SendToOneAsync_RegisteredButClosed_ReturnsSendFailedWithoutSending()
    {
        // §1-3：已注册但套接字非 Open → SendFailed（不得发送，交由调用方按已处置处理）
        var manager = CreateManager();
        var socket = new FakeAppSocket();
        manager.Add(socket);
        socket.Dispose(); // FakeAppSocket.Dispose 置 State=Closed

        var result = await manager.SendToOneAsync(socket, "{\"type\":\"message\"}");

        Assert.Equal(SendToOneResult.SendFailed, result);
        Assert.Equal(0, socket.SendCount);
    }

    [Fact]
    public async Task SendToOneAsync_SendThrows_TimeoutAbortsRemovesAndReturnsSendFailed()
    {
        // §1-3：发送挂起超时 → Abort+Remove+SendFailed（调用方不得再裸发）
        var manager = CreateManager();
        var hanging = new HangingAppSocket();
        manager.Add(hanging);

        manager.SendTimeout = TimeSpan.FromMilliseconds(80);

        var result = await manager.SendToOneAsync(hanging, "{\"type\":\"message\"}");

        Assert.Equal(SendToOneResult.SendFailed, result);
        Assert.Equal(WebSocketState.Aborted, hanging.State);
        Assert.Equal(0, manager.ConnectionCount());
    }

    /// <summary>SendAsync 永不完成的挂起连接（模拟移动端 TCP 零窗口）。</summary>
    private sealed class HangingAppSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketCloseStatus? CloseStatus => null;

        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new InvalidOperationException("HangingAppSocket 不产生入站帧");
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static void MarkSilent(AppWebSocketManager manager, FakeAppSocket socket, TimeSpan ago)
    {
        // LastPongTicks 是 AppConnection 的私有字段，通过 List<AppConnection> 反射置旧
        var list = (System.Collections.IEnumerable)manager.GetType()
            .GetField("_connections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(manager)!;
        foreach (var conn in list)
        {
            var socketProp = conn.GetType().GetProperty("Socket")!;
            if (ReferenceEquals(socketProp.GetValue(conn), socket))
            {
                conn.GetType().GetField("LastPongTicks")!.SetValue(conn, DateTime.UtcNow.Subtract(ago).Ticks);
                return;
            }
        }
        Assert.Fail("未找到目标连接");
    }

    /// <summary>
    /// FakeAppSocket：内存型 WebSocket，记录发送内容；检测并发写；可控延迟。
    /// </summary>
    private sealed class FakeAppSocket(int delayPerSendMs = 0) : WebSocket
    {
        private readonly object _gate = new();
        private bool _sending;
        private readonly List<byte> _buffer = [];

        public string SentText => Encoding.UTF8.GetString([.. _buffer]);
        public int SendCount { get; private set; }
        public bool ConcurrentWriteDetected { get; private set; }

        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("FakeAppSocket 不产生入站帧");
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_sending)
                {
                    ConcurrentWriteDetected = true;
                }
                _sending = true;
            }
            try
            {
                if (delayPerSendMs > 0)
                {
                    await Task.Delay(delayPerSendMs, cancellationToken);
                }
                lock (_gate)
                {
                    _buffer.AddRange(buffer.Array!.Skip(buffer.Offset).Take(buffer.Count));
                    SendCount++;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _sending = false;
                }
            }
        }
    }
}
