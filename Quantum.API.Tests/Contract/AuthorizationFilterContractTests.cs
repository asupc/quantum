using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Quantum.Web.Filters;
using Quantum.Utils;
using Quantum.Entities.Result;

namespace Quantum.API.Tests.Contract;

/// <summary>
/// 契约测试：CustomAuthorizationFilter 的鉴权语义。
/// 无 token / token 无效时返回 HTTP 200 + body Code=401——Code 401 是前端强制登出的触发信号（axios.js:59）。
/// </summary>
[Collection("ConstsState")]
public class AuthorizationFilterContractTests
{
    static AuthorizationFilterContractTests() => Log4NetTestSetup.EnsureRepository();

    private const string TestKey = "contract-test-symmetric-security-key-0123456789abcdef";
    private const string TestIssuer = "Issuer.ContractTest";
    private const string TestAudience = "Audience.ContractTest";

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

    private static string CreateValidToken()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey));
        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: [new Claim("Id", "U1")],
            expires: DateTime.Now.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task AllowAnonymous_Endpoint_Passes_Without_Token()
    {
        var original = (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience);
        try
        {
            Consts.SymmetricSecurityKey = TestKey;
            Consts.SecurityIssuer = TestIssuer;
            Consts.SecurityAudience = TestAudience;

            var context = CreateContext(new AllowAnonymousAttribute());
            await new CustomAuthorizationFilter().OnAuthorizationAsync(context);

            Assert.Null(context.Result);
        }
        finally
        {
            (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience) = original;
        }
    }

    [Fact]
    public async Task Missing_Token_Returns_Code401_Body_With_Http200()
    {
        var context = CreateContext();

        await new CustomAuthorizationFilter().OnAuthorizationAsync(context);

        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        var envelope = Assert.IsType<ResultModel>(objectResult.Value);
        Assert.Equal(401, envelope.Code);
        Assert.Equal(200, context.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Invalid_Token_Returns_Code401_With_Fixed_Message()
    {
        var context = CreateContext();
        context.HttpContext.Request.Headers.Authorization = "Bearer not-a-jwt";

        await new CustomAuthorizationFilter().OnAuthorizationAsync(context);

        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        var envelope = Assert.IsType<ResultModel>(objectResult.Value);
        Assert.Equal(401, envelope.Code);
        Assert.Equal("Token验证失败", envelope.Message);
    }

    [Fact]
    public async Task Valid_Token_Passes_Through()
    {
        var original = (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience);
        try
        {
            Consts.SymmetricSecurityKey = TestKey;
            Consts.SecurityIssuer = TestIssuer;
            Consts.SecurityAudience = TestAudience;

            var context = CreateContext();
            context.HttpContext.Request.Headers.Authorization = $"Bearer {CreateValidToken()}";

            await new CustomAuthorizationFilter().OnAuthorizationAsync(context);

            Assert.Null(context.Result);
        }
        finally
        {
            (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience) = original;
        }
    }
}
