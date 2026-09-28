using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

internal static class WeixinProbe
{
    private static readonly Uri Root = Probe.WeixinUri("");

    internal static async Task RunAsync(string challenge, CancellationToken ct)
    {
        var clientVersion = Probe.Required("P0_WEIXIN_CLIENT_VERSION");
        var channelVersion = Probe.Required("P0_WEIXIN_CHANNEL_VERSION");
        if (!uint.TryParse(clientVersion, out var encodedVersion) || encodedVersion == 0 ||
            channelVersion.Length is < 1 or > 32)
            throw new ProbeException("微信版本配置不合法；请核对官方插件当前 wire 版本，不要冒用 OpenClaw 产品标识。");
        using var store = WeixinStateStorage.OpenFromEnvironment();
        var state = store?.Load();
        if (state is null)
        {
            state = await LoginAsync(clientVersion, ct);
            store?.Save(state); // 登录态先持久化，重启后可继续从已提交的游标轮询。
        }
        else
            Console.WriteLine("已从加密状态恢复测试登录；不重新扫码。过期令牌需人工清理本机 P0 状态后重新扫码。");
        var apiRoot = Probe.WeixinUri("", new Uri(state.ApiBase, UriKind.Absolute));
        var botToken = state.BotToken;
        var botId = state.BotId;
        var peer = state.PeerId;
        var cursor = state.Cursor;
        while (!ct.IsCancellationRequested)
        {
            using var request = JsonPost(Probe.WeixinUri("ilink/bot/getupdates", apiRoot),
                new { get_updates_buf = cursor, base_info = BaseInfo(channelVersion) }, clientVersion, botToken);
            var response = await Probe.SendAsync(request, ct);
            Probe.CheckBusiness(response);
            if (Probe.Int(response, "ret") != 0)
                throw new ProbeException("微信轮询未返回明确 ret=0，不推进游标。");
            var next = Probe.Text(response, "get_updates_buf");
            var messages = Probe.Field(response, "msgs");
            if (messages is not { ValueKind: JsonValueKind.Array })
                throw new ProbeException("微信轮询未返回消息数组，不推进游标。");
            if (messages.Value.GetArrayLength() > 0 && string.IsNullOrEmpty(next))
                throw new ProbeException("微信收到消息但未给新游标，停止以免重复回发。");
            var sent = false;
            foreach (var message in messages.Value.EnumerateArray())
            {
                if (sent || Probe.Int(message, "message_type") != 1 || Probe.Text(message, "to_user_id") != botId ||
                    Probe.Text(message, "from_user_id") != peer || !HasChallenge(message, challenge))
                    continue;
                if (Probe.Field(message, "message_id") is not { ValueKind: JsonValueKind.Number } ||
                    Probe.Text(message, "context_token") is not { Length: > 0 } context)
                    throw new ProbeException("匹配消息缺少数字 message_id 或 context_token，不进行无 token 回发。");
                var id = Probe.Field(message, "message_id")!.Value.GetRawText();
                if (state.AttemptedMessageIds.Contains(id, StringComparer.Ordinal))
                    continue; // 已经尝试过的消息可能在旧游标重放，未知回执也不自动补发。
                state = state with { AttemptedMessageIds = [.. state.AttemptedMessageIds, id] };
                store?.Save(state); // 必须先记录尝试，再执行不可撤销的对外发送。
                var msg = new
                {
                    from_user_id = "", to_user_id = peer, client_id = Guid.NewGuid().ToString("N"),
                    message_type = 2, message_state = 2, context_token = context,
                    item_list = new[] { new { type = 1, text_item = new { text = "Quantum P0 微信测试回执 " + challenge } } }
                };
                using var send = JsonPost(Probe.WeixinUri("ilink/bot/sendmessage", apiRoot),
                    new { msg, base_info = BaseInfo(channelVersion) }, clientVersion, botToken);
                var result = await Probe.SendAsync(send, ct);
                Probe.CheckBusiness(result);
                if (Probe.Int(result, "ret") != 0)
                    throw new ProbeException("微信发送未返回明确 ret=0，不判定成功。");
                sent = true;
            }
            if (!string.IsNullOrEmpty(next))
            {
                state = state with { Cursor = next };
                store?.Save(state); // P0 单文件检查点；生产必须与 Inbox 同一 DB 事务持久化。
                cursor = next;
            }
            if (sent)
            {
                Console.WriteLine(store is null
                    ? "微信发送接口已受理；请在手机核验实际收到。本次未启用状态持久化，不能验证重启。"
                    : "微信发送接口已受理、测试游标已加密保存；请在手机核验实际收到，重启后继续测试。");
                return;
            }
        }
    }

    // 与被动回复完全分离的 P0 实验；不携带 context_token，不暗中重试或回退。
    internal static async Task RunActiveAsync(string challenge, CancellationToken ct)
    {
        var clientVersion = Probe.Required("P0_WEIXIN_CLIENT_VERSION");
        var channelVersion = Probe.Required("P0_WEIXIN_CHANNEL_VERSION");
        if (!uint.TryParse(clientVersion, out var version) || version == 0 || channelVersion.Length is < 1 or > 32)
            throw new ProbeException("微信版本配置不合法；已停止主动实验。");
        using var store = WeixinStateStorage.OpenFromEnvironment() ??
            throw new ProbeException("主动实验必须先通过加密 P0 状态恢复测试账号，不能临时猜测目标。");
        var state = store.Load() ?? throw new ProbeException("没有已扫码并加密保存的测试微信账号，已停止主动实验。");
        var apiRoot = Probe.WeixinUri("", new Uri(state.ApiBase, UriKind.Absolute));
        var msg = new
        {
            from_user_id = "", to_user_id = state.PeerId, client_id = Guid.NewGuid().ToString("N"),
            message_type = 2, message_state = 2,
            item_list = new[] { new { type = 1, text_item = new { text = "Quantum P0 微信主动发送实验 " + challenge } } }
        };
        using var request = JsonPost(Probe.WeixinUri("ilink/bot/sendmessage", apiRoot),
            new { msg, base_info = BaseInfo(channelVersion) }, clientVersion, state.BotToken);
        var result = await Probe.SendAsync(request, ct);
        Probe.CheckBusiness(result);
        if (Probe.Int(result, "ret") != 0)
            throw new ProbeException("微信主动实验未返回明确 ret=0，不判定成功。");
        Console.WriteLine("微信主动实验接口已受理（不等于手机收到）；请手机核验并记录时间窗口/错误码。");
    }

    private static async Task<WeixinProbeState> LoginAsync(string clientVersion, CancellationToken ct)
    {
        using var qrRequest = JsonPost(Probe.WeixinUri("ilink/bot/get_bot_qrcode?bot_type=3"),
            new { local_token_list = Array.Empty<string>() }, clientVersion);
        var qr = await Probe.SendAsync(qrRequest, ct);
        var qrValue = Probe.Text(qr, "qrcode") ?? throw new ProbeException("微信未返回二维码标识。");
        var qrDisplay = Probe.Text(qr, "qrcode_img_content") ?? throw new ProbeException("微信未返回二维码内容。");
        Console.WriteLine("仅在本机终端显示二维码内容；请在信任的设备上扫码，不要复制到网站、聊天或日志：");
        Console.WriteLine(qrDisplay);
        Uri pollBase = Root;
        string? verifyCode = null;
        using var qrTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        qrTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        JsonElement confirmation;
        while (true)
        {
            qrTimeout.Token.ThrowIfCancellationRequested();
            var statusPath = "ilink/bot/get_qrcode_status?qrcode=" + Uri.EscapeDataString(qrValue);
            if (verifyCode is not null) statusPath += "&verify_code=" + Uri.EscapeDataString(verifyCode);
            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, Probe.WeixinUri(statusPath, pollBase));
            AddAppHeaders(statusRequest, clientVersion);
            var status = await Probe.SendAsync(statusRequest, qrTimeout.Token);
            verifyCode = null;
            switch (Probe.Text(status, "status"))
            {
                case "wait" or "scaned":
                    await Task.Delay(TimeSpan.FromSeconds(2), qrTimeout.Token);
                    continue;
                case "scaned_but_redirect":
                    var host = Probe.Text(status, "redirect_host") ?? throw new ProbeException("微信扫码重定向缺少主机。");
                    pollBase = Probe.WeixinUri("", new Uri(host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? host : "https://" + host, UriKind.Absolute));
                    continue;
                case "confirmed":
                    confirmation = status;
                    break;
                case "need_verifycode":
                    Console.Write("微信要求验证码（本地输入，不回显）：");
                    verifyCode = ReadSecret();
                    continue;
                case "expired" or "verify_code_blocked" or "binded_redirect":
                    throw new ProbeException($"微信扫码状态 {Probe.Text(status, "status")}；本探针不自动处理验证码或已有绑定，请人工核验。");
                default:
                    throw new ProbeException("微信返回未知扫码状态；未使用返回的凭据。");
            }
            break;
        }
        var botToken = Probe.Text(confirmation, "bot_token") ?? throw new ProbeException("微信扫码无令牌。");
        var botId = Probe.Text(confirmation, "ilink_bot_id") ?? throw new ProbeException("微信扫码无机器人 ID。");
        var peer = Probe.Text(confirmation, "ilink_user_id") ?? throw new ProbeException("扫码未提供可核验的用户 ID；禁止首条消息自动绑定。");
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(botId) || string.IsNullOrWhiteSpace(peer))
            throw new ProbeException("扫码确认未提供完整 bot_token/bot_id/user_id，拒绝保存。");
        var baseString = Probe.Text(confirmation, "baseurl");
        var apiRoot = baseString is null ? Root : Probe.WeixinUri("", new Uri(baseString, UriKind.Absolute));
        Console.WriteLine("扫码成功；等待唯一扫码用户发送本次测试口令……");
        return new WeixinProbeState(botId, peer, botToken, apiRoot.AbsoluteUri, "", []);
    }

    private static string ReadSecret()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0) buffer.Length--;
            else if (!char.IsControl(key.KeyChar) && buffer.Length < 32) buffer.Append(key.KeyChar);
        }
        if (buffer.Length == 0) throw new ProbeException("验证码为空，已中止。");
        return buffer.ToString();
    }

    internal static bool HasChallenge(JsonElement message, string expected)
    {
        var items = Probe.Field(message, "item_list");
        if (items is not { ValueKind: JsonValueKind.Array } || items.Value.GetArrayLength() != 1) return false;
        foreach (var item in items.Value.EnumerateArray())
        {
            if (Probe.Int(item, "type") == 1 && Probe.Field(item, "text_item") is { } text &&
                Probe.Text(text, "text")?.Trim() == expected) return true;
        }
        return false;
    }

    private static object BaseInfo(string version) => new { channel_version = version, bot_agent = "QuantumPhase0/1" };

    private static HttpRequestMessage JsonPost(Uri uri, object body, string clientVersion, string? token = null)
    {
        var request = Probe.Post(uri, body);
        AddAppHeaders(request, clientVersion);
        request.Headers.TryAddWithoutValidation("AuthorizationType", "ilink_bot_token");
        request.Headers.TryAddWithoutValidation("X-WECHAT-UIN", Convert.ToBase64String(Encoding.ASCII.GetBytes(
            BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)).ToString())));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void AddAppHeaders(HttpRequestMessage request, string clientVersion)
    {
        request.Headers.TryAddWithoutValidation("iLink-App-Id", "bot");
        request.Headers.TryAddWithoutValidation("iLink-App-ClientVersion", clientVersion);
    }
}
