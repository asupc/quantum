using Microsoft.IdentityModel.Tokens;
using Quantum.Utils;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Quantum.Application;

public class OpenAuthService
{
    /// <summary>
    /// 通过AppKey获取授权Token，该Token有效期为10分钟
    /// </summary>
    /// <param name="AppKey"></param>
    /// <param name="ip">客户端IP</param>
    /// <returns></returns>
    public string Auth(string AppKey, string ip)
    {
        string result = "";

        MemoryObjectCache.PruneStaleLimits();
        if (MemoryObjectCache.IpLimit.ContainsKey(ip))
        {
            if (MemoryObjectCache.IpLimit[ip].AddSeconds(3 * MemoryObjectCache.GetLoginFailed(ip)) > System.DateTime.Now)
            {
                throw new BusinessException("请求频繁，请稍后重试！");
            }
            MemoryObjectCache.IpLimit[ip] = DateTime.Now;
        }
        else
        {
            MemoryObjectCache.IpLimit.TryAdd(ip, DateTime.Now);
        }
        var installConfig = SystemConfigHelper.GetSetting();
        // AppKey 通道无用户名维度，统一以专用记账名登记（与其他账号的失败互不清除）
        if (string.IsNullOrEmpty(AppKey))
        {
            MemoryObjectCache.AddLoginFailed(ip, MemoryObjectCache.AppKeyAuthAccount);
            throw new BusinessException("未知异常");
        }
        else if (string.IsNullOrEmpty(installConfig.AppKey))
        {
            MemoryObjectCache.AddLoginFailed(ip, MemoryObjectCache.AppKeyAuthAccount);
            throw new BusinessException("未知异常");
        }
        else if (!FixedTimeEquals(AppKey, installConfig.AppKey))
        {
            MemoryObjectCache.AddLoginFailed(ip, MemoryObjectCache.AppKeyAuthAccount);
            throw new BusinessException("验证失败");
        }
        else if (AppKey == installConfig.AppKey)
        {
            var time = DateTime.Now;
            var appTokenName = "______App-Key-Auth";
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Nbf,$"{new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds()}") ,
                new Claim (JwtRegisteredClaimNames.Exp,$"{new DateTimeOffset(DateTime.Now.AddMinutes(10)).ToUnixTimeSeconds()}"),
                new Claim("Name", appTokenName),
                new Claim("IP", ip),
                new Claim("LoginTime", time.ToUnix().ToString()),
            };
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                Consts.SecurityIssuer,
                Consts.SecurityAudience,
                claims: claims,
                expires: DateTime.Now.AddMinutes(10),
                signingCredentials: creds);
            result = new JwtSecurityTokenHandler().WriteToken(token);
            MemoryObjectCache.ResetLoginLimit(ip, MemoryObjectCache.AppKeyAuthAccount);
        }
        return result;
    }

    /// <summary>
    /// 常数时间比较（防时序侧信道逐字节探测 AppKey）
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a ?? string.Empty);
        var bytesB = Encoding.UTF8.GetBytes(b ?? string.Empty);
        if (bytesA.Length != bytesB.Length)
        {
            // 长度差异也走完一次比较，避免长度本身成为旁路
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(bytesA, bytesA);
            return false;
        }
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
