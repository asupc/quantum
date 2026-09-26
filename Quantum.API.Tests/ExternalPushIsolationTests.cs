using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using System.Security.Claims;
using System.Security.Principal;
using Quantum.Entities.Model;
using Quantum.Entities.Result;
using Quantum.Utils;
using Quantum.Web.Filters;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 鉴权面双向隔离回归（审核项 R-09 与 G2「Open/匿名不开放运行历史」）：
/// - PushKey 凭据**只能**抵达外部推送端点，抵达旧 Task/Logs/Open 端点必须被拒；
/// - 旧 Open AppKey 换签的通用 JWT **不能**读执行历史（它只验签的过滤器挡不住，需正向判定主体）；
/// - 未认证与「Id 不存在」在外部推送端点上不可区分。
/// </summary>
public class ExternalPushIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;

    public ExternalPushIsolationTests()
    {
        (_connection, _db) = AppTestDb.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static AuthorizationFilterContext Context(string authorizationHeader, ClaimsPrincipal user = null)
    {
        var http = new DefaultHttpContext();
        if (authorizationHeader != null)
        {
            http.Request.Headers["Authorization"] = authorizationHeader;
        }
        http.User = user ?? new ClaimsPrincipal();
        return new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor { EndpointMetadata = new List<object>() }),
            new List<IFilterMetadata>());
    }

    private static int? EnvelopeCode(ObjectResult result)
        => (result?.Value as ResultModel)?.Code;

    private static IServiceProvider ProviderWith(IQuantumDbContext db) => new ServiceCollection()
        .AddSingleton(new Quantum.Application.ExternalPushCredentialService(db))
        .BuildServiceProvider();

    private async Task<(string Id, string Secret)> NewLiveCredential()
    {
        var service = new Quantum.Application.ExternalPushCredentialService(_db);
        var created = await service.CreateAsync("接入方", 60, 1000, null, null);
        await service.SetEnabledAsync(created.Credential.Id, true, "test");
        return (created.Credential.Id, created.Secret);
    }

    // -------------------------------------------------- PushKey 抵达旧端点必须被拒

    [Fact]
    public async Task PushKeyHeader_CannotReach_JwtProtectedEndpoints()
    {
        var (id, _) = await NewLiveCredential();

        var context = Context($"PushKey {id}.whatever");
        await new CustomAuthorizationFilter().OnAuthorizationAsync(context);

        // 旧端点只认 Bearer JWT：PushKey 串过不了验签 → 401
        Assert.NotNull(context.Result);
        Assert.Equal(401, EnvelopeCode((ObjectResult)context.Result));
    }

    [Fact]
    public async Task PushKeyEndpoint_RejectsMissingAndForeignAuthorization()
    {
        var (id, secret) = await NewLiveCredential();
        var filter = new PushKeyAuthAttribute();

        var noHeader = Context(null);
        noHeader.HttpContext.RequestServices = ProviderWith(_db);
        await filter.OnAuthorizationAsync(noHeader);
        Assert.Equal(401, EnvelopeCode((ObjectResult)noHeader.Result));

        // 合法 Manager/用户 JWT 也拿不到推送权限（凭据体系与 JWT 互不相通）
        var bearer = Context("Bearer not-a-real-jwt-at-all");
        bearer.HttpContext.RequestServices = ProviderWith(_db);
        await filter.OnAuthorizationAsync(bearer);
        Assert.Equal(401, EnvelopeCode((ObjectResult)bearer.Result));

        // 密钥错与 Id 不存在同为 401，不给可区分信号
        var wrongSecret = Context($"PushKey {id}.wrong-wrong-wrong-wrong-wrong");
        wrongSecret.HttpContext.RequestServices = ProviderWith(_db);
        await filter.OnAuthorizationAsync(wrongSecret);
        var ghost = Context("PushKey absent000000.something-something-something");
        ghost.HttpContext.RequestServices = ProviderWith(_db);
        await filter.OnAuthorizationAsync(ghost);
        Assert.Equal(EnvelopeCode((ObjectResult)wrongSecret.Result), EnvelopeCode((ObjectResult)ghost.Result));
        Assert.Equal(401, EnvelopeCode((ObjectResult)ghost.Result));
    }

    [Fact]
    public async Task PushKeyEndpoint_AcceptsLiveCredential_AndStashesNoPrincipal()
    {
        var (id, secret) = await NewLiveCredential();
        var context = Context($"PushKey {id}.{secret}");
        context.HttpContext.RequestServices = ProviderWith(_db);

        await new PushKeyAuthAttribute().OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        var stashed = Assert.IsType<ExternalPushCredentialModel>(
            context.HttpContext.Items[PushKeyAuthAttribute.CredentialItemKey]);
        Assert.Equal(id, stashed.Id);
        // 关键：不产出 ClaimsPrincipal，也不带 Manager claim——推送凭据永远换不来身份
        Assert.False(context.HttpContext.IsManager());
        Assert.Equal("", context.HttpContext.GetUserId());
    }

    // -------------------------------------------------- Open 令牌读不到执行历史

    private static ClaimsPrincipal PrincipalWithName(string name)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("Name", name) }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task RealPrincipal_RejectsOpenAppKeyToken()
    {
        var context = Context("Bearer x", PrincipalWithName(HttpContextExtension.OpenAppTokenName));

        await new RealPrincipalAttribute().OnAuthorizationAsync(context);

        Assert.Equal(401, EnvelopeCode((ObjectResult)context.Result));
    }

    [Fact]
    public async Task RealPrincipal_RejectsAnonymousAndAcceptsLoginSubject()
    {
        var anonymous = Context("Bearer x");
        await new RealPrincipalAttribute().OnAuthorizationAsync(anonymous);
        Assert.Equal(401, EnvelopeCode((ObjectResult)anonymous.Result));

        var real = Context("Bearer x", PrincipalWithName("admin"));
        await new RealPrincipalAttribute().OnAuthorizationAsync(real);
        Assert.Null(real.Result);
    }

    [Fact]
    public void OpenAppTokenName_IsSingleSource()
    {
        // 常量只允许一份事实源（Utils），Application 侧引用它——避免两处字面量漂移
        Assert.Equal("______App-Key-Auth", HttpContextExtension.OpenAppTokenName);
        Assert.True(new DefaultHttpContext().IsOpenAppToken() == false);
    }
}
