using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Quantum.Entities.Result;
using Quantum.Utils;
using Quantum.Web.Filters;

namespace Quantum.API.Tests.Contract;

/// <summary>
/// 登录过滤器只接受当前账号的有效登录用途声明。
/// </summary>
[Collection("ConstsState")]
public class LoggedInUserFilterContractTests
{
    static LoggedInUserFilterContractTests() => Log4NetTestSetup.EnsureRepository();

    private static AuthorizationFilterContext CreateContext(params object[] endpointMetadata)
    {
        var httpContext = new DefaultHttpContext();
        var descriptor = new ActionDescriptor
        {
            EndpointMetadata = new List<object>(endpointMetadata)
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        return new AuthorizationFilterContext(actionContext, []);
    }

    private static void SetPrincipal(AuthorizationFilterContext context, params Claim[] claims)
    {
        context.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task AllowAnonymous_Endpoint_Passes_Without_Claim()
    {
        var context = CreateContext(new AllowAnonymousAttribute());
        await new LoggedInUserAttribute().OnAuthorizationAsync(context);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Anonymous_Request_Is_Rejected()
    {
        var context = CreateContext();
        await new LoggedInUserAttribute().OnAuthorizationAsync(context);

        var envelope = Assert.IsType<ResultModel>(Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.Equal(401, envelope.Code);
        Assert.Equal(200, context.HttpContext.Response.StatusCode); // HTTP 恒 200
    }

    [Fact]
    public async Task Other_Account_Is_Rejected()
    {
        using var setting = new TestLoginSettingScope();
        var context = CreateContext();
        SetPrincipal(context,
            new Claim("Name", "user-a"),
            new Claim("TokenPurpose", "User"),
            new Claim("LoginTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new Claim("UserId", "uid-1"),
            new Claim("DeviceId", "dev-1"));

        await new LoggedInUserAttribute().OnAuthorizationAsync(context);

        var envelope = Assert.IsType<ResultModel>(Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.Equal(401, envelope.Code);
    }

    [Fact]
    public async Task Open_Token_Is_Rejected()
    {
        var context = CreateContext();
        SetPrincipal(context, new Claim("Name", HttpContextExtension.OpenAppTokenName),
            new Claim("TokenPurpose", "Open"));

        await new LoggedInUserAttribute().OnAuthorizationAsync(context);

        Assert.NotNull(context.Result);
    }

    [Fact]
    public async Task Login_Token_Passes_Without_Role_Claim()
    {
        using var setting = new TestLoginSettingScope();
        var context = CreateContext();
        SetPrincipal(context,
            new Claim("Name", "tester"),
            new Claim("TokenPurpose", "User"),
            new Claim("LoginTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));

        await new LoggedInUserAttribute().OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }
}
