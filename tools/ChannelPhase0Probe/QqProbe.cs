using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;

internal static class QqProbe
{
    private const int C2cIntent = 1 << 25;

    internal static async Task RunAsync(string challenge, CancellationToken ct, bool captureOnly = false)
    {
        var appId = Probe.Required("P0_QQ_APP_ID");
        var appSecret = Probe.Required("P0_QQ_APP_SECRET");
        var expectedPeer = captureOnly ? Probe.Required("P0_QQ_PEER_OPENID") : Probe.Optional("P0_QQ_PEER_OPENID");
        string? session = null;
        int? sequence = null;
        while (!ct.IsCancellationRequested)
        {
            var (token, refreshAt) = await RequestTokenAsync(appId, appSecret, ct);
            using var gatewayRequest = new HttpRequestMessage(HttpMethod.Get, Probe.QqUri("/gateway/bot"));
            gatewayRequest.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
            var gateway = await Probe.SendAsync(gatewayRequest, ct);
            var gatewayUrl = Probe.Text(gateway, "url") ?? throw new ProbeException("QQ 未返回 Gateway 地址。");
            if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var address) || address.Scheme != "wss" ||
                !string.IsNullOrEmpty(address.UserInfo) ||
                !(address.Host.Equals("qq.com", StringComparison.OrdinalIgnoreCase) ||
                  address.Host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase)))
                throw new ProbeException("QQ Gateway 目标不是可信 wss://*.qq.com 地址。");
            var limit = Probe.Field(gateway, "session_start_limit");
            if (limit is { } quota && Probe.Int(quota, "remaining") is 0)
            {
                var reset = Math.Clamp(Probe.Int(quota, "reset_after") ?? 60000, 5000, 300000);
                Console.WriteLine($"QQ session_start_limit 耗尽，等待 {reset / 1000} 秒。");
                await Task.Delay(reset, ct);
                continue;
            }
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan; // 仅使用官方 Op1 心跳。
            await ws.ConnectAsync(address, ProbeNetwork.WebSocketInvoker, ct);
            Console.WriteLine(session is null ? "QQ Gateway 已连通，等待测试消息。" : "QQ Gateway 重新连接，尝试 Resume。");
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var sendLock = new SemaphoreSlim(1, 1);
            Task? heartbeat = null;
            var ackPending = 0;
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var frame = await ReceiveAsync(ws, ct);
                    if (frame is null) break;
                    using var json = JsonDocument.Parse(frame);
                    var root = json.RootElement;
                    switch (Probe.Int(root, "op"))
                    {
                        case 10:
                            var hello = Probe.Field(root, "d") ?? throw new ProbeException("QQ Hello 无心跳配置。");
                            var interval = Math.Clamp(Probe.Int(hello, "heartbeat_interval") ?? 0, 5000, 300000);
                            if (Probe.Int(hello, "heartbeat_interval") is null or <= 0)
                                throw new ProbeException("QQ Hello 的 heartbeat_interval 无效。");
                            if (session is null || sequence is null)
                                await SendAsync(ws, sendLock, new
                                {
                                    op = 2,
                                    d = new { token = "QQBot " + token, intents = C2cIntent, shard = new[] { 0, 1 }, properties = new Dictionary<string, string>
                                    {
                                        ["$os"] = Environment.OSVersion.Platform.ToString().ToLowerInvariant(),
                                        ["$browser"] = "quantum-phase0", ["$device"] = "quantum-phase0"
                                    } }
                                }, ct);
                            else
                                await SendAsync(ws, sendLock, new { op = 6, d = new { token = "QQBot " + token, session_id = session, seq = sequence } }, ct);
                            heartbeat = HeartbeatAsync(ws, sendLock, () => sequence,
                                () => Volatile.Read(ref ackPending) != 0,
                                value => Interlocked.Exchange(ref ackPending, value ? 1 : 0), interval, loopCts.Token);
                            break;
                        case 11:
                            Interlocked.Exchange(ref ackPending, 0);
                            break;
                        case 9:
                            session = null;
                            sequence = null;
                            ws.Abort();
                            break;
                        case 7:
                            ws.Abort();
                            break;
                        case 0:
                            var evt = Probe.Text(root, "t");
                            var data = Probe.Field(root, "d");
                            if (evt == "READY" && data is { } ready)
                                session = Probe.Text(ready, "session_id") ?? throw new ProbeException("QQ READY 缺 session_id。");
                            if (evt == "C2C_MESSAGE_CREATE" && data is { } message)
                            {
                                var peer = Probe.Field(message, "author") is { } author ? Probe.Text(author, "user_openid") : null;
                                var messageId = Probe.Text(message, "id");
                                var scene = Probe.Field(message, "message_scene");
                                var ext = scene is { } sceneValue ? Probe.Field(sceneValue, "ext") : null;
                                var idx = ext is { } extValue ? GetMessageIndex(extValue) : null;
                                if (string.IsNullOrWhiteSpace(peer) || string.IsNullOrWhiteSpace(messageId) || idx is null)
                                    throw new ProbeException("QQ 测试事件缺少 user_openid、id 或 msg_idx；不允许不完整事件触发回复。");
                                if (Probe.Int(message, "message_type") == 0 && Probe.Text(message, "content")?.Trim() == challenge)
                                {
                                    if (expectedPeer is null)
                                    {
                                        Console.WriteLine($"收到挑战码。测试 peer OpenID：{peer}（仅本地终端显示；设置 P0_QQ_PEER_OPENID 后重跑，勿分享）。");
                                        return;
                                    }
                                    if (peer == expectedPeer)
                                    {
                                        if (captureOnly)
                                        {
                                            Console.WriteLine($"QQ 测试入站 ID：{messageId}；本机接收 UTC：{DateTimeOffset.UtcNow:O}。仅本地使用此 ID 测试回复窗口，不要转发。");
                                            return;
                                        }
                                        using var reply = Probe.Post(Probe.QqUri("/v2/users/" + Uri.EscapeDataString(peer) + "/messages"),
                                            new { msg_type = 0, content = "Quantum P0 QQ 测试回执 " + challenge, msg_id = messageId, msg_seq = 1 });
                                        var restToken = DateTimeOffset.UtcNow < refreshAt
                                            ? token : (await RequestTokenAsync(appId, appSecret, ct)).Token;
                                        reply.Headers.Authorization = new AuthenticationHeaderValue("QQBot", restToken);
                                        var accepted = await Probe.SendAsync(reply, ct);
                                        if (Probe.Int(accepted, "code") is { } code && code != 0)
                                            throw new ProbeException($"QQ 回发业务码 {code}；未判定成功。");
                                        Console.WriteLine("QQ 发送接口已受理；请由测试账号持有人确认消息实际到达。");
                                        return;
                                    }
                                }
                            }
                            if (Probe.Int(root, "s") is { } seq) sequence = seq; // 内存态，不代表生产系统的持久化游标。
                            break;
                    }
                }
            }
            catch (WebSocketException) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine("QQ Gateway 中断，退避后重新连接（本探针只保留内存态 Session）。");
            }
            finally
            {
                loopCts.Cancel();
                ws.Abort();
                if (heartbeat is not null)
                {
                    try { await heartbeat; }
                    catch (OperationCanceledException) { }
                    catch (WebSocketException) when (!ct.IsCancellationRequested) { }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    // 官方 C2C 的 message_scene.ext 为 ["msg_idx=REFIDX_...", "auth_token=..."]；只读索引，绝不输出或持久化其它扩展项。
    internal static string? GetMessageIndex(JsonElement ext)
    {
        if (ext.ValueKind != JsonValueKind.Array) return null;
        string? index = null;
        foreach (var field in ext.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } raw ||
                !raw.StartsWith("msg_idx=", StringComparison.Ordinal)) continue;
            var value = raw["msg_idx=".Length..];
            if (index is not null || value.Length is < 1 or > 128) return null;
            index = value;
        }
        return index;
    }

    // 仅供持有人许可后的 P0 主动消息实测；绝不作为回复超时后的静默兜底。
    internal static async Task RunActiveAsync(string challenge, CancellationToken ct)
    {
        var appId = Probe.Required("P0_QQ_APP_ID");
        var appSecret = Probe.Required("P0_QQ_APP_SECRET");
        var peer = Probe.Required("P0_QQ_PEER_OPENID");
        var (token, _) = await RequestTokenAsync(appId, appSecret, ct);
        using var reply = Probe.Post(Probe.QqUri("/v2/users/" + Uri.EscapeDataString(peer) + "/messages"),
            new { msg_type = 0, content = "Quantum P0 QQ 主动发送实验 " + challenge });
        reply.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        var result = await Probe.SendAsync(reply, ct);
        if (Probe.Int(result, "code") is { } code && code != 0)
            throw new ProbeException($"QQ 主动实验业务码 {code}；未判定成功。");
        Console.WriteLine("QQ 主动实验接口已受理（不等于手机收到）；请手机核验并记录权限/额度/时间窗口。");
    }

    // 手工验证 5/60 分钟文案冲突：由操作者先通过 qq-capture 获取仅测试账号的 msg_id，按实际时间窗口手工运行。
    internal static async Task RunPassiveAsync(string challenge, CancellationToken ct)
    {
        var appId = Probe.Required("P0_QQ_APP_ID");
        var appSecret = Probe.Required("P0_QQ_APP_SECRET");
        var peer = Probe.Required("P0_QQ_PEER_OPENID");
        var messageId = Probe.Required("P0_QQ_MSG_ID");
        if (messageId.Length is < 8 or > 256 || messageId.Any(char.IsWhiteSpace))
            throw new ProbeException("测试消息 ID 格式异常；请使用 qq-capture 获取原始 ID。");
        var (token, _) = await RequestTokenAsync(appId, appSecret, ct);
        using var request = Probe.Post(Probe.QqUri("/v2/users/" + Uri.EscapeDataString(peer) + "/messages"),
            new { msg_type = 0, content = "Quantum P0 QQ 被动回复窗口实验 " + challenge, msg_id = messageId, msg_seq = 1 });
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        var result = await Probe.SendAsync(request, ct);
        if (Probe.Int(result, "code") is { } code && code != 0)
            throw new ProbeException($"QQ 被动窗口实验业务码 {code}；未判定成功。");
        Console.WriteLine("QQ 被动窗口实验接口已受理（不等于手机收到）；请核对手机和实际消息发出时间。");
    }

    private static async Task<(string Token, DateTimeOffset RefreshAt)> RequestTokenAsync(
        string appId, string appSecret, CancellationToken ct)
    {
        using var request = Probe.Post(Probe.QqUri("/app/getAppAccessToken"), new { appId, clientSecret = appSecret });
        var response = await Probe.SendAsync(request, ct);
        return ParseToken(response, DateTimeOffset.UtcNow);
    }

    internal static (string Token, DateTimeOffset RefreshAt) ParseToken(JsonElement response, DateTimeOffset now)
    {
        var token = Probe.Text(response, "access_token");
        var expiry = Probe.Int(response, "expires_in");
        if (string.IsNullOrWhiteSpace(token) || expiry is null or <= 0)
            throw new ProbeException("QQ 鉴权未返回有效 token/有效期，拒绝继续。");
        // 官方有效期以服务器值为准，并在 60 秒前刷新；短有效期用半程刷新。
        var refreshSeconds = expiry.Value <= 120 ? Math.Max(1, expiry.Value / 2) : expiry.Value - 60;
        return (token, now.AddSeconds(refreshSeconds));
    }

    private static async Task<byte[]?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var next = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (next.MessageType == WebSocketMessageType.Close) return null;
            if (next.MessageType != WebSocketMessageType.Text || data.Length + next.Count > 65536)
                throw new ProbeException("QQ Gateway 非法帧或超过 64KB，拒绝处理。");
            data.Write(buffer, 0, next.Count);
            if (next.EndOfMessage) return data.ToArray();
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, SemaphoreSlim gate, object packet, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(packet, Probe.Json);
        await gate.WaitAsync(ct);
        try { await ws.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, ct); }
        finally { gate.Release(); }
    }

    private static async Task HeartbeatAsync(ClientWebSocket ws, SemaphoreSlim gate, Func<int?> seq,
        Func<bool> pending, Action<bool> setPending, int milliseconds, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(milliseconds));
        while (await timer.WaitForNextTickAsync(ct) && ws.State == WebSocketState.Open)
        {
            if (pending()) { ws.Abort(); return; }
            setPending(true);
            await SendAsync(ws, gate, new { op = 1, d = seq() }, ct);
        }
    }
}
