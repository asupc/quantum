using System.Net;
using System.Net.Sockets;

// 测试探针只允许直连 QQ/微信官方域名的公网地址，不允许代理、私网或 DNS 回绑后的二次解析。
internal static class ProbeNetwork
{
    internal static readonly SocketsHttpHandler Handler = new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 4,
        ConnectCallback = ConnectAsync
    };

    internal static readonly HttpMessageInvoker WebSocketInvoker = new(Handler, disposeHandler: false);
    internal static int ConnectionChecks;

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref ConnectionChecks);
        var endpoint = context.DnsEndPoint;
        if (endpoint.Port != 443 || !IsAllowedHost(endpoint.Host))
            throw new ProbeException("测试探针拒绝非官方 HTTPS/WSS 目标地址。");
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new ProbeException("平台域名解析到非公网 IP；拒绝连接，防止 DNS 回绑。");

        // socket 按筛选后的单个 IP 直连；TLS/SNI/证书仍由 HttpClient 使用原域名校验。
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, endpoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e) { last = e; socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new ProbeException(last is null ? "所有公网 IP 均不可达。" : "平台公网 IP 连接失败（已隐藏底层地址）。");
    }

    internal static bool IsAllowedHost(string host) =>
        host.Equals("qq.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("weixin.qq.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] is 0 or 10 or 127 || b[0] >= 224) return false;
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return false; // shared CGNAT
            if (b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31) return false;
            if (b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 88 && b[2] == 99)) return false;
            if (b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) return false;
            if (b[0] == 192 && b[1] == 0 && b[2] == 2 || b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
            return true;
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(ip) ||
            ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal)
            return false;
        var bytes = ip.GetAddressBytes();
        if ((bytes[0] & 0xE0) != 0x20) return false; // 仅 2000::/3 全球单播。
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return false; // 6to4 可嵌套内网 v4。
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false; // 文档网段。
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0) return false; // Teredo。
        return true;
    }
}
