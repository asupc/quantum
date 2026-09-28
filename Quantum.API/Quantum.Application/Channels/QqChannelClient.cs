using System.Globalization;
using System.Text.Json;

namespace Quantum.Application.Channels;

/// <summary>QQ 官方 AccessToken/REST 共用客户端；凭据不进入日志及前端。</summary>
public sealed class QqChannelClient
{
    private readonly ChannelNetwork _network;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string _fingerprint;
    private string _token;
    private DateTime _refreshAtUtc;

    public QqChannelClient(ChannelNetwork network) => _network = network;

    public async Task<string> TokenAsync(string appId, string appSecret, string apiBase, CancellationToken ct)
    {
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(appId + ":" + appSecret)));
        if (_fingerprint == fingerprint && _refreshAtUtc > DateTime.UtcNow && _token is not null)
            return _token;
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_fingerprint == fingerprint && _refreshAtUtc > DateTime.UtcNow && _token is not null)
                return _token;
            // 凭证接口固定在 bots.qq.com，与 openapi（沙箱/正式）域名无关。
            var result = await _network.PostAsync(ChannelNetwork.QqTokenUri("/app/getAppAccessToken"),
                new { appId, clientSecret = appSecret }, ct: ct);
            var (access, ttl) = ParseTokenResponse(result);
            _fingerprint = fingerprint;
            _token = access;
            _refreshAtUtc = DateTime.UtcNow.AddSeconds(ttl <= 120 ? Math.Max(1, ttl / 2) : ttl - 60);
            return access;
        }
        finally { _tokenGate.Release(); }
    }

    /// <summary>QQ 官方令牌接口的 expires_in 是十进制字符串；兼容历史数字响应，拒绝无效时长。</summary>
    internal static (string Access, int Ttl) ParseTokenResponse(JsonElement result)
    {
        if (Int(result, "code") is { } code && code != 0)
            throw new ChannelProtocolException("QQ_AUTH_INVALID");
        var access = Text(result, "access_token");
        if (string.IsNullOrWhiteSpace(access))
            throw new ChannelProtocolException("QQ_AUTH_INVALID");
        var raw = Field(result, "expires_in");
        int ttl;
        if (raw is { ValueKind: JsonValueKind.String })
        {
            var text = raw.Value.GetString();
            if (text is null || text.Length is < 1 or > 10 ||
                !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ttl) || ttl <= 0)
                throw new ChannelProtocolException("QQ_AUTH_RESPONSE_INVALID");
        }
        else if (raw is { ValueKind: JsonValueKind.Number } && raw.Value.TryGetInt32(out ttl) && ttl > 0)
        {
            // 兼容旧版数值形态，不影响官方字符串形态。
        }
        else throw new ChannelProtocolException("QQ_AUTH_RESPONSE_INVALID");
        return (access, ttl);
    }

    public async Task<(Uri Gateway, int ResetAfter)> GatewayAsync(string access, string apiBase, CancellationToken ct)
    {
        var json = await _network.GetAsync(ChannelNetwork.QqUri("/gateway", apiBase), token: access, ct: ct);
        var url = Text(json, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.Port != 443 ||
            !ChannelNetwork.IsQqHost(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ChannelProtocolException("QQ_GATEWAY_DENIED");
        var limit = Field(json, "session_start_limit");
        var reset = limit is { } l && Int(l, "remaining") is 0
            ? Math.Clamp(Int(l, "reset_after") ?? 60000, 5000, 300000) : 0;
        return (uri, reset);
    }

    public async Task<string> SendReplyAsync(string access, string peerId, string content, string messageId, int seq,
        string apiBase, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId) || seq is < 1 or > 4)
            throw new ChannelProtocolException("QQ_REPLY_ROUTE_INVALID");
        var result = await _network.PostAsync(
            ChannelNetwork.QqUri("/v2/users/" + Uri.EscapeDataString(peerId) + "/messages", apiBase),
            new { msg_type = 0, content, msg_id = messageId, msg_seq = seq }, token: access, ct: ct);
        if (Int(result, "code") is { } code && code != 0)
            throw new ChannelProtocolException("QQ_CODE_" + code);
        return Text(result, "id");
    }

    public static string Text(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
    public static int? Int(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var n)
            ? n : null;
    public static long? Long(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var n)
            ? n : null;
    public static JsonElement? Field(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var field) ? field : null;

    public static string GetMessageIndex(JsonElement ext)
    {
        if (ext.ValueKind != JsonValueKind.Array) return null;
        string value = null;
        foreach (var element in ext.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } text ||
                !text.StartsWith("msg_idx=", StringComparison.Ordinal)) continue;
            var found = text["msg_idx=".Length..];
            if (value is not null || found.Length is < 1 or > 160) return null;
            value = found;
        }
        return value;
    }
}
