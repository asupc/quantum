using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Quantum.Utils;

/// <summary>
/// 任务外部请求（ctx.Http / ctx.File.DownloadAsync）的 SSRF 网络边界：只允许公网目标。
/// 本机回环、RFC1918 私网、链路本地（含云元数据 169.254.169.254）、CGNAT(100.64/10)、组播/保留段、
/// 未指定地址，以及 IPv6 的 ULA/link-local/组播/Teredo/6to4 等一律拒绝（含 IPv4-mapped IPv6 形态）。
/// 域名的判定口径是「DNS 解析结果全量为公网」，任一地址非公网即整体拒绝（防公网域名改指内网）。
/// 判定口径与通道网络边界 <c>ChannelNetwork.IsPublicIp</c> 一致（该类不改动，此处为任务侧独立实现）。
/// 除连接级校验外，另设逐请求校验 <see cref="TaskUrlGuardHandler"/>：代理场景下连接池的键是代理地址，
/// 复用同一条代理连接发往不同目标不会再触发连接回调，只在连接回调校验会漏掉该路径。
/// </summary>
public static class NetworkGuard
{
    /// <summary>
    /// DNS 解析接缝：生产恒为系统解析；单测注入确定性结果，避免用例依赖真实网络。
    /// </summary>
    internal static Func<string, CancellationToken, Task<IPAddress[]>> ResolveHostAddressesAsync { get; set; }
        = (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    /// <summary>
    /// 地址是否属于「可公网直连」：false 覆盖回环/私网/链路本地/CGNAT/组播/未指定/ULA/Teredo 等。
    /// </summary>
    public static bool IsPublicIp(IPAddress ip)
    {
        // IPv4-mapped IPv6（::ffff:127.0.0.1）必须还原成 v4 判定，否则 ::ffff:10.0.0.1 这类会漏放
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

    /// <summary>
    /// IP 字面量判定：非字面量返回 null（调用方按域名处理）；字面量返回其是否公网。
    /// 覆盖简短/十进制/十六进制写法（127.1、2130706433、0x7f000001）与方括号 IPv6（[::1]）。
    /// </summary>
    public static bool? ClassifyLiteralHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var text = host.Trim();
        if (text.Length > 1 && text[0] == '[' && text[^1] == ']') text = text[1..^1];
        return IPAddress.TryParse(text, out var ip) ? IsPublicIp(ip) : null;
    }

    /// <summary>
    /// 校验任务外部请求 URL：仅 http/https，且目标主机为公网；不通过抛中文 BusinessException。
    /// </summary>
    public static async Task EnsurePublicUrlAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new BusinessException("外部请求地址必须以 http/https 开头！");
        }
        await EnsurePublicHostAsync(uri.Host, ct);
    }

    /// <summary>
    /// 校验主机为公网：IP 字面量直接分类；域名走 DNS 解析并要求全部地址为公网（解析失败同样拒绝）。
    /// 命中运维白名单（Setting.TaskNetworkAllowlist）的主机直接放行——内网自建服务只能靠显式登记访问。
    /// </summary>
    public static async Task EnsurePublicHostAsync(string host, CancellationToken ct = default)
    {
        if (IsAllowlisted(host))
        {
            return;
        }
        var literal = ClassifyLiteralHost(host);
        if (literal == true) return;
        if (literal == false)
        {
            throw new BusinessException($"外部请求被拒绝：目标地址 {Show(host)} 指向内网/本机，不在允许访问范围内。");
        }
        await ResolvePublicAddressesAsync(host, ct);
    }

    /// <summary>
    /// 主机是否命中运维白名单（Setting.TaskNetworkAllowlist）：逗号/分号/空白分隔，条目可为 IP、
    /// CIDR（v4/v6）或主机名（精确匹配、忽略大小写；不匹配子域，避免 "example.com" 顺带放行
    /// "evil.example.com"）。配置不可读时按「未配置」处理（fail-closed，不放行）。
    /// </summary>
    public static bool IsAllowlisted(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        string configured;
        try
        {
            configured = SystemConfigHelper.GetSetting()?.TaskNetworkAllowlist;
        }
        catch
        {
            // 配置不可读（如启动早期）不得因此放行
            return false;
        }
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }
        var target = host.Trim().Trim('[', ']');
        foreach (var entry in configured.Split([',', ';', '\n', '\r', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/'))
            {
                if (IPAddress.TryParse(target, out var ip) && IsInCidr(ip, entry))
                {
                    return true;
                }
                continue;
            }
            if (string.Equals(entry, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>IPv4/v6 地址是否落在 CIDR 内（前缀长度非法一律 false）。</summary>
    private static bool IsInCidr(IPAddress ip, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix))
        {
            return false;
        }
        var address = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        var baseAddress = network.IsIPv4MappedToIPv6 ? network.MapToIPv4() : network;
        if (address.AddressFamily != baseAddress.AddressFamily)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        var baseBytes = baseAddress.GetAddressBytes();
        var maxPrefix = bytes.Length * 8;
        if (prefix < 0 || prefix > maxPrefix)
        {
            return false;
        }
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Min(8, Math.Max(0, prefix - i * 8));
            if (bits == 0)
            {
                break;
            }
            var mask = (byte)(0xFF << (8 - bits));
            if ((bytes[i] & mask) != (baseBytes[i] & mask))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 解析域名并返回全部公网地址（任一非公网或解析失败即拒绝）；域名的字面量形式返回空数组。
    /// 白名单主机返回空数组（= 由调用方按主机名直连，不做公网口径限制）。
    /// </summary>
    internal static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken ct)
    {
        if (IsAllowlisted(host))
        {
            return [];
        }
        var literal = ClassifyLiteralHost(host);
        if (literal == false)
        {
            throw new BusinessException($"外部请求被拒绝：目标地址 {Show(host)} 指向内网/本机，不在允许访问范围内。");
        }
        if (literal == true)
        {
            return [];
        }
        IPAddress[] addresses;
        try
        {
            addresses = await ResolveHostAddressesAsync(host, ct);
        }
        catch (Exception e) when (e is SocketException or ArgumentException or InvalidOperationException)
        {
            throw new BusinessException($"外部请求被拒绝：目标域名 {Show(host)} 解析失败（{e.Message}）。");
        }
        if (addresses == null || addresses.Length == 0 || addresses.Any(n => !IsPublicIp(n)))
        {
            throw new BusinessException($"外部请求被拒绝：目标域名 {Show(host)} 解析到内网/本机地址，不在允许访问范围内。");
        }
        return addresses;
    }

    /// <summary>
    /// 任务 HttpClient 的连接回调：直连时按公网口径校验本次目标并「用校验过的地址」建连（防解析换址）；
    /// 经代理时端点即代理自身（常为本机 127.0.0.1:10808），不按目标口径限制——代理是运维配置的可信基础设施，
    /// 而目标校验由逐请求的 <see cref="TaskUrlGuardHandler"/> 完成（代理复用连接时本回调不会被再触发，
    /// 只在连接级校验会漏掉「复用代理连接发往任意目标」这条路径）。
    /// </summary>
    public static async ValueTask<Stream> ConnectTaskHttpAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var initial = context.InitialRequestMessage;
        // HTTPS 经代理：此处是 CONNECT 隧道建立，请求 URI 指向代理而非目标，只能按代理端点建连
        if (initial?.Method == HttpMethod.Connect)
        {
            return await OpenSocketAsync(endpoint.Host, endpoint.Port, ct);
        }
        var targetHost = initial?.RequestUri?.Host;
        if (targetHost != null && !HostEquals(endpoint.Host, targetHost))
        {
            // 明文 HTTP 经代理：端点主机是代理，目标已在请求级校验
            return await OpenSocketAsync(endpoint.Host, endpoint.Port, ct);
        }
        // 直连：解析目标域名并要求全量为公网地址，然后只连这些已校验的地址
        var addresses = await ResolvePublicAddressesAsync(targetHost ?? endpoint.Host, ct);
        if (addresses.Length == 0)
        {
            // 目标是公网 IP 字面量：无解析环节
            return await OpenSocketAsync(endpoint.Host, endpoint.Port, ct);
        }
        SocketException last = null;
        foreach (var ip in addresses)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(ip, endpoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                last = e;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException($"无法连接外部请求目标 {Show(endpoint.Host)}", last);
    }

    /// <summary>按主机名（逐地址尝试）建立连接：仅用于代理端点等已判定为可信的目标。</summary>
    private static async Task<Stream> OpenSocketAsync(string host, int port, CancellationToken ct)
    {
        // IP 字面量（代理端点常为本机 127.0.0.1）直接连，省一次无谓解析且不受 DNS 接缝影响
        var literal = IPAddress.TryParse(host.Trim('[', ']'), out var parsed) ? parsed : null;
        IPAddress[] addresses;
        try
        {
            addresses = literal != null ? [literal] : await ResolveHostAddressesAsync(host, ct);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            throw new HttpRequestException($"无法解析外部请求端点 {Show(host)}", e);
        }
        SocketException last = null;
        foreach (var ip in addresses)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(ip, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                last = e;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException($"无法连接外部请求端点 {Show(host)}", last);
    }

    private static bool HostEquals(string a, string b)
        => string.Equals(a?.Trim('[', ']'), b?.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);

    /// <summary>日志/异常里回显主机：去掉方括号并限长，避免把超长伪造主机名整段带回。</summary>
    private static string Show(string host)
        => string.IsNullOrEmpty(host) ? "(空)" : (host.Length > 100 ? host[..100] + "…" : host.Trim('[', ']'));
}

/// <summary>
/// 逐请求目标校验（任务 ctx.Http 专用）：连接级回调只在建连时刻触发，而连接池复用不会再触发它——
/// 代理场景下池键是代理地址，一条已建的代理连接会被复用发往任意目标，故每个请求都必须校验目标主机。
/// </summary>
public sealed class TaskUrlGuardHandler : DelegatingHandler
{
    public TaskUrlGuardHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri == null)
        {
            throw new BusinessException("外部请求缺少目标地址！");
        }
        await NetworkGuard.EnsurePublicUrlAsync(request.RequestUri.AbsoluteUri, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
