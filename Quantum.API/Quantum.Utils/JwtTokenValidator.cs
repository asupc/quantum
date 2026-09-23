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
            if (!ValidateManagerNotBefore(principal))
            {
                Log.Warn("管理令牌已因改密作废");
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
    /// 管理令牌吊销闸（属性过滤器与 JwtBearer OnTokenValidated 共用）：
    /// Manager=true 的令牌若 LoginTime 早于 Consts.ManagerTokenNotBefore（改密时置位）即拒绝。
    /// </summary>
    public static bool ValidateManagerNotBefore(ClaimsPrincipal principal)
    {
        if (!string.Equals(principal?.FindFirst("Manager")?.Value, "true", StringComparison.Ordinal))
        {
            return true;
        }
        var loginTime = principal.FindFirst("LoginTime")?.Value;
        if (long.TryParse(loginTime, out var seconds))
        {
            return seconds >= Consts.ManagerTokenNotBefore;
        }
        // 无 LoginTime 声明的管理令牌（历史存量格式）按不合规拒绝
        return false;
    }
}
