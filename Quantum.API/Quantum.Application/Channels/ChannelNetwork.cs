using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quantum.Utils;

namespace Quantum.Application.Channels;

/// <summary>QQ/微信专用网络边界：固定域名、禁止代理/重定向、公网 DNS 全量校验后按 IP 直连。</summary>
public sealed class ChannelNetwork : IDisposable
{
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;
    public HttpMessageInvoker WebSocketInvoker { get; }

    public ChannelNetwork()
    {
        _handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            MaxConnectionsPerServer = 4, PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync
        };
        _http = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(65) };
        WebSocketInvoker = new HttpMessageInvoker(_handler, disposeHandler: false);
    }

    /// <summary>QQ 开放平台的调用凭证方案（<c>Authorization: QQBot &lt;token&gt;</c>）；飞书走标准 Bearer，见 <c>BearerAuthScheme</c>。</summary>
    public const string QqAuthScheme = "QQBot";
    public const string BearerAuthScheme = "Bearer";

    public async Task<JsonElement> PostAsync(Uri uri, object body, string token = null, string version = null,
        CancellationToken ct = default, bool readErrorBody = false, string authScheme = QqAuthScheme)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = BuildJsonContent(body) };
        AddHeaders(request, token, version, authScheme);
        return await SendAsync(request, ct, readErrorBody);
    }

    /// <summary>
    /// 请求体必须自带 Content-Length：微信 iLink 网关对分块传输（<c>JsonContent</c> 不预计算长度）
    /// 直接回 <c>HTTP 412</c> 且响应体为空，长轮询因此永远取不到消息；QQ/飞书一并受益。
    /// </summary>
    internal static HttpContent BuildJsonContent(object body)
    {
        var bytes = body is JsonNode node
            ? Encoding.UTF8.GetBytes(node.ToJsonString())
            : JsonSerializer.SerializeToUtf8Bytes(body);
        return new ByteArrayContent(bytes)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } }
        };
    }

    /// <summary>表单编码 POST：飞书设备码注册端点（accounts.feishu.cn）只收 form-urlencoded。</summary>
    public async Task<JsonElement> PostFormAsync(Uri uri, IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken ct = default, bool readErrorBody = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        return await SendAsync(request, ct, readErrorBody);
    }

    public async Task<JsonElement> GetAsync(Uri uri, string token = null, string version = null,
        CancellationToken ct = default, string authScheme = QqAuthScheme)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddHeaders(request, token, version, authScheme);
        return await SendAsync(request, ct);
    }

    /// <summary>
    /// 鉴权头按平台分方案：iLink（带 version）用 Bearer + 两件套；QQ 开放平台用 <c>QQBot &lt;token&gt;</c>；
    /// 飞书虽同属「无 version」形态，但只认标准 <c>Bearer</c>——早期共用默认值让飞书每条发送都吃 HTTP 400。
    /// </summary>
    internal static void AddHeaders(HttpRequestMessage request, string token, string version, string authScheme = QqAuthScheme)
    {
        if (version != null)
        {
            // iLink 协议：官方样例只发 iLink-App-ClientVersion（扫码状态接口固定 "1"），
            // 鉴权两件套 AuthorizationType/Authorization 仅在拿到 bot_token 之后才带。
            request.Headers.TryAddWithoutValidation("iLink-App-ClientVersion", version);
            if (token != null)
            {
                request.Headers.TryAddWithoutValidation("AuthorizationType", "ilink_bot_token");
                request.Headers.TryAddWithoutValidation("X-WECHAT-UIN", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(
                    System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, int.MaxValue).ToString())));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        else if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue(authScheme, token);
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct, bool readErrorBody = false)
    {
        if (!AllowedUri(request.RequestUri)) throw new BusinessException("通道目标地址不在平台 HTTPS 白名单内");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            // 飞书设备码端点在 authorization_pending 等等待态返回 4xx 但 body 仍是 JSON（error=...），
            // readErrorBody 即为该协议形态准备：错误状态也解析 body，body 不是 JSON 再按状态码上报。
            if (!response.IsSuccessStatusCode && !readErrorBody) throw new ChannelProtocolException("HTTP_" + status);
            try
            {
                await response.Content.LoadIntoBufferAsync(512 * 1024, ct);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return document.RootElement.Clone();
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                throw new ChannelProtocolException("HTTP_" + status);
            }
        }
        catch (HttpRequestException) { throw new ChannelProtocolException("NETWORK_ERROR"); }
        catch (JsonException) { throw new ChannelProtocolException("INVALID_JSON"); }
    }

    /// <summary>
    /// QQ openapi 接入点。正式环境 https://api.bot.qq.com（2026-08-10 起由 api.sgroup.qq.com 统一而来）；
    /// 沙箱环境用不同域名，且**沙箱不受平台的 IP 白名单限制**，无公网 IP 时只能走沙箱，故允许按账户配置。
    /// </summary>
    public static Uri QqUri(string path, string root = QqProdApiBase)
    {
        // 存量账户的凭据里没有 ApiBase 字段（本次新增），故空值一律回落正式环境而不是直接拒绝。
        if (string.IsNullOrWhiteSpace(root)) root = QqProdApiBase;
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !IsQqHost(uri.Host))
            throw new BusinessException("QQ 接口地址不受信任（须为 https 的 qq.com 官方域名）");
        return new Uri(uri, path.TrimStart('/'));
    }

    /// <summary>取调用凭证固定走 bots.qq.com——它不在 openapi 域名下，早期实现误拼到 openapi 导致取 token 必失败。</summary>
    public static Uri QqTokenUri(string path) => new("https://bots.qq.com" + path);

    public const string QqProdApiBase = "https://api.bot.qq.com/";

    /// <summary>
    /// 官方 wiki「沙箱配置」给出的沙箱接入点。沙箱域名是否随 2026-08-10 域名统一迁移尚未实测，
    /// 故内置为默认值的同时保留按账户覆盖（Environment=Sandbox + 显式 ApiBase）。
    /// </summary>
    public const string QqSandboxApiBase = "https://sandbox.api.sgroup.qq.com/";

    /// <summary>
    /// 按环境解析 QQ openapi 接入点：显式地址优先（仍过 QqUri 信任校验），
    /// 沙箱留空用官方默认沙箱地址、正式留空用正式地址——普通用户不必再去开放平台「沙箱配置」找地址。
    /// </summary>
    public static string ResolveQqApiBase(string environment, string apiBase)
    {
        var sandbox = string.Equals(environment?.Trim(), "Sandbox", StringComparison.OrdinalIgnoreCase);
        var root = string.IsNullOrWhiteSpace(apiBase)
            ? (sandbox ? QqSandboxApiBase : QqProdApiBase)
            : apiBase.Trim();
        return QqUri("", root).AbsoluteUri;
    }

    public static Uri WeixinUri(string path, string root = "https://ilinkai.weixin.qq.com/")
    {
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !IsWeixinHost(uri.Host))
            throw new BusinessException("微信返回不受信任的 HTTPS 地址");
        return new Uri(uri, path.TrimStart('/'));
    }

    /// <summary>飞书开放平台接入点。仅接受 https 的 feishu.cn 官方域，其余一律拒绝。</summary>
    public static Uri FeishuUri(string path, string root = FeishuApiBase)
    {
        if (string.IsNullOrWhiteSpace(root)) root = FeishuApiBase;
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !IsFeishuHost(uri.Host))
            throw new BusinessException("飞书接口地址不受信任（须为 https 的 feishu.cn 官方域名）");
        return new Uri(uri, path.TrimStart('/'));
    }

    public const string FeishuApiBase = "https://open.feishu.cn/";

    public static bool AllowedUri(Uri uri) => uri is { Scheme: "https", Port: 443 } &&
        string.IsNullOrEmpty(uri.UserInfo) && (IsQqHost(uri.Host) || IsWeixinHost(uri.Host) || IsFeishuHost(uri.Host));

    public static bool IsWeixinHost(string host) => host.Equals("weixin.qq.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsQqHost(string host) => host.Equals("qq.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>飞书开放平台与长连接接入点（含 msg-frontier 前线域）。</summary>
    public static bool IsFeishuHost(string host) => host.Equals("feishu.cn", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".feishu.cn", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        if (endpoint.Port != 443 || !(IsWeixinHost(endpoint.Host) || IsQqHost(endpoint.Host) || IsFeishuHost(endpoint.Host)))
            throw new ChannelProtocolException("HOST_DENIED");
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(x => !IsPublicIp(x)))
            throw new ChannelProtocolException("DNS_PRIVATE_IP");
        foreach (var ip in addresses)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(ip, endpoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new ChannelProtocolException("CONNECT_FAILED");
    }

    public static bool IsPublicIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] is 0 or 10 or 127 || b[0] >= 224) return false;
            if (b[0] == 100 && (b[1] & 0xc0) == 64) return false;
            if (b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31) return false;
            if (b[0] == 192 && b[1] is 0 or 168 || b[0] == 198 && b[1] is 18 or 19) return false;
            if (b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
            return true;
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(ip) || ip.IsIPv6UniqueLocal ||
            ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return false;
        var v = ip.GetAddressBytes();
        return (v[0] & 0xe0) == 0x20 && !(v[0] == 0x20 && v[1] == 0x02) &&
               !(v[0] == 0x20 && v[1] == 0x01 && v[2] == 0x0d && v[3] == 0xb8) &&
               !(v[0] == 0x20 && v[1] == 0x01 && v[2] == 0 && v[3] == 0); // Teredo 可嵌套私网 v4。
    }

    public void Dispose() { _http.Dispose(); WebSocketInvoker.Dispose(); _handler.Dispose(); }
}

/// <summary>仅暴露脱敏状态码，不把 HTTP 响应正文、敏感目标地址或 token 写入日志。</summary>
public sealed class ChannelProtocolException(string code) : Exception("通道平台调用失败")
{
    public string ErrorCode { get; } = code;
}
