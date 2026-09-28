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

    public FeishuWssWorker(IServiceScopeFactory scopes, ChannelNetwork network, FeishuChannelClient client)
    {
        _scopes = scopes;
        _network = network;
        _client = client;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            string accountId = null;
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
                var account = await db.ChannelAccounts.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Platform == ChannelManagementService.Feishu && x.Enabled, stoppingToken);
                if (account?.CredentialCiphertext is not { Length: > 0 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }
                accountId = account.Id;
                var protector = scope.ServiceProvider.GetRequiredService<ChannelSecretProtector>();
                using var credentials = JsonDocument.Parse(
                    protector.Unprotect(account.CredentialCiphertext, account.Id, "feishu-credentials"));
                var appId = FeishuChannelClient.Text(credentials.RootElement, "AppId");
                var appSecret = FeishuChannelClient.Text(credentials.RootElement, "AppSecret");
                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
                    throw new ChannelProtocolException("FEISHU_CREDENTIAL_INVALID");
                await RunSessionAsync(account, appId, appSecret, stoppingToken);
                backoff = 2;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // 不写正文/凭据到日志；保留协议错误码定位到具体环节（取地址/鉴权/建连/帧），下一轮退避后重试
                await SafeSetErrorAsync(accountId,
                    error is ChannelProtocolException p ? p.ErrorCode : "FEISHU_WSS_ERROR", stoppingToken);
                try { await Task.Delay(TimeSpan.FromSeconds(backoff + Random.Shared.Next(0, 3)), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                backoff = Math.Min(backoff * 2, 300);
            }
        }
    }

    private async Task RunSessionAsync(ChannelAccountModel account, string appId, string appSecret, CancellationToken ct)
    {
        var (url, pingSeconds, reconnectSeconds) = await _client.WssEndpointAsync(appId, appSecret, ct);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "wss")
            throw new ChannelProtocolException("FEISHU_WS_URL_DENIED");

        using var ws = new ClientWebSocket();
        ws.Options.Proxy = null; // 通道出站固定官方域名，不走代理
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var guard = WatchAccountAsync(account.Id, account.BindingVersion, account.CredentialCiphertext, ws, linked.Token);
        var pong = new SemaphoreSlim(0, 1);
        try
        {
            await ws.ConnectAsync(endpoint, linked.Token);
            // 建连即视为恢复正常：错误码只在真正连不上时保留，否则管理页会一直挂着「连接异常」
            await ClearErrorAsync(account.Id, linked.Token);
            var keepAlive = KeepAliveAsync(ws, linked.Token, TimeSpan.FromSeconds(pingSeconds));
            var receive = ReceiveLoopAsync(ws, account.Id, appId, appSecret, linked.Token, reconnectSeconds);
            await Task.WhenAny(receive, keepAlive);
        }
        finally
        {
            linked.Cancel();
            try { ws.Abort(); } catch { /* 已断开 */ }
            try { await guard; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>接收循环：控制帧回 Pong，数据帧按事件类型分发。响应帧必须尽快回，3 秒是飞书的硬时限。</summary>
    private async Task ReceiveLoopAsync(ClientWebSocket ws, string accountId, string appId, string appSecret,
        CancellationToken ct, int reconnectSeconds)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await ws.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close) return;
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
                await SendFrameAsync(ws, Pong(frame), ct);
                continue;
            }
            if (type == "pong" || string.IsNullOrEmpty(frame.PayloadText)) continue;

            // 事件处理异常一律吞掉并留痕：外抛会让飞书重试，重复投递比丢一条更糟
            try { await DispatchAsync(accountId, appId, frame, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { await SafeSetErrorAsync(accountId, "FEISHU_EVENT_FAILED", ct); }

            if (reconnectSeconds > 0) { /* 保留参数：后续可按服务端下发值做差异化重连策略 */ }
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

    private static FeishuFrame Pong(FeishuFrame ping) => new()
    {
        SeqId = ping.SeqId,
        Method = FeishuFrame.MethodControl,
        Headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "pong", ["message_id"] = ping.Header("message_id") ?? ""
        },
        PayloadType = "ping"
    };

    private static async Task SendFrameAsync(ClientWebSocket ws, FeishuFrame frame, CancellationToken ct)
    {
        var bytes = FeishuFrameCodec.Encode(frame);
        await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, ct);
    }

    /// <summary>服务端下发的 PingInterval 用来保活；同时兼作连接存活探测。</summary>
    private static async Task KeepAliveAsync(ClientWebSocket ws, CancellationToken ct, TimeSpan interval)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            if (ws.State != WebSocketState.Open) return;
        }
    }

    private async Task WatchAccountAsync(string accountId, int version, string credential, ClientWebSocket ws, CancellationToken ct)
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

    private async Task SafeSetErrorAsync(string accountId, string code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accountId)) return;
        await UpdateErrorAsync(accountId, code[..Math.Min(code.Length, 128)], ct);
    }

    private async Task ClearErrorAsync(string accountId, CancellationToken ct) =>
        await UpdateErrorAsync(accountId, null, ct);

    /// <summary>DB 不可用不应影响其它通道与主应用，故状态写失败只留待下一轮。</summary>
    private async Task UpdateErrorAsync(string accountId, string code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accountId)) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
            if (code is null)
                await db.ChannelAccounts.Where(x => x.Id == accountId && x.LastErrorCode != null)
                    .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastErrorCode, (string)null), ct);
            else
                await db.ChannelAccounts.Where(x => x.Id == accountId)
                    .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastErrorCode, code), ct);
        }
        catch { /* 下轮重试 */ }
    }
}
