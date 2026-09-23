using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Quantum.Utils;

/// <summary>
/// 令牌签发器（扫码登录 App→Web 换发用）：与 LoginService/AppAuthService 同一签名密钥/
/// Issuer/Audience，claims 由调用方组装（含正向 Manager claim 时即为管理端等价令牌）。
/// </summary>
public static class JwtTokenIssuer
{
    public static string Issue(List<Claim> claims, DateTime expires)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Consts.SecurityIssuer,
            Consts.SecurityAudience,
            claims: claims,
            expires: expires,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
