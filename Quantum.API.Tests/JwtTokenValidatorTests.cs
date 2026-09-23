using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Quantum.API.Tests.Contract;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// JWT 共享验签器契约：CustomAuthorizationFilter、标准 JwtBearer 中间件与 /ws/app 握手共用。
/// 校验规则 = 签名有效 + Issuer/Audience 匹配 + 未过期（ClockSkew = 0）。
/// </summary>
[Collection("ConstsState")]
public class JwtTokenValidatorTests
{
    static JwtTokenValidatorTests()
    {
        Log4NetTestSetup.EnsureRepository();
        Consts.SymmetricSecurityKey = "jwt-validator-test-symmetric-security-key-0123456789abcdef";
        Consts.SecurityIssuer = "Issuer.JwtValidatorTest";
        Consts.SecurityAudience = "Audience.JwtValidatorTest";
    }

    private static string CreateToken(string issuer = null, DateTime? expires = null, int minutes = 120)
    {
        var time = DateTime.Now;
        var claims = new[]
        {
            new Claim("Name", "tester"),
            new Claim("UserId", "user-1"),
            new Claim("DeviceId", "device-1"),
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        // 过期令牌的 NotBefore 同步前移，避免 JWT 构造器校验 expires > notBefore 抛错
        var notBefore = expires.HasValue ? expires.Value.AddMinutes(-10) : time.AddMinutes(-1);
        var token = new JwtSecurityToken(
            issuer ?? Consts.SecurityIssuer,
            Consts.SecurityAudience,
            claims: claims,
            notBefore: notBefore,
            expires: expires ?? time.AddMinutes(minutes),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public void ValidToken_ReturnsPrincipalWithClaims()
    {
        var principal = JwtTokenValidator.Validate(CreateToken());

        Assert.NotNull(principal);
        Assert.Equal("user-1", principal.FindFirst("UserId")?.Value);
        Assert.Equal("tester", principal.FindFirst("Name")?.Value);
        Assert.Equal("device-1", principal.FindFirst("DeviceId")?.Value);
    }

    [Fact]
    public void TamperedSignature_ReturnsNull()
    {
        var token = CreateToken();
        var tampered = token[..^4] + "AAAA";

        Assert.Null(JwtTokenValidator.Validate(tampered));
    }

    [Fact]
    public void WrongIssuer_ReturnsNull()
    {
        Assert.Null(JwtTokenValidator.Validate(CreateToken(issuer: "Issuer.Other")));
    }

    [Fact]
    public void ExpiredToken_ReturnsNull()
    {
        Assert.Null(JwtTokenValidator.Validate(CreateToken(expires: DateTime.Now.AddMinutes(-10))));
    }

    [Fact]
    public void MalformedOrEmptyToken_ReturnsNull()
    {
        Assert.Null(JwtTokenValidator.Validate("not-a-jwt"));
        // 形似 JWS（两点三段）但 header 非 Base64Url：ReadJwtToken 抛 IDX12729，须按 401 处理而非 500
        Assert.Null(JwtTokenValidator.Validate("garbage.token.here"));
        Assert.Null(JwtTokenValidator.Validate(""));
        Assert.Null(JwtTokenValidator.Validate(null));
    }
}
