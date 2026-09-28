using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quantum.Utils;

namespace Quantum.Application.Channels;

/// <summary>飞书开放平台 REST 客户端：tenant_access_token 缓存 + 卡片发送 + 事件体解析。凭据不进日志。</summary>
public sealed class FeishuChannelClient
{
    private readonly ChannelNetwork _network;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _fingerprint;
    private string _token;
    private DateTime _refreshAtUtc;

    public FeishuChannelClient(ChannelNetwork network) => _network = network;

    /// <summary>
    /// tenant_access_token 有效期最长 2 小时；剩余 ≥30 分钟时服务端会返回原 token，
    /// 故按剩余时间提前一点刷新即可，不必频繁调用。
    /// </summary>
    public async Task<string> TokenAsync(string appId, string appSecret, CancellationToken ct)
    {
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(appId + ":" + appSecret)));
        if (_fingerprint == fingerprint && _token is not null && _refreshAtUtc > DateTime.UtcNow) return _token;
        await _gate.WaitAsync(ct);
        try
        {
            if (_fingerprint == fingerprint && _token is not null && _refreshAtUtc > DateTime.UtcNow) return _token;
            var result = await _network.PostAsync(ChannelNetwork.FeishuUri("/open-apis/auth/v3/tenant_access_token/internal"),
                new { app_id = appId, app_secret = appSecret }, ct: ct);
            if (Code(result) is { } code && code != 0) throw new ChannelProtocolException("FEISHU_AUTH_" + code);
            var token = Text(result, "tenant_access_token");
            var expire = Int(result, "expire");
            if (string.IsNullOrWhiteSpace(token) || expire is null or <= 0)
                throw new ChannelProtocolException("FEISHU_AUTH_INVALID");
            _fingerprint = fingerprint;
            _token = token;
            var ttl = expire.Value;
            _refreshAtUtc = DateTime.UtcNow.AddSeconds(ttl <= 300 ? Math.Max(1, ttl / 2) : ttl - 300);
            return token;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 取长连接接入地址（wss URL 与保活/重连参数）。
    /// 平台对字段名大小写敏感：只认 <c>AppID</c>，发 <c>appId</c>/<c>AppId</c> 一律 HTTP 400 + code 9499，
    /// 是历史上「扫码成功但永远连不上」的直接原因，故请求体与响应解析都收敛到可测的两个方法里。
    /// </summary>
    public async Task<(string Url, int PingSeconds, int ReconnectSeconds)> WssEndpointAsync(
        string appId, string appSecret, CancellationToken ct)
    {
        var result = await _network.PostAsync(ChannelNetwork.FeishuUri("/callback/ws/endpoint"),
            BuildEndpointRequest(appId, appSecret), ct: ct, readErrorBody: true);
        return ParseEndpointResponse(result);
    }

    internal static JsonObject BuildEndpointRequest(string appId, string appSecret) => new()
    {
        ["AppID"] = appId,
        ["AppSecret"] = appSecret
    };

    /// <summary>成功响应把地址嵌在 <c>data</c> 里：<c>{"code":0,"data":{"URL":"wss://…","ClientConfig":{…}}}</c>。</summary>
    internal static (string Url, int PingSeconds, int ReconnectSeconds) ParseEndpointResponse(JsonElement result)
    {
        if (Code(result) is { } code && code != 0) throw new ChannelProtocolException("FEISHU_WS_ENDPOINT_" + code);
        var data = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out var d) ? d : default;
        if (data.ValueKind != JsonValueKind.Object) throw new ChannelProtocolException("FEISHU_WS_ENDPOINT_MISSING");
        var url = Text(data, "URL");
        if (string.IsNullOrWhiteSpace(url)) throw new ChannelProtocolException("FEISHU_WS_ENDPOINT_MISSING");
        var config = data.TryGetProperty("ClientConfig", out var cfg) ? cfg : default;
        var ping = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("PingInterval", out var p) ? p.GetInt32() : 120;
        var reconnect = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("ReconnectInterval", out var r) ? r.GetInt32() : 120;
        return (url, Math.Clamp(ping <= 0 ? 120 : ping, 15, 600), Math.Clamp(reconnect <= 0 ? 120 : reconnect, 1, 600));
    }

    /// <summary>
    /// 发送卡片。uuid 取 outbox.ClientId——同 uuid 一小时内至多成功一条，正好当幂等键用。
    /// 返回平台消息 Id（om_xxx），供后续按 message_id 更新卡片。
    /// </summary>
    public async Task<string> SendCardAsync(string token, string openId, JsonObject card, string uuid, CancellationToken ct)
    {
        var body = new
        {
            receive_id = openId,
            msg_type = "interactive",
            content = FeishuRichText.SerializeContent(card),
            uuid
        };
        var result = await _network.PostAsync(
            ChannelNetwork.FeishuUri("/open-apis/im/v1/messages?receive_id_type=open_id"), body, token: token,
            ct: ct, authScheme: ChannelNetwork.BearerAuthScheme);
        if (Code(result) is { } code && code != 0) throw new ChannelProtocolException("FEISHU_SEND_" + code);
        var messageId = ExtractMessageId(result);
        if (string.IsNullOrWhiteSpace(messageId)) throw new ChannelProtocolException("FEISHU_SEND_NO_MESSAGE_ID");
        return messageId;
    }

    /// <summary>纯文本降级通道（卡片构造失败时用）。</summary>
    public async Task<string> SendTextAsync(string token, string openId, string text, string uuid, CancellationToken ct)
    {
        var body = new
        {
            receive_id = openId,
            msg_type = "text",
            content = JsonSerializer.Serialize(new { text = text ?? "" }),
            uuid
        };
        var result = await _network.PostAsync(
            ChannelNetwork.FeishuUri("/open-apis/im/v1/messages?receive_id_type=open_id"), body, token: token,
            ct: ct, authScheme: ChannelNetwork.BearerAuthScheme);
        if (Code(result) is { } code && code != 0) throw new ChannelProtocolException("FEISHU_SEND_" + code);
        return ExtractMessageId(result);
    }

    /// <summary>
    /// 飞书把 <c>message_id</c> 嵌在响应的 <c>data</c> 里（与长连接地址同一形态）。顶层读不到时，
    /// 已投递成功的卡片会被判成失败并再降级补发一条文本——用户收到两条重复消息。
    /// </summary>
    internal static string ExtractMessageId(JsonElement result)
    {
        var direct = Text(result, "message_id");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out var data) &&
               data.ValueKind == JsonValueKind.Object ? Text(data, "message_id") : null;
    }

    /// <summary>
    /// 解析 im.message.receive_v1 事件为通用入站对象。
    /// 只接受**私聊**（chat_type=p2p）：本通道是「每平台唯一私聊绑定」，群聊会打破该假设且未被授权。
    /// </summary>
    public static ChannelIncoming ParseIncoming(JsonElement eventElement, string appId, string expectedBotOpenId = null)
    {
        var message = eventElement.TryGetProperty("message", out var m) ? m : default;
        var sender = eventElement.TryGetProperty("sender", out var s) ? s : default;
        var senderId = sender.ValueKind == JsonValueKind.Object &&
                       sender.TryGetProperty("sender_id", out var sid) &&
                       sid.TryGetProperty("open_id", out var oid) ? oid.GetString() : null;
        var chatType = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("chat_type", out var ct)
            ? ct.GetString() : null;
        var messageId = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("message_id", out var mid)
            ? mid.GetString() : null;
        var text = ExtractText(message);
        var isPrivate = string.Equals(chatType, "p2p", StringComparison.Ordinal);
        var botMatches = expectedBotOpenId is null ||
                         (senderId is not null && senderId.StartsWith("ou_", StringComparison.Ordinal) &&
                          !string.Equals(senderId, expectedBotOpenId, StringComparison.Ordinal));

        return new ChannelIncoming(
            EventType: "im.message.receive_v1",
            MessageId: messageId,
            MessageIndex: messageId,
            PeerId: isPrivate ? senderId : null,
            Text: text,
            ContextToken: null,
            IsText: isPrivate && botMatches && text is { Length: > 0 },
            Fingerprint: messageId);
    }

    /// <summary>卡片消息内容是 JSON 字符串（{\"text\":\"…\"}），需二次解析。</summary>
    private static string ExtractText(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("message_type", out var type)) return null;
        if (type.GetString() != "text") return null;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;
        try
        {
            using var inner = JsonDocument.Parse(content.GetString());
            return inner.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public static string Text(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var field) &&
           field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    public static int? Int(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var field) &&
           field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var n) ? n : null;

    public static int? Code(JsonElement obj) => Int(obj, "code");
}
