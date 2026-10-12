using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Application.Channels;
using Quantum.Entities.Model;

namespace Quantum.API.Tests;

/// <summary>通道断网/黑洞/登录失效离线回归：不访问平台，不使用真实账号或凭据。</summary>
public sealed class ChannelReconnectTests
{
    [Fact]
    public void FeishuHeartbeat_UsesEndpointServiceId_AndControlFrame()
    {
        var endpoint = FeishuWssWorker.ValidateEndpoint("wss://msg-frontier.feishu.cn/ws?device_id=test&service_id=42");
        var frame = FeishuFrameCodec.Decode(FeishuFrameCodec.Encode(FeishuWssWorker.Ping(FeishuWssWorker.ServiceId(endpoint))));
        Assert.Equal(42, frame.Service);
        Assert.Equal(FeishuFrame.MethodControl, frame.Method);
        Assert.Equal("ping", frame.Header("type"));
        Assert.Empty(frame.Payload);
        Assert.Equal(42, FeishuWssWorker.Pong(frame).Service);
    }

    [Theory]
    [InlineData("wss://attacker.example/ws?service_id=1")]
    [InlineData("ws://msg-frontier.feishu.cn/ws?service_id=1")]
    [InlineData("wss://user@msg-frontier.feishu.cn/ws?service_id=1")]
    [InlineData("wss://msg-frontier.feishu.cn:8443/ws?service_id=1")]
    public void FeishuReconnect_RejectsUntrustedEndpoint(string url)
        => Assert.Equal("FEISHU_WS_URL_DENIED", Assert.Throws<ChannelProtocolException>(
            () => FeishuWssWorker.ValidateEndpoint(url)).ErrorCode);

    [Fact]
    public async Task FeishuMissingPong_AbortsBlackholeConnection()
    {
        using var ws = new TestWebSocket();
        using var gate = new SemaphoreSlim(1, 1);
        using var pong = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<ChannelProtocolException>(() =>
            FeishuWssWorker.KeepAliveAsync(ws, gate, pong, 42, TimeSpan.FromMilliseconds(30), stop.Token));
        Assert.Equal("FEISHU_HEARTBEAT_TIMEOUT", error.ErrorCode);
        Assert.True(ws.Aborted);
        Assert.Single(ws.Sent);
    }

    [Fact]
    public async Task FeishuPong_KeepsConnectionAlive_AndShutdownCancelsCleanly()
    {
        using var ws = new TestWebSocket();
        using var gate = new SemaphoreSlim(1, 1);
        using var pong = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ws.OnSend = () => { pong.Release(); if (ws.Sent.Count >= 3) stop.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FeishuWssWorker.KeepAliveAsync(ws, gate, pong, 42, TimeSpan.FromMilliseconds(10), stop.Token));
        Assert.Equal(3, ws.Sent.Count);
    }

    [Fact]
    public async Task FeishuSessionFault_IsPropagated_AndOtherTasksAreAwaited()
    {
        using var linked = new CancellationTokenSource();
        var receive = Task.FromException(new ChannelProtocolException("FEISHU_CONNECTION_CLOSED"));
        var heartbeat = Task.Delay(Timeout.Infinite, linked.Token);
        var guard = Task.Delay(Timeout.Infinite, linked.Token);
        var aborted = false;
        var error = await Assert.ThrowsAsync<ChannelProtocolException>(() =>
            FeishuWssWorker.AwaitSessionAsync(linked, () => aborted = true, receive, heartbeat, guard));
        Assert.Equal("FEISHU_CONNECTION_CLOSED", error.ErrorCode);
        Assert.True(aborted);
        Assert.True(heartbeat.IsCanceled);
        Assert.True(guard.IsCanceled);
    }

    [Fact]
    public async Task FeishuAccountDisabled_CancelsBothConnectionTasks()
    {
        using var linked = new CancellationTokenSource();
        var receive = Task.Delay(Timeout.Infinite, linked.Token);
        var heartbeat = Task.Delay(Timeout.Infinite, linked.Token);
        await FeishuWssWorker.AwaitSessionAsync(linked, () => { }, receive, heartbeat, Task.CompletedTask);
        Assert.True(receive.IsCanceled);
        Assert.True(heartbeat.IsCanceled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeartbeatSendFailure_WakesBlockedReceive(bool feishu)
    {
        using var ws = new TestWebSocket { OnSend = () => throw new WebSocketException() };
        using var gate = new SemaphoreSlim(1, 1);
        using var pong = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        if (feishu)
            await Assert.ThrowsAsync<WebSocketException>(() =>
                FeishuWssWorker.KeepAliveAsync(ws, gate, pong, 42, TimeSpan.FromMilliseconds(10), stop.Token));
        else
            await Assert.ThrowsAsync<WebSocketException>(() =>
                QqChannelWorker.HeartbeatAsync(ws, gate, () => 7, () => 0, _ => { }, 10, stop.Token));
        Assert.True(ws.Aborted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2147483648L)]
    public async Task QqMissingAck_AbortsConnection_AndUsesPersistedSequence(long sequence)
    {
        using var ws = new TestWebSocket();
        using var gate = new SemaphoreSlim(1, 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var pending = 0;
        var error = await Assert.ThrowsAsync<ChannelProtocolException>(() =>
            QqChannelWorker.HeartbeatAsync(ws, gate, () => sequence,
                () => Volatile.Read(ref pending), value => Interlocked.Exchange(ref pending, value), 10, stop.Token));
        Assert.Equal("QQ_HEARTBEAT_TIMEOUT", error.ErrorCode);
        Assert.True(ws.Aborted);
        using var frame = JsonDocument.Parse(Assert.Single(ws.Sent));
        Assert.Equal(1, frame.RootElement.GetProperty("op").GetInt32());
        if (sequence < 0) Assert.Equal(JsonValueKind.Null, frame.RootElement.GetProperty("d").ValueKind);
        else Assert.Equal(sequence, frame.RootElement.GetProperty("d").GetInt64());
    }

    [Theory]
    [InlineData(4004, "QQ_TOKEN_EXPIRED")]
    [InlineData(4006, "QQ_SESSION_INVALID")]
    [InlineData(4007, "QQ_SESSION_INVALID")]
    [InlineData(4009, "QQ_SESSION_INVALID")]
    [InlineData(4900, "QQ_SESSION_INVALID")]
    [InlineData(4913, "QQ_SESSION_INVALID")]
    [InlineData(4008, "QQ_RATE_LIMITED")]
    public async Task QqExpiredGatewaySession_RequestsFreshIdentifyWithoutDeletingCredentials(int status, string code)
    {
        using var ws = new TestWebSocket { ReceivedCloseStatus = (WebSocketCloseStatus)status };
        var error = await Assert.ThrowsAsync<ChannelProtocolException>(() => QqChannelWorker.ReceiveAsync(ws, CancellationToken.None));
        Assert.Equal(code, error.ErrorCode);
    }

    [Fact]
    public async Task QqExpiredToken_RefreshesFromSavedAppCredentials_AndStaleFailureCannotDropNewToken()
    {
        var requests = 0;
        using var network = new ChannelNetwork(new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { access_token = "test-token-" + ++requests, expires_in = "7200" }))
        })), TimeSpan.FromSeconds(3));
        var client = new QqChannelClient(network);
        var first = await client.TokenAsync("test-app", "test-secret", null, CancellationToken.None);
        Assert.Equal(first, await client.TokenAsync("test-app", "test-secret", null, CancellationToken.None));
        await client.InvalidateTokenAsync(first, CancellationToken.None);
        var refreshed = await client.TokenAsync("test-app", "test-secret", null, CancellationToken.None);
        Assert.NotEqual(first, refreshed);
        await client.InvalidateTokenAsync(first, CancellationToken.None);
        Assert.Equal(refreshed, await client.TokenAsync("test-app", "test-secret", null, CancellationToken.None));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpTimeout_CoversHeadersAndBody_AndIsRetryable(bool bodyStalls)
    {
        using var network = new ChannelNetwork(new TestHandler(async (_, ct) =>
        {
            if (!bodyStalls) await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent() };
        }), TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<ChannelProtocolException>(() =>
            network.PostAsync(ChannelNetwork.WeixinUri("ilink/bot/getupdates", "https://ilinkai.weixin.qq.com"), new { }, ct: CancellationToken.None));
        Assert.Equal("NETWORK_TIMEOUT", error.ErrorCode);
    }

    [Fact]
    public async Task HttpCancellationFromDisableOrShutdown_IsNotMisreportedAsNetworkFailure()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        using var network = new ChannelNetwork(new TestHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            network.GetAsync(ChannelNetwork.QqUri("/gateway"), ct: stop.Token));
    }

    [Theory]
    [InlineData("NETWORK_TIMEOUT", true)]
    [InlineData("NETWORK_ERROR", true)]
    [InlineData("WEIXIN_RET_500", true)]
    [InlineData("WEIXIN_SESSION_EXPIRED", false)]
    public async Task WeixinTemporaryFailure_PreservesCredentialsAndCheckpoint_OnlyRealExpiryDisables(string code, bool enabled)
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var account = new ChannelAccountModel { Platform = ChannelManagementService.Weixin,
                BotId = "test-bot", Enabled = true, CredentialCiphertext = "test-encrypted-credential" };
            db.ChannelAccounts.Add(account);
            db.ChannelCursors.Add(new ChannelCursorModel { AccountId = account.Id, WeixinCursorCiphertext = "test-checkpoint" });
            await db.SaveChangesAsync();
            await WeixinChannelWorker.UpdateConnectionErrorAsync(db, account, code, CancellationToken.None);
            var state = await db.ChannelAccounts.AsNoTracking().SingleAsync();
            Assert.Equal(enabled, state.Enabled);
            Assert.Equal(code, state.LastErrorCode);
            Assert.Equal(account.CredentialCiphertext, state.CredentialCiphertext);
            Assert.Equal("test-checkpoint", (await db.ChannelCursors.AsNoTracking().SingleAsync()).WeixinCursorCiphertext);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WeixinOldSessionExpiry_CannotDisableNewQrSession(bool changedVersion)
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var account = new ChannelAccountModel { Platform = ChannelManagementService.Weixin,
                BotId = "test-bot", Enabled = true, CredentialCiphertext = "old-credential" };
            db.ChannelAccounts.Add(account);
            await db.SaveChangesAsync();
            var old = await db.ChannelAccounts.AsNoTracking().SingleAsync();
            if (changedVersion) account.BindingVersion++;
            else account.CredentialCiphertext = "new-credential";
            await db.SaveChangesAsync();
            await WeixinChannelWorker.UpdateConnectionErrorAsync(db, old, "WEIXIN_SESSION_EXPIRED", CancellationToken.None);
            var state = await db.ChannelAccounts.AsNoTracking().SingleAsync();
            Assert.True(state.Enabled);
            Assert.Null(state.LastErrorCode);
        }
    }

    [Fact]
    public void MasterKeyMismatch_IsNotMisreportedAsPlatformLogoutOrNetworkFailure()
    {
        var first = new ChannelSecretProtector(RandomNumberGenerator.GetBytes(32));
        var second = new ChannelSecretProtector(RandomNumberGenerator.GetBytes(32));
        var cipher = first.Protect("test-credential", "test-account", "weixin-credentials");
        var error = Assert.Throws<ChannelProtocolException>(() => second.UnprotectCredential(cipher, "test-account", "weixin-credentials"));
        Assert.Equal("CHANNEL_CREDENTIAL_UNREADABLE", error.ErrorCode);
    }

    [Theory]
    [InlineData(4009, 2)]
    [InlineData(1000, 6)]
    public async Task QqWorker_DisconnectsAndAutomaticallyRestoresSession_WithSavedCredentials(int closeCode, int expectedOp)
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var protector = new ChannelSecretProtector(RandomNumberGenerator.GetBytes(32));
            var account = CreateAccount(protector, ChannelManagementService.Qq,
                "qq-credentials", new { AppId = "test-app", AppSecret = "test-secret" });
            db.ChannelAccounts.Add(account);
            db.ChannelCursors.Add(new ChannelCursorModel { AccountId = account.Id, QqSessionId = "previous-session", QqSequence = 3 });
            await db.SaveChangesAsync();
            using var services = Services(db, protector);
            using var network = new ChannelNetwork(new TestHandler((request, _) => Task.FromResult(JsonResponse(
                request.RequestUri!.AbsolutePath.Contains("getAppAccessToken")
                    ? new { access_token = "test-token", expires_in = "7200" }
                    : (object)new { url = "wss://api.bot.qq.com/gateway" }))), TimeSpan.FromSeconds(3));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new TestWebSocket { ReceivedCloseStatus = (WebSocketCloseStatus)closeCode };
            var second = new TestWebSocket { BlockReceive = true, OnReceiveBlocked = () => ready.TrySetResult() };
            second.Incoming.Enqueue(Encoding.UTF8.GetBytes("""{"op":10,"d":{"heartbeat_interval":5000}}"""));
            second.Incoming.Enqueue(Encoding.UTF8.GetBytes(expectedOp == 2
                ? """{"op":0,"t":"READY","s":4,"d":{"session_id":"fresh-session"}}"""
                : """{"op":0,"t":"RESUMED","s":4,"d":{}}"""));
            var connections = 0;
            using var worker = new QqChannelWorker(services.GetRequiredService<IServiceScopeFactory>(), network,
                new QqChannelClient(network), (_, _) => Task.FromResult<WebSocket>(++connections == 1 ? first : second));
            await RunWorkerUntilAsync(worker, ready.Task);
            var state = await db.ChannelAccounts.AsNoTracking().SingleAsync();
            Assert.Equal(2, connections);
            Assert.True(state.Enabled);
            Assert.Equal(account.CredentialCiphertext, state.CredentialCiphertext);
            Assert.Null(state.LastErrorCode);
            Assert.Equal(expectedOp == 2 ? "fresh-session" : "previous-session",
                (await db.ChannelCursors.AsNoTracking().SingleAsync()).QqSessionId);
            using var identify = JsonDocument.Parse(Assert.Single(second.Sent));
            Assert.Equal(expectedOp, identify.RootElement.GetProperty("op").GetInt32()); // 有效会话 Resume，失效会话重新 Identify。
        }
    }

    [Fact]
    public async Task FeishuWorker_DisconnectsAndAutomaticallyReconnects_WithSavedQrCredentials()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var protector = new ChannelSecretProtector(RandomNumberGenerator.GetBytes(32));
            var account = CreateAccount(protector, ChannelManagementService.Feishu,
                "feishu-credentials", new { AppId = "test-app", AppSecret = "test-secret" });
            db.ChannelAccounts.Add(account);
            await db.SaveChangesAsync();
            using var services = Services(db, protector);
            using var network = new ChannelNetwork(new TestHandler((_, _) => Task.FromResult(JsonResponse(new
            {
                code = 0, data = new { URL = "wss://msg-frontier.feishu.cn/ws?service_id=7",
                    ClientConfig = new { PingInterval = 15, ReconnectInterval = 1 } }
            }))), TimeSpan.FromSeconds(3));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connections = 0;
            using var worker = new FeishuWssWorker(services.GetRequiredService<IServiceScopeFactory>(), network,
                new FeishuChannelClient(network), (_, _) => Task.FromResult<WebSocket>(++connections == 1
                    ? new TestWebSocket { ReceivedCloseStatus = WebSocketCloseStatus.NormalClosure }
                    : new TestWebSocket { BlockReceive = true, OnReceiveBlocked = () => ready.TrySetResult() }));
            await RunWorkerUntilAsync(worker, ready.Task);
            var state = await db.ChannelAccounts.AsNoTracking().SingleAsync();
            Assert.Equal(2, connections);
            Assert.True(state.Enabled);
            Assert.Equal(account.CredentialCiphertext, state.CredentialCiphertext);
            Assert.Null(state.LastErrorCode);
        }
    }

    [Fact]
    public async Task WeixinWorker_NetworkRecoveryAndRestart_ReuseSavedTokenAndDurableCursor()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var protector = new ChannelSecretProtector(RandomNumberGenerator.GetBytes(32));
            var account = CreateAccount(protector, ChannelManagementService.Weixin,
                "weixin-credentials", new { BotToken = "test-bot-token", ApiBase = "https://ilinkai.weixin.qq.com" });
            db.ChannelAccounts.Add(account);
            db.ChannelCursors.Add(new ChannelCursorModel { AccountId = account.Id,
                WeixinCursorCiphertext = protector.Protect("old-checkpoint", account.Id, "weixin-cursor") });
            await db.SaveChangesAsync();
            using var services = Services(db, protector);
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            var checkpoints = new ConcurrentQueue<string?>();
            using var network = new ChannelNetwork(new TestHandler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                checkpoints.Enqueue(body.RootElement.GetProperty("get_updates_buf").GetString());
                Assert.Equal("test-bot-token", request.Headers.Authorization?.Parameter);
                var attempt = ++requests;
                if (attempt == 1) throw new HttpRequestException();
                if (attempt is 3 or 5)
                {
                    (attempt == 3 ? recovered : restarted).TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return JsonResponse(new { msgs = Array.Empty<object>(), get_updates_buf = "new-checkpoint" });
            }), TimeSpan.FromSeconds(3));
            using (var worker = new WeixinChannelWorker(services.GetRequiredService<IServiceScopeFactory>(), network))
                await RunWorkerUntilAsync(worker, recovered.Task);
            using (var worker = new WeixinChannelWorker(services.GetRequiredService<IServiceScopeFactory>(), network))
                await RunWorkerUntilAsync(worker, restarted.Task);
            var state = await db.ChannelAccounts.AsNoTracking().SingleAsync();
            Assert.True(state.Enabled);
            Assert.Null(state.LastErrorCode);
            Assert.Equal(account.CredentialCiphertext, state.CredentialCiphertext);
            Assert.Equal(new[] { "old-checkpoint", "old-checkpoint", "new-checkpoint", "new-checkpoint", "new-checkpoint" }, checkpoints.ToArray());
        }
    }

    private static ChannelAccountModel CreateAccount(ChannelSecretProtector protector, string platform, string purpose, object credentials)
    {
        var account = new ChannelAccountModel { Platform = platform, BotId = "test-bot", Enabled = true };
        account.CredentialCiphertext = protector.Protect(JsonSerializer.Serialize(credentials), account.Id, purpose);
        return account;
    }

    private static ServiceProvider Services(IQuantumDbContext db, ChannelSecretProtector protector)
    {
        var inbox = new ChannelInboxService(db, protector);
        return new ServiceCollection().AddSingleton(db).AddSingleton(protector).AddSingleton(inbox).BuildServiceProvider();
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value)) };

    private static async Task RunWorkerUntilAsync(BackgroundService worker, Task recovered)
    {
        await worker.StartAsync(CancellationToken.None);
        try { await recovered.WaitAsync(TimeSpan.FromSeconds(12)); }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StopAsync(stop.Token);
        }
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class BlockingContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
            => Task.Delay(Timeout.Infinite, ct);
    }

    private sealed class TestWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private readonly CancellationTokenSource _abort = new();
        public bool BlockReceive { get; init; }
        public Action? OnReceiveBlocked { get; init; }
        public ConcurrentQueue<byte[]> Incoming { get; } = new();
        public bool Aborted { get; private set; }
        public WebSocketCloseStatus? ReceivedCloseStatus { get; init; }
        public Action? OnSend { get; set; }
        public ConcurrentQueue<byte[]> Sent { get; } = new();
        public override WebSocketCloseStatus? CloseStatus => ReceivedCloseStatus;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => _state;
        public override void Abort() { Aborted = true; _state = WebSocketState.Aborted; _abort.Cancel(); }
        public override void Dispose() { _state = WebSocketState.Closed; _abort.Dispose(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (Incoming.TryDequeue(out var bytes))
            {
                bytes.AsSpan().CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            }
            if (BlockReceive)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _abort.Token);
                OnReceiveBlocked?.Invoke();
                await Task.Delay(Timeout.Infinite, linked.Token);
            }
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, ReceivedCloseStatus, null);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            Sent.Enqueue(buffer.ToArray());
            OnSend?.Invoke();
            return Task.CompletedTask;
        }
    }
}
