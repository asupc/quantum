using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quantum.Data;
using Quantum.Entities.Model;

namespace Quantum.Application.Channels;

/// <summary>
/// 飞书长连接事件入口。
///
/// 生命周期与 QQ/微信通道一致：**默认无账号不连平台**；仅在「已配置 + 已绑定 + 已启用」时建连；
/// 账户被禁用/换绑/解绑会主动断开并停止重连（对齐 RevokeAsync 的原子吊销语义）。
///
/// 两条硬约束（写在这里以免后续重构踩掉）：
/// 1) 事件处理异常**不得外抛到连接层**——飞书侧 3 秒未收到响应会触发重试，异常外溢会放大成重复投递；
/// 2) 只接私聊（chat_type=p2p），本通道是「每平台唯一私聊绑定」，群聊不在授权范围内。
/// </summary>
public sealed class FeishuWssWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ChannelNetwork _network;
    private readonly FeishuChannelClient _client;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connect;

    public FeishuWssWorker(IServiceScopeFactory scopes, ChannelNetwork network, FeishuChannelClient client)
        : this(scopes, network, client, null) { }

    internal FeishuWssWorker(IServiceScopeFactory scopes, ChannelNetwork network, FeishuChannelClient client,
        Func<Uri, CancellationToken, Task<WebSocket>> connect)
    {
        _scopes = scopes;
        _network = network;
        _client = client;
        _connect = connect ?? ConnectAsync;
    }

    private async Task<WebSocket> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        try { await ws.ConnectAsync(endpoint, _network.WebSocketInvoker, ct); return ws; }
        catch { ws.Dispose(); throw; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            ChannelAccountModel account = null;
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
                account = await db.ChannelAccounts.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Platform == ChannelManagementService.Feishu && x.Enabled, stoppingToken);
                if (account?.CredentialCiphertext is not { Length: > 0 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }
                var protector = scope.ServiceProvider.GetRequiredService<ChannelSecretProtector>();
                using var credentials = JsonDocument.Parse(
                    protector.UnprotectCredential(account.CredentialCiphertext, account.Id, "feishu-credentials"));
                var appId = FeishuChannelClient.Text(credentials.RootElement, "AppId");
                var appSecret = FeishuChannelClient.Text(credentials.RootElement, "AppSecret");
                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
                    throw new ChannelProtocolException("FEISHU_CREDENTIAL_INVALID");
                await RunSessionAsync(account, appId, appSecret,
                    seconds => backoff = Math.Clamp(seconds, 1, 300), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // 不写正文/凭据到日志；保留协议错误码定位到具体环节（取地址/鉴权/建连/帧），下一轮退避后重试
                await SafeSetErrorAsync(account,
                    error is ChannelProtocolException p ? p.ErrorCode : "FEISHU_WSS_ERROR", stoppingToken);
                try { await Task.Delay(TimeSpan.FromSeconds(backoff + Random.Shared.Next(0, 3)), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                backoff = Math.Min(backoff * 2, 300);
            }
        }
    }

    private async Task RunSessionAsync(ChannelAccountModel account, string appId, string appSecret,
        Action<int> connected, CancellationToken ct)
    {
        var (url, pingSeconds, reconnectSeconds) = await _client.WssEndpointAsync(appId, appSecret, ct);
        var endpoint = ValidateEndpoint(url);
        var serviceId = ServiceId(endpoint);
        WebSocket socket;
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { socket = await _connect(endpoint, connectTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new ChannelProtocolException("FEISHU_CONNECT_TIMEOUT"); }
        }
        using var ws = socket;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sendGate = new SemaphoreSlim(1, 1);
        using var pong = new SemaphoreSlim(0, 1);
        Task guard = null, receive = null, keepAlive = null;
        try
        {
            await ClearErrorAsync(account, linked.Token);
            connected(reconnectSeconds);
            guard = WatchAccountAsync(account.Id, account.BindingVersion, account.CredentialCiphertext, ws, linked.Token);
            receive = ReceiveLoopAsync(ws, sendGate, pong, account, appId, linked.Token);
            keepAlive = KeepAliveAsync(ws, sendGate, pong, serviceId, TimeSpan.FromSeconds(pingSeconds), linked.Token);
            await AwaitSessionAsync(linked, ws.Abort, receive, keepAlive, guard);
        }
        finally
        {
            linked.Cancel();
            ws.Abort();
        }
    }

    internal static Uri ValidateEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "wss" ||
            endpoint.Port != 443 || !ChannelNetwork.IsFeishuHost(endpoint.Host) || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ChannelProtocolException("FEISHU_WS_URL_DENIED");
        return endpoint;
    }

    internal static int ServiceId(Uri endpoint)
    {
        foreach (var part in endpoint.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == "service_id" &&
                int.TryParse(Uri.UnescapeDataString(pair[1]), out var id) && id >= 0) return id;
        }
        throw new ChannelProtocolException("FEISHU_WS_SERVICE_ID_MISSING");
    }

    /// <summary>必须观察先结束任务的异常；取消并等待其余任务，避免孤儿收发占住旧连接。</summary>
    internal static async Task AwaitSessionAsync(CancellationTokenSource linked, Action abort, params Task[] tasks)
    {
        try { await await Task.WhenAny(tasks); }
        finally
        {
            linked.Cancel();
            abort();
            foreach (var task in tasks)
            {
                try { await task; }
                catch { /* 原始故障由先结束任务传播；清理时仅观察其它任务的异常。 */ }
            }
        }
    }

    /// <summary>接收循环：控制帧回 Pong，数据帧按事件类型分发。响应帧必须尽快回，3 秒是飞书的硬时限。</summary>
    private async Task ReceiveLoopAsync(WebSocket ws, SemaphoreSlim sendGate, SemaphoreSlim pong,
        ChannelAccountModel account, string appId, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await ws.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw new ChannelProtocolException("FEISHU_CONNECTION_CLOSED");
                message.Write(buffer, 0, received.Count);
                // 单条事件上限 8MB：超过即判为异常流并断开，避免被单个畸形帧撑爆内存
                if (message.Length > 8 * 1024 * 1024) throw new ChannelProtocolException("FEISHU_FRAME_TOO_LARGE");
            } while (!received.EndOfMessage);

            FeishuFrame frame;
            try { frame = FeishuFrameCodec.Decode(message.ToArray()); }
            catch (ChannelProtocolException) { continue; } // 单帧解析失败不该拖垮整条连接

            var type = frame.Header("type");
            if (type == "ping")
            {
                await SendFrameAsync(ws, sendGate, Pong(frame), ct);
                continue;
            }
            if (type == "pong")
            {
                try { pong.Release(); } catch (SemaphoreFullException) { /* 重复 Pong 不累计。 */ }
                continue;
            }
            if (string.IsNullOrEmpty(frame.PayloadText)) continue;

            // 事件处理异常一律吞掉并留痕：外抛会让飞书重试，重复投递比丢一条更糟
            try { await DispatchAsync(account.Id, appId, frame, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { await SafeSetErrorAsync(account, "FEISHU_EVENT_FAILED", ct); }

        }
    }

    private async Task DispatchAsync(string accountId, string appId, FeishuFrame frame, CancellationToken ct)
    {
        using var envelope = JsonDocument.Parse(frame.PayloadText);
        var root = envelope.RootElement;
        if (!root.TryGetProperty("header", out var header)) return;
        var eventType = FeishuChannelClient.Text(header, "event_type");
        if (FeishuChannelClient.Text(header, "app_id") is { } incomingApp && !string.Equals(incomingApp, appId, StringComparison.Ordinal))
        {
            // 非本应用的事件直接忽略：长连接是按应用维度的，串台说明订阅配置有问题
            return;
        }
        if (eventType != "im.message.receive_v1") return;
        if (!root.TryGetProperty("event", out var payload)) return;

        var incoming = FeishuChannelClient.ParseIncoming(payload, appId);
        if (!incoming.IsText) return;

        using var scope = _scopes.CreateScope();
        var inbox = scope.ServiceProvider.GetRequiredService<ChannelInboxService>();
        await inbox.AcceptBatchAsync(accountId, ChannelManagementService.Feishu, [incoming], null, null, null, ct);
    }

    internal static FeishuFrame Pong(FeishuFrame ping) => new()
    {
        SeqId = ping.SeqId,
        Service = ping.Service,
        Method = FeishuFrame.MethodControl,
        Headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "pong", ["message_id"] = ping.Header("message_id") ?? ""
        },
        PayloadType = "ping"
    };

    internal static FeishuFrame Ping(int serviceId) => new()
    {
        Service = serviceId,
        Method = FeishuFrame.MethodControl,
        Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "ping" }
    };

    private static async Task SendFrameAsync(WebSocket ws, SemaphoreSlim gate, FeishuFrame frame, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var bytes = FeishuFrameCodec.Encode(frame);
        await gate.WaitAsync(timeout.Token);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, timeout.Token); }
        finally { gate.Release(); }
    }

    /// <summary>按官方帧协议主动 Ping 并等待 Pong；黑洞连接即使 State=Open 也必须断开重连。</summary>
    internal static async Task KeepAliveAsync(WebSocket ws, SemaphoreSlim gate, SemaphoreSlim pong,
        int serviceId, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                while (pong.Wait(0)) { }
                await SendFrameAsync(ws, gate, Ping(serviceId), ct);
                if (!await pong.WaitAsync(interval, ct))
                    throw new ChannelProtocolException("FEISHU_HEARTBEAT_TIMEOUT");
                await Task.Delay(interval, ct);
            }
        }
        catch
        {
            ws.Abort();
            throw;
        }
    }

    private async Task WatchAccountAsync(string accountId, int version, string credential, WebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
            var state = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (state is null || !state.Enabled || state.BindingVersion != version || state.CredentialCiphertext != credential)
            {
                ws.Abort();
                return;
            }
        }
    }

    private async Task SafeSetErrorAsync(ChannelAccountModel account, string code, CancellationToken ct)
    {
        if (account is null) return;
        await UpdateErrorAsync(account, code[..Math.Min(code.Length, 128)], ct);
    }

    private async Task ClearErrorAsync(ChannelAccountModel account, CancellationToken ct) =>
        await UpdateErrorAsync(account, null, ct);

    /// <summary>DB 不可用不应影响其它通道与主应用，故状态写失败只留待下一轮。</summary>
    private async Task UpdateErrorAsync(ChannelAccountModel account, string code, CancellationToken ct)
    {
        if (account is null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
            if (code is null)
                await db.ChannelAccounts.Where(x => x.Id == account.Id && x.Enabled &&
                    x.BindingVersion == account.BindingVersion && x.CredentialCiphertext == account.CredentialCiphertext &&
                    x.LastErrorCode != null)
                    .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastErrorCode, (string)null), ct);
            else
                await db.ChannelAccounts.Where(x => x.Id == account.Id && x.Enabled &&
                    x.BindingVersion == account.BindingVersion && x.CredentialCiphertext == account.CredentialCiphertext)
                    .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastErrorCode, code), ct);
        }
        catch { /* 下轮重试 */ }
    }
}
