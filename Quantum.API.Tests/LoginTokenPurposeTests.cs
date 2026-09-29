using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Quantum.API.Tests.Contract;
using Quantum.Utils;

namespace Quantum.API.Tests;

[Collection("ConstsState")]
public sealed class LoginTokenPurposeTests : IDisposable
{
    private readonly TestLoginSettingScope _setting = new("tester");
    private readonly (string Key, string Issuer, string Audience, long NotBefore) _original =
        (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience, Consts.UserTokenNotBefore);

    public LoginTokenPurposeTests()
    {
        Log4NetTestSetup.EnsureRepository();
        Consts.SetJwtSecrets("login-purpose-test-key-0123456789-abcdef", "Audience.LoginPurpose",
            "Issuer.LoginPurpose", 0);
    }

    public void Dispose()
    {
        Consts.SetJwtSecrets(_original.Key, _original.Audience, _original.Issuer, _original.NotBefore);
        _setting.Dispose();
    }

    private static string Token(string name = "tester", string purpose = "User", long? loginTime = null,
        bool app = false, bool expired = false, string legacyRole = null)
    {
        var claims = new List<Claim> { new("Name", name) };
        if (purpose != null) claims.Add(new Claim("TokenPurpose", purpose));
        if (loginTime.HasValue) claims.Add(new Claim("LoginTime", loginTime.Value.ToString()));
        if (app) claims.Add(new Claim("DeviceId", "device-1"));
        if (legacyRole != null) claims.Add(new Claim("Manager", legacyRole));
        var now = DateTime.UtcNow;
        var signed = new JwtSecurityToken(Consts.SecurityIssuer, Consts.SecurityAudience, claims,
            notBefore: now.AddMinutes(expired ? -20 : -1),
            expires: now.AddMinutes(expired ? -10 : 10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(signed);
    }

    [Fact]
    public void NewWebAndAppTokens_AcceptedWithoutRoleClaim()
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.NotNull(JwtTokenValidator.ValidateLogin(Token(loginTime: issued)));
        Assert.NotNull(JwtTokenValidator.ValidateLogin(Token(loginTime: issued, app: true)));
    }

    [Fact]
    public void LegacyWebAndAppTokens_AcceptedWithinOriginalLifetime_RegardlessOfRoleValue()
    {
        var issued = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
        Assert.NotNull(JwtTokenValidator.ValidateLogin(Token(purpose: null, loginTime: issued, legacyRole: "false")));
        Assert.NotNull(JwtTokenValidator.ValidateLogin(Token(purpose: null, loginTime: issued, app: true)));
        Assert.Null(JwtTokenValidator.ValidateLogin(Token(purpose: null, loginTime: issued, expired: true)));
    }

    [Fact]
    public void OpenWrongAccountAndNameOnlyTokens_CannotBecomeLoginTokens()
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Null(JwtTokenValidator.ValidateLogin(Token(HttpContextExtension.OpenAppTokenName, "Open", issued)));
        Assert.Null(JwtTokenValidator.ValidateLogin(Token("other", "User", issued)));
        Assert.Null(JwtTokenValidator.ValidateLogin(Token(purpose: null)));
        Assert.Null(JwtTokenValidator.ValidateLogin(Token(purpose: "Open", loginTime: issued)));
    }

    [Fact]
    public void PasswordCutoff_RevokesBothNewAndLegacyLoginTokens_ButNotOpenToken()
    {
        var issued = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
        var current = Token(loginTime: issued);
        var legacy = Token(purpose: null, loginTime: issued, app: true);
        var open = Token(HttpContextExtension.OpenAppTokenName, "Open", issued);
        Consts.UserTokenNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        Assert.Null(JwtTokenValidator.ValidateLogin(current));
        Assert.Null(JwtTokenValidator.ValidateLogin(legacy));
        Assert.NotNull(JwtTokenValidator.Validate(open));
    }

    [Fact]
    public void SameSecondCutoff_RejectsPreviouslyIssuedToken()
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var before = Token(loginTime: issued);
        Consts.UserTokenNotBefore = issued + 1;

        Assert.Null(JwtTokenValidator.ValidateLogin(before));
        Assert.NotNull(JwtTokenValidator.ValidateLogin(Token(loginTime: Consts.UserTokenNotBefore)));
    }

    [Fact]
    public void QrExchange_IssuesWebLifetimeWithoutInheritingAppExpiry()
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var appToken = Token(loginTime: issued, app: true);
        var appPrincipal = JwtTokenValidator.ValidateLogin(appToken);
        Assert.NotNull(appPrincipal);

        var webToken = JwtTokenIssuer.Issue(
            appPrincipal.Claims.Select(claim => new Claim(claim.Type, claim.Value)).ToList(),
            DateTime.UtcNow.AddDays(7));
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(webToken);

        Assert.NotNull(JwtTokenValidator.ValidateLogin(webToken));
        Assert.True(parsed.ValidTo > DateTime.UtcNow.AddDays(6));
    }

    [Fact]
    public void LegacyCutoffSetting_IsPreservedWhenWritingNewKey()
    {
        var setting = SystemConfigHelper.GetSetting();
        var staleCopy = setting.Clone();
        setting.ManagerTokenNotBefore = 42;
        setting.UserTokenNotBefore = 0;
        SystemConfigHelper.SetSetting(setting);
        var upgraded = SystemConfigHelper.GetSetting();
        Assert.Equal(42, upgraded.UserTokenNotBefore);
        Assert.Equal(42, upgraded.ManagerTokenNotBefore);

        upgraded.UserTokenNotBefore = 50;
        SystemConfigHelper.SetSetting(upgraded);
        var reloaded = SystemConfigHelper.GetSetting();
        Assert.Equal(50, reloaded.UserTokenNotBefore);
        Assert.Equal(50, reloaded.ManagerTokenNotBefore);

        SystemConfigHelper.SetSetting(staleCopy);
        var afterStaleSave = SystemConfigHelper.GetSetting();
        Assert.Equal(50, afterStaleSave.UserTokenNotBefore);
        Assert.Equal(50, afterStaleSave.ManagerTokenNotBefore);
    }
}
