using Microsoft.AspNetCore.Http;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// HttpContext 扩展：取 IP 与取用户名（Name claim）。
/// 取 IP 不再直读 X-Forwarded-For（防伪造绕过限流/白名单）：
/// 可信代理场景由 ForwardedHeaders 中间件改写 RemoteIpAddress，业务侧只认 RemoteIpAddress。
/// </summary>
public class HttpContextExtensionTests
{
    private static HttpContext BuildContext(string remoteIp = "10.0.0.5", string forwarded = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);
        if (forwarded != null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwarded;
        }
        return context;
    }

    [Fact]
    public void GetUserIp_UsesRemoteIpWithoutForwardedHeader()
    {
        Assert.Equal("10.0.0.5", BuildContext().GetUserIp());
    }

    [Fact]
    public void GetUserIp_IgnoresUntrustedForwardedHeader()
    {
        // 安全语义：未过可信代理链时 X-Forwarded-For 一律不采信（整串/单值都不得影响结果），
        // 防伪造 XFF 轮换 IP 绕过登录限流与外触白名单
        Assert.Equal("10.0.0.5", BuildContext(forwarded: "172.16.0.1, 10.0.0.5").GetUserIp());
        Assert.Equal("10.0.0.5", BuildContext(forwarded: "1.2.3.4").GetUserIp());
    }

    [Fact]
    public void GetUserId_ReturnsNameClaimValue()
    {
        var context = BuildContext();
        var identity = new System.Security.Claims.ClaimsIdentity();
        identity.AddClaim(new System.Security.Claims.Claim("Name", "user-001"));
        context.User = new System.Security.Claims.ClaimsPrincipal(identity);

        Assert.Equal("user-001", context.GetUserId());
    }

    [Fact]
    public void GetUserId_ReturnsEmptyWithoutNameClaim()
    {
        Assert.Equal("", BuildContext().GetUserId());
    }
}
