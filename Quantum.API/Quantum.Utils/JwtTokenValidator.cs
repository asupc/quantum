using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using log4net;
using Microsoft.IdentityModel.Tokens;

namespace Quantum.Utils;

/// <summary>
/// JWT 共享验签器：管理端属性过滤器（CustomAuthorizationFilter）、标准 JwtBearer 中间件与
/// App WS 握手（/ws/app）共用同一套校验规则（签名密钥/Issuer/Audience 取自系统配置）。
/// </summary>
public static class JwtTokenValidator
{
    private static readonly ILog Log = LogManager.GetLogger("NETCoreRepository", typeof(JwtTokenValidator));

    /// <summary>
    /// 验签并返回 ClaimsPrincipal（含全部声明）；失败返回 null。
    /// </summary>
    public static ClaimsPrincipal Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }
        var handler = new JwtSecurityTokenHandler();
        // 格式坏的 token（含形似 JWS 但 header 非 Base64Url 的串）不能让解析异常穿透到全局错误处理，
        // 必须按验签失败返回 401（ReadJwtToken 对带点畸形串会抛 IDX12729，历史上曾穿透成 500）
        if (!handler.CanReadToken(token))
        {
            Log.Warn("Token格式无效");
            return null;
        }
        try
        {
            var securityToken = handler.ReadJwtToken(token);
            if (securityToken.Issuer != Consts.SecurityIssuer || securityToken.Audiences.FirstOrDefault() != Consts.SecurityAudience)
            {
                Log.Warn("Issuer和Audience验证失败");
                return null;
            }
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey));
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero // 默认允许的小偏差时间
            };
            var principal = handler.ValidateToken(token, validationParameters, out SecurityToken validatedToken);
            if (!ValidateUserNotBefore(principal))
            {
                Log.Warn("登录令牌已因改密作废");
                return null;
            }
            return principal;
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            Log.Warn("签名无效");
            return null;
        }
        catch (Exception)
        {
            Log.Warn("Token无效");
            return null;
        }
    }

    /// <summary>
    /// 登录令牌吊销闸：Open 等外部凭据不受改密影响。
    /// </summary>
    public static bool ValidateUserNotBefore(ClaimsPrincipal principal)
    {
        if (principal == null || principal.FindFirst("Name")?.Value == HttpContextExtension.OpenAppTokenName)
        {
            return true;
        }
        var purpose = principal.FindFirst("TokenPurpose")?.Value;
        if (purpose != null && purpose != "User")
        {
            return true;
        }
        if (long.TryParse(principal.FindFirst("LoginTime")?.Value, out var seconds))
        {
            return seconds >= Consts.UserTokenNotBefore;
        }
        return false;
    }

    /// <summary>旧 Web/App 令牌只按原主体、签发时间和有效期兼容，不使用角色声明。</summary>
    public static bool IsLoginPrincipal(ClaimsPrincipal principal)
    {
        if (principal == null)
        {
            return false;
        }
        var name = principal.FindFirst("Name")?.Value;
        var purpose = principal.FindFirst("TokenPurpose")?.Value;
        if (string.IsNullOrWhiteSpace(name) || name == HttpContextExtension.OpenAppTokenName
            || (purpose != null && purpose != "User"))
        {
            return false;
        }
        var configuredName = SystemConfigHelper.GetSetting()?.UserName;
        if (string.IsNullOrWhiteSpace(configuredName) || configuredName == HttpContextExtension.OpenAppTokenName
            || !string.Equals(name, configuredName, StringComparison.Ordinal))
        {
            return false;
        }
        return long.TryParse(principal.FindFirst("LoginTime")?.Value, out var seconds)
            && seconds > 0
            && seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60
            && seconds >= Consts.UserTokenNotBefore;
    }

    public static ClaimsPrincipal ValidateLogin(string token)
    {
        var principal = Validate(token);
        return IsLoginPrincipal(principal) ? principal : null;
    }

}
