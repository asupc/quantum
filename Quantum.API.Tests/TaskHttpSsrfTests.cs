using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Quantum.Application;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 任务外部请求的 SSRF 边界（ctx.Http / ctx.File.DownloadAsync）：只允许公网目标，
/// 拒绝回环/RFC1918/链路本地（云元数据）/CGNAT/组播/未指定与 IPv6 ULA、link-local、Teredo/6to4
/// （含 IPv4-mapped 形态）；域名按 DNS 全量解析结果判定，解析失败同样拒绝。
/// 覆盖「校验通过却未发请求」与「公开地址仍可正常请求」两侧。
/// </summary>
[Collection("ConstsState")]
public class TaskHttpSsrfTests : IDisposable
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _originalResolver;
    private readonly ConcurrentDictionary<string, IPAddress[]> _dns = new();

    public TaskHttpSsrfTests()
    {
        // 注入确定性 DNS：用例不依赖真实网络，且能构造「公网域名指向内网」的场景
        _originalResolver = NetworkGuard.ResolveHostAddressesAsync;
        NetworkGuard.ResolveHostAddressesAsync = (host, ct) =>
            _dns.TryGetValue(host, out var addrs)
                ? Task.FromResult(addrs)
                : Task.FromException<IPAddress[]>(new SocketException(11001));
    }

    public void Dispose() => NetworkGuard.ResolveHostAddressesAsync = _originalResolver;

    // ---------- 地址分类（拒绝侧全量覆盖） ----------

    [Theory]
    [InlineData("127.0.0.1")]        // 回环
    [InlineData("::1")]              // IPv6 回环
    [InlineData("10.0.0.1")]         // RFC1918
    [InlineData("172.16.0.1")]       // RFC1918
    [InlineData("192.168.1.1")]      // RFC1918
    [InlineData("169.254.169.254")]  // 链路本地 / 云元数据
    [InlineData("100.64.0.1")]       // CGNAT
    [InlineData("0.0.0.0")]          // 未指定
    [InlineData("224.0.0.1")]        // 组播
    [InlineData("fe80::1")]          // IPv6 link-local
    [InlineData("fc00::1")]          // IPv6 ULA
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped 回环（还原成 v4 后判定）
    [InlineData("::ffff:10.0.0.1")]  // IPv4-mapped 私网（同一漏洞形态）
    [InlineData("127.1")]            // 简短写法
    [InlineData("2130706433")]       // 十进制写法
    [InlineData("2002:7f00:1::")]    // 6to4 内嵌回环
    public void IsPublicIp_RejectsNonPublicAddresses(string text)
        => Assert.False(NetworkGuard.IsPublicIp(IPAddress.Parse(text)), $"{text} 应判为非公网");

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]   // IPv4-mapped 公网
    public void IsPublicIp_AcceptsPublicAddresses(string text)
        => Assert.True(NetworkGuard.IsPublicIp(IPAddress.Parse(text)), $"{text} 应判为公网");

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("[::1]", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("example.com", null)]      // 域名：非字面量，交由 DNS 判定
    [InlineData("localhost", null)]
    public void ClassifyLiteralHost_DistinguishesLiteralsFromNames(string host, bool? expected)
        => Assert.Equal(expected, NetworkGuard.ClassifyLiteralHost(host));

    // ---------- URL 校验：拒绝内网且不发出请求 ----------

    [Theory]
    [InlineData("http://127.0.0.1:9/x")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/admin")]
    [InlineData("http://[::1]:5088/api")]
    [InlineData("http://192.168.1.1:8080/")]
    public async Task EnsurePublicUrlAsync_RejectsInternalTargets(string url)
    {
        var error = await Assert.ThrowsAsync<BusinessException>(() => NetworkGuard.EnsurePublicUrlAsync(url));
        Assert.Contains("外部请求被拒绝", error.Message);
    }

    [Fact]
    public async Task EnsurePublicUrlAsync_RejectsPublicDomainResolvingToInternal()
    {
        // 公网域名改指内网（DNS rebinding 的静态形态）：解析结果含内网地址即拒绝
        _dns["rebind.example"] = [IPAddress.Parse("1.2.3.4"), IPAddress.Parse("192.168.1.10")];

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => NetworkGuard.EnsurePublicUrlAsync("https://rebind.example/x"));
        Assert.Contains("外部请求被拒绝", error.Message);
    }

    [Fact]
    public async Task EnsurePublicUrlAsync_UnresolvableDomain_IsRejected()
    {
        var error = await Assert.ThrowsAsync<BusinessException>(
            () => NetworkGuard.EnsurePublicUrlAsync("https://no-such-host.example/x"));
        Assert.Contains("解析失败", error.Message);
    }

    [Fact]
    public async Task EnsurePublicUrlAsync_RejectsMissingOrNonHttpTarget()
    {
        await Assert.ThrowsAsync<BusinessException>(() => NetworkGuard.EnsurePublicUrlAsync(null));
        await Assert.ThrowsAsync<BusinessException>(() => NetworkGuard.EnsurePublicUrlAsync("file:///etc/passwd"));
    }

    [Fact]
    public async Task EnsurePublicUrlAsync_AcceptsPublicTargets()
    {
        _dns["example.com"] = [IPAddress.Parse("8.8.8.8")];

        await NetworkGuard.EnsurePublicUrlAsync("https://example.com/x.jpg");
        // 公网 IP 字面量无需解析
        await NetworkGuard.EnsurePublicUrlAsync("http://8.8.8.8/x");
    }

    // ---------- 运维白名单：内网自建服务（qBittorrent/NAS）需显式登记才放行 ----------

    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("10.0.0.0/24")]
    [InlineData("qb.lan")]
    public async Task EnsurePublicHostAsync_AllowlistedInternalTarget_IsAccepted(string entry)
    {
        using var scope = new AllowlistScope(entry);

        // 白名单条目本身即目标：内网 IP / 网段内 IP / 主机名三种形态都要放行
        var host = entry.Contains('/') ? "10.0.0.7" : entry;
        await NetworkGuard.EnsurePublicHostAsync(host);
        await NetworkGuard.EnsurePublicUrlAsync($"http://{host}/x");
    }

    [Fact]
    public async Task EnsurePublicHostAsync_NotAllowlistedInternalTarget_StillRejected()
    {
        using var scope = new AllowlistScope("192.168.1.50,10.0.0.0/24,qb.lan");

        // 白名单不得顺带放行其它内网目标：同段外、其它私网段、白名单主机的子域、本机
        await Assert.ThrowsAsync<BusinessException>(() => NetworkGuard.EnsurePublicHostAsync("192.168.1.51"));
        await Assert.ThrowsAsync<BusinessException>(() => NetworkGuard.EnsurePublicHostAsync("10.0.1.7"));
        await Assert.ThrowsAsync<BusinessException>(() => NetworkGuard.EnsurePublicHostAsync("evil.qb.lan"));
        await Assert.ThrowsAsync<BusinessException>(() => NetworkGuard.EnsurePublicHostAsync("127.0.0.1"));
    }

    [Fact]
    public async Task EnsurePublicHostAsync_EmptyAllowlist_KeepsDefaultStrict()
    {
        using var scope = new AllowlistScope(null);

        await Assert.ThrowsAsync<BusinessException>(() => NetworkGuard.EnsurePublicHostAsync("192.168.1.50"));
        Assert.False(NetworkGuard.IsAllowlisted("192.168.1.50"));
    }

    [Theory]
    [InlineData("10.0.0.7", "10.0.0.0/24", true)]
    [InlineData("10.0.1.7", "10.0.0.0/24", false)]
    [InlineData("192.168.1.50", "192.168.1.50", true)]
    [InlineData("192.168.1.50", "192.168.0.0/16", true)]
    [InlineData("192.168.1.50", "999.0.0.0/24", false)]    // 非法 CIDR 不得放行
    [InlineData("192.168.1.50", "192.168.1.50/40", false)] // 非法前缀长度不得放行
    public void IsAllowlisted_MatchesExactHostAndValidCidrOnly(string host, string entry, bool expected)
    {
        using var scope = new AllowlistScope(entry);

        Assert.Equal(expected, NetworkGuard.IsAllowlisted(host));
    }

    /// <summary>临时替换配置里的内网白名单（配置写锁下改内存快照，用完还原）。</summary>
    private sealed class AllowlistScope : IDisposable
    {
        private readonly string _original;

        public AllowlistScope(string allowlist)
        {
            _original = SystemConfigHelper.GetSetting().TaskNetworkAllowlist;
            SystemConfigHelper.UpdateSetting(current => current.TaskNetworkAllowlist = allowlist);
        }

        public void Dispose()
            => SystemConfigHelper.UpdateSetting(current => current.TaskNetworkAllowlist = _original);
    }

    // ---------- ctx.File.DownloadAsync：内网地址在发请求前即拒绝 ----------

    [Theory]
    [InlineData("http://127.0.0.1:9/x")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]:5088/api")]
    public async Task DownloadAsync_InternalTarget_ThrowsWithoutIssuingRequest(string url)
    {
        var root = NewTempRoot();
        try
        {
            var counter = new CountingHandler();
            var facade = new QuantumFileFacade(root, new HttpClient(counter));

            var error = await Assert.ThrowsAsync<BusinessException>(() => facade.DownloadAsync(url, "x.bin"));

            Assert.Contains("外部请求被拒绝", error.Message);
            Assert.Equal(0, counter.Calls);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadAsync_InternalTarget_NoRelativePath_TraversalStillGuarded()
    {
        // 回归护栏：新增校验不得削弱原有 subDir confinement
        var root = NewTempRoot();
        try
        {
            var facade = new QuantumFileFacade(root, new HttpClient(new CountingHandler()));
            await Assert.ThrowsAsync<BusinessException>(() => facade.DownloadAsync("https://example.com/a", "a", "../escape"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadAsync_PublicTarget_StillDownloads()
    {
        // 公网目标不受影响：校验通过后照常下载落盘
        _dns["example.com"] = [IPAddress.Parse("8.8.8.8")];
        var root = NewTempRoot();
        try
        {
            var counter = new CountingHandler();
            var facade = new QuantumFileFacade(root, new HttpClient(counter));

            var result = await facade.DownloadAsync("https://example.com/x.jpg", "x.jpg");

            Assert.Equal(1, counter.Calls);
            Assert.True(File.Exists(result.FullPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---------- ctx.Http：逐请求目标校验（代理复用连接时连接回调不再触发） ----------

    [Fact]
    public async Task TaskUrlGuardHandler_BlocksInternalTargetBeforeInnerHandler()
    {
        var counter = new CountingHandler();
        using var client = new HttpClient(new TaskUrlGuardHandler(counter));

        await Assert.ThrowsAsync<BusinessException>(() => client.GetAsync("http://127.0.0.1:9/x"));
        await Assert.ThrowsAsync<BusinessException>(() => client.GetAsync("http://169.254.169.254/latest/meta-data/"));

        Assert.Equal(0, counter.Calls);
    }

    [Fact]
    public async Task TaskUrlGuardHandler_AllowsPublicTarget()
    {
        _dns["example.com"] = [IPAddress.Parse("8.8.8.8")];
        var counter = new CountingHandler();
        using var client = new HttpClient(new TaskUrlGuardHandler(counter));

        var response = await client.GetAsync("https://example.com/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public void CreateTaskHttpClient_HasGuardAndNoAutoRedirect()
    {
        // 任务 HttpClient 必须是「逐请求校验 + 禁自动重定向」形态：
        // 自动重定向会让公网地址被 302 引向内网（连接回调只在重定向目标建连时才校验，代理复用路径更会绕过）。
        var guard = TaskExcuteService.CreateTaskHttpHandlerForTest(enableProxy: false);
        var sockets = Assert.IsType<SocketsHttpHandler>(guard.InnerHandler);
        Assert.False(sockets.AllowAutoRedirect, "任务外部请求必须关闭自动重定向（防公网 → 内网跳转）");
        Assert.NotNull(sockets.ConnectCallback);
        // 代理开关保留：两种形态各自独立缓存
        var proxied = TaskExcuteService.CreateTaskHttpHandlerForTest(enableProxy: true);
        Assert.True(Assert.IsType<SocketsHttpHandler>(proxied.InnerHandler).UseProxy);
    }

    [Fact]
    public async Task ConnectTaskHttpAsync_RefusesHostResolvingToInternal()
    {
        // 连接级第二道防线（直连免代理路径）：域名改指内网时建连即被拒
        _dns["rebind.example"] = [IPAddress.Parse("127.0.0.1")];
        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = NetworkGuard.ConnectTaskHttpAsync
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://rebind.example/x"));
        Assert.Contains("外部请求被拒绝", error.InnerException?.Message ?? "");
    }

    [Fact]
    public async Task ConnectTaskHttpAsync_ProxyEndpoint_IsNotTreatedAsTarget()
    {
        // 代理场景：连接回调看到的是代理端点（常为本机），不得按目标口径拦掉代理本身；
        // 目标校验由 TaskUrlGuardHandler 在每个请求上完成。
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                var buffer = new byte[2048];
                _ = await accepted.GetStream().ReadAsync(buffer);
                await accepted.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
            }
            catch { /* 用例结束时的收尾失败忽略 */ }
        });
        try
        {
            _dns["example.com"] = [IPAddress.Parse("8.8.8.8")];
            using var client = new HttpClient(new TaskUrlGuardHandler(new SocketsHttpHandler
            {
                UseProxy = true,
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                AllowAutoRedirect = false,
                ConnectCallback = NetworkGuard.ConnectTaskHttpAsync
            }));

            // 明文 HTTP 经代理：连接回调的端点是代理（127.0.0.1），目标 example.com 已由请求级校验通过
            var response = await client.GetAsync("http://example.com/x");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectTaskHttpAsync_ProxiedRequestWithInternalTarget_StillBlockedByRequestGuard()
    {
        // 代理不能成为绕过边界的跳板：目标为内网时，逐请求校验在连接回调之前即拒绝
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var counter = new CountingHandler();
            using var client = new HttpClient(new TaskUrlGuardHandler(counter));

            await Assert.ThrowsAsync<BusinessException>(() => client.GetAsync("http://127.0.0.1:9/x"));

            Assert.Equal(0, counter.Calls);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "quantum-ssrf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>计数型内层 handler：断言「校验失败时不发请求」。</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        }
    }
}
