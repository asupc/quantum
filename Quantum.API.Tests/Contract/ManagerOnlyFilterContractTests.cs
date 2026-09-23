using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Quantum.Entities.Result;
using Quantum.Web.Filters;

namespace Quantum.API.Tests.Contract;

/// <summary>
/// 契约测试：[ManagerOnly] 的正向 claim 语义——仅 "Manager"="true" 放行；
/// 普通用户令牌（无该 claim）、Open AppKey 令牌与任务临时令牌（同样无该 claim）一律 401。
/// </summary>
[Collection("ConstsState")]
public class ManagerOnlyFilterContractTests
{
    static ManagerOnlyFilterContractTests() => Log4NetTestSetup.EnsureRepository();

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
        await new ManagerOnlyAttribute().OnAuthorizationAsync(context);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Anonymous_Request_Is_Rejected()
    {
        var context = CreateContext();
        await new ManagerOnlyAttribute().OnAuthorizationAsync(context);

        var envelope = Assert.IsType<ResultModel>(Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.Equal(401, envelope.Code);
        Assert.Equal(200, context.HttpContext.Response.StatusCode); // HTTP 恒 200
    }

    [Fact]
    public async Task App_User_Token_Without_Manager_Claim_Is_Rejected()
    {
        var context = CreateContext();
        SetPrincipal(context,
            new Claim("Name", "user-a"),
            new Claim("UserId", "uid-1"),
            new Claim("DeviceId", "dev-1"));

        await new ManagerOnlyAttribute().OnAuthorizationAsync(context);

        var envelope = Assert.IsType<ResultModel>(Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.Equal(401, envelope.Code);
    }

    [Fact]
    public async Task Manager_Claim_False_Value_Is_Rejected()
    {
        var context = CreateContext();
        SetPrincipal(context, new Claim("Manager", "false"));

        await new ManagerOnlyAttribute().OnAuthorizationAsync(context);

        Assert.NotNull(context.Result);
    }

    [Fact]
    public async Task Manager_Claim_True_Passes()
    {
        var context = CreateContext();
        SetPrincipal(context,
            new Claim("UserId", "uid-1"),
            new Claim("Manager", "true"));

        await new ManagerOnlyAttribute().OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }
}
