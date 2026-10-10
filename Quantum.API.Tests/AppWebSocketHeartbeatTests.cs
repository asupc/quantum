using System.Net.WebSockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;

namespace Quantum.API.Tests;

/// <summary>
/// 心跳巡检的「关闭必须有界」回归（与其它静态态用例同 Collection 串行）：
/// 黑洞对端（TCP 零窗口/不完成关闭握手）的 CloseAsync 可无限挂起，而巡检是单条 PeriodicTimer 循环——
/// 一次挂起即让循环体永不返回，此后所有巡检停摆、死连接只增不减（与 SendTimeout 同一类防线）。
/// 修复后：单连接关闭有界（超时 Abort 强制拆链），且逐条隔离——一条坏连接不阻塞同批其余连接。
/// </summary>
[Collection("ConstsState")]
public class AppWebSocketHeartbeatTests : IDisposable
{
    private readonly TimeSpan _originalHeartbeat = AppWebSocketHeartbeatService.HeartbeatTimeout;
    private readonly TimeSpan _originalClose = AppWebSocketHeartbeatService.CloseTimeout;
    private readonly TimeSpan _originalScan = AppWebSocketHeartbeatService.ScanInterval;

    public void Dispose()
    {
        AppWebSocketHeartbeatService.HeartbeatTimeout = _originalHeartbeat;
        AppWebSocketHeartbeatService.CloseTimeout = _originalClose;
        AppWebSocketHeartbeatService.ScanInterval = _originalScan;
    }

    private static AppWebSocketManager CreateManager() => new(NullLogger<AppWebSocketManager>.Instance);

    [Fact]
    public async Task Sweep_BlackHoleClose_TimesOutAbortsAndStillClosesOtherSockets()
    {
        // 同批两条死连接：第一条是黑洞（CloseAsync 永不完成、忽略取消），第二条正常。
        // 黑洞必须被有界超时打断并 Abort，且不得妨碍同一批里第二条被关闭。
        var manager = CreateManager();
        var blackHole = new BlackHoleCloseSocket();
        var normal = new NormalClosableSocket();
        manager.Add(blackHole);
        manager.Add(normal);
        MarkSilent(manager, blackHole, TimeSpan.FromSeconds(5));
        MarkSilent(manager, normal, TimeSpan.FromSeconds(5));
        AppWebSocketHeartbeatService.HeartbeatTimeout = TimeSpan.FromMilliseconds(10);
        AppWebSocketHeartbeatService.CloseTimeout = TimeSpan.FromMilliseconds(100);
        var service = new AppWebSocketHeartbeatService(manager);

        // 巡检必须在「关闭超时 + 余量」内跑完（修复前此处永久挂起）
        var sweep = service.SweepOnceAsync();
        Assert.True(await Task.WhenAny(sweep, Task.Delay(TimeSpan.FromSeconds(5))) == sweep,
            "巡检未在有界时间内完成：黑洞连接的 CloseAsync 挂死了整轮扫描");

        Assert.Equal(WebSocketState.Aborted, blackHole.State);
        Assert.Equal(WebSocketState.Closed, normal.State);
        Assert.Equal(0, manager.ConnectionCount());
    }

    [Fact]
    public async Task HeartbeatLoop_BlackHoleClose_DoesNotStallFollowingSweeps()
    {
        // 循环级验证：黑洞连接占住第一轮扫描时，第二轮加入的连接仍必须被踢除
        // （修复前循环卡在第一轮的 CloseAsync 上，此后永不推进）。
        var manager = CreateManager();
        var blackHole = new BlackHoleCloseSocket();
        manager.Add(blackHole);
        MarkSilent(manager, blackHole, TimeSpan.FromSeconds(5));
        AppWebSocketHeartbeatService.HeartbeatTimeout = TimeSpan.FromMilliseconds(10);
        AppWebSocketHeartbeatService.CloseTimeout = TimeSpan.FromMilliseconds(50);
        AppWebSocketHeartbeatService.ScanInterval = TimeSpan.FromMilliseconds(30);
        var service = new AppWebSocketHeartbeatService(manager);
        using var stop = new CancellationTokenSource();
        var loop = StartLoop(service, stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => blackHole.State == WebSocketState.Aborted, TimeSpan.FromSeconds(5), "黑洞连接未被有界超时 Abort"));

            // 后续连接（第一轮扫描时还不存在）：循环仍在推进才可能扫到它
            var later = new NormalClosableSocket();
            manager.Add(later);
            MarkSilent(manager, later, TimeSpan.FromSeconds(5));

            Assert.True(await WaitUntilAsync(
                () => later.State == WebSocketState.Closed, TimeSpan.FromSeconds(5), "后续巡检未执行：循环被黑洞关闭挂死"));
            Assert.Equal(0, manager.ConnectionCount());
        }
        finally
        {
            stop.Cancel();
            Assert.True(await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5))) == loop,
                "停机取消后巡检循环未退出");
        }
    }

    [Fact]
    public async Task Sweep_CloseThrows_IsIsolatedAndAborts()
    {
        // 关闭抛异常（连接已死）同样不得打断同批其余连接，且套接字必须落到 Abort
        var manager = CreateManager();
        var throwing = new ThrowingCloseSocket();
        var normal = new NormalClosableSocket();
        manager.Add(throwing);
        manager.Add(normal);
        MarkSilent(manager, throwing, TimeSpan.FromSeconds(5));
        MarkSilent(manager, normal, TimeSpan.FromSeconds(5));
        AppWebSocketHeartbeatService.HeartbeatTimeout = TimeSpan.FromMilliseconds(10);
        var service = new AppWebSocketHeartbeatService(manager);

        await service.SweepOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketState.Aborted, throwing.State);
        Assert.Equal(WebSocketState.Closed, normal.State);
        Assert.Equal(0, manager.ConnectionCount());
    }

    [Fact]
    public void CloseTimeout_IsBoundedAndShorterThanHeartbeatInterval()
    {
        // 关闭上限必须远小于心跳超时（否则「有界」等于没界）；默认 5s
        Assert.True(AppWebSocketHeartbeatService.CloseTimeout > TimeSpan.Zero);
        Assert.True(AppWebSocketHeartbeatService.CloseTimeout <= TimeSpan.FromSeconds(5));
        Assert.True(AppWebSocketHeartbeatService.CloseTimeout < AppWebSocketHeartbeatService.HeartbeatTimeout);
    }

    private static Task StartLoop(AppWebSocketHeartbeatService service, CancellationToken token)
        => (Task)typeof(AppWebSocketHeartbeatService)
            .GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [token])!;

    private static async Task<bool> WaitUntilAsync(Func<bool> probe, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe())
            {
                return true;
            }
            await Task.Delay(20);
        }
        Assert.Fail(message);
        return false;
    }

    private static void MarkSilent(AppWebSocketManager manager, WebSocket socket, TimeSpan ago)
    {
        // LastPongTicks 是 AppConnection 的私有字段，通过 List<AppConnection> 反射置旧（与 AppWebSocketManagerTests 同法）
        var list = (System.Collections.IEnumerable)manager.GetType()
            .GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)!
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

    /// <summary>黑洞连接：CloseAsync 返回永不完成的任务且完全无视取消令牌（模拟 TCP 零窗口对端）。</summary>
    private class BlackHoleCloseSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketCloseStatus? CloseStatus => null;

        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => new TaskCompletionSource().Task;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new InvalidOperationException("BlackHoleCloseSocket 不产生入站帧");
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>正常可关闭连接：CloseAsync 立即完成。</summary>
    private sealed class NormalClosableSocket : WebSocket
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
            => throw new InvalidOperationException("NormalClosableSocket 不产生入站帧");
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>关闭即抛的连接（对端已消失）：验证异常隔离。</summary>
    private sealed class ThrowingCloseSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketCloseStatus? CloseStatus => null;

        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new WebSocketException("connection already gone");
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new InvalidOperationException("ThrowingCloseSocket 不产生入站帧");
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
