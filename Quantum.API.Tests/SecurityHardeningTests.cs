using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Quantum.API.Tests.Contract;
using Quantum.Entities;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 公网部署安全加固（2026-09-14 审计修复）回归：
/// 1) 管理令牌吊销闸：改密后（ManagerTokenNotBefore 置位）旧 Manager 令牌失效；
/// 2) CSPRNG 随机串：长度与字符集。
/// （PasswordHasher 已随用户体系移除删除：口令校验统一为 Setting 配置常数时间比较）
/// </summary>
[Collection("ConstsState")]
public class SecurityHardeningTests
{
    static SecurityHardeningTests()
    {
        Log4NetTestSetup.EnsureRepository();
        Consts.SymmetricSecurityKey = "security-hardening-test-symmetric-key-0123456789abcdef";
        Consts.SecurityIssuer = "Issuer.SecurityHardening";
        Consts.SecurityAudience = "Audience.SecurityHardening";
    }

    #region 管理令牌吊销闸

    private static string CreateManagerToken(long loginTimeUnix)
    {
        var claims = new[]
        {
            new Claim("Name", "admin"),
            new Claim("Manager", "true"),
            new Claim("LoginTime", loginTimeUnix.ToString()),
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Consts.SecurityIssuer,
            Consts.SecurityAudience,
            claims: claims,
            notBefore: DateTime.Now.AddMinutes(-1),
            expires: DateTime.Now.AddMinutes(30),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static ClaimsPrincipal ManagerPrincipal(long loginTimeUnix)
    {
        return JwtTokenValidator.Validate(CreateManagerToken(loginTimeUnix));
    }

    [Fact]
    public void ManagerNotBefore_TokenIssuedBeforeCutoffRejected()
    {
        var before = DateTimeOffset.Now.AddHours(-1).ToUnixTimeSeconds();
        var principal = ManagerPrincipal(before);
        Assert.NotNull(principal);

        var oldNotBefore = Consts.ManagerTokenNotBefore;
        try
        {
            Consts.ManagerTokenNotBefore = DateTimeOffset.Now.ToUnixTimeSeconds();
            // 闸门判定 + 端到端验签（Validate 同样拒绝）
            Assert.False(JwtTokenValidator.ValidateManagerNotBefore(principal));
            Assert.Null(JwtTokenValidator.Validate(CreateManagerToken(before)));
        }
        finally
        {
            Consts.ManagerTokenNotBefore = oldNotBefore;
        }
    }

    [Fact]
    public void ManagerNotBefore_TokenIssuedAfterCutoffAccepted()
    {
        var oldNotBefore = Consts.ManagerTokenNotBefore;
        try
        {
            Consts.ManagerTokenNotBefore = DateTimeOffset.Now.AddHours(-2).ToUnixTimeSeconds();
            var after = DateTimeOffset.Now.AddMinutes(-5).ToUnixTimeSeconds();
            var principal = ManagerPrincipal(after);
            Assert.NotNull(principal);
            Assert.True(JwtTokenValidator.ValidateManagerNotBefore(principal));
        }
        finally
        {
            Consts.ManagerTokenNotBefore = oldNotBefore;
        }
    }

    [Fact]
    public void ManagerNotBefore_NonManagerTokenUnaffected()
    {
        var claims = new[] { new Claim("Name", "user-1"), new Claim("UserId", "u1") };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));
        Assert.True(JwtTokenValidator.ValidateManagerNotBefore(principal));
    }

    [Fact]
    public void ManagerNotBefore_ManagerClaimWithoutLoginTimeRejected()
    {
        var claims = new[] { new Claim("Name", "admin"), new Claim("Manager", "true") };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));
        Assert.False(JwtTokenValidator.ValidateManagerNotBefore(principal));
    }

    #endregion

    #region CSPRNG

    [Fact]
    public void RandomStringBuilder_LengthAndCharset()
    {
        var s = RandomStringBuilder.Create(64);
        Assert.Equal(64, s.Length);
        Assert.Matches("^[A-Za-z0-9]+$", s);
        Assert.Equal(string.Empty, RandomStringBuilder.Create(0));
        // 64 字符集下两次碰撞概率可忽略，仅防退化实现（如恒定输出）
        Assert.NotEqual(RandomStringBuilder.Create(32), RandomStringBuilder.Create(32));
    }

    #endregion
}
