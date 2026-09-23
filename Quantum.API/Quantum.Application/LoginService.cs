using Microsoft.IdentityModel.Tokens;
using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Quantum.Application;

public class LoginService
{
    /// <summary>
    /// 用户登录（Web 管理端：凭据即 appsettings 的 Quantum:UserName/PassWord）
    /// </summary>
    public string Login(Setting login, string ip)
    {
        MemoryObjectCache.PruneStaleLimits();
        if (MemoryObjectCache.IpLimit.ContainsKey(ip))
        {
            if (MemoryObjectCache.IpLimit[ip].AddSeconds(10 * MemoryObjectCache.GetLoginFailed(ip)) > DateTime.Now)
            {
                throw new BusinessException("请求频繁，请稍后重试！");
            }
            MemoryObjectCache.IpLimit[ip] = DateTime.Now;
        }
        else
        {
            MemoryObjectCache.IpLimit.TryAdd(ip, DateTime.Now);
        }

        var config = SystemConfigHelper.GetSetting();
        if (config == null)
        {
            throw new BusinessException("用户数据库未初始化，请初始化后重启容器登录！");
        }

        // 常数时间比较口令（防时序旁路）；用户名等值比较
        var passwordOk = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(login?.PassWord ?? string.Empty),
            Encoding.UTF8.GetBytes(config.PassWord ?? string.Empty));
        if (!string.IsNullOrEmpty(config.PassWord) && passwordOk && login.UserName == config.UserName)
        {            var time = DateTime.Now;

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Nbf, $"{new DateTimeOffset(DateTime.Now).ToUnixTimeSeconds()}"),
                new Claim(JwtRegisteredClaimNames.Exp, $"{new DateTimeOffset(DateTime.Now.AddMinutes(60 * 24 * 7)).ToUnixTimeSeconds()}"),
                new Claim("Name", config.UserName),
                new Claim("IP", ip),
                // 正向管理员 claim：[ManagerOnly] 端点唯一放行依据（单管理员体系下 App 令牌恒带同款 claim）
                new Claim("Manager", "true"),
                new Claim("LoginTime", time.ToUnix().ToString()),
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                Consts.SecurityIssuer,
                Consts.SecurityAudience,
                claims: claims,
                expires: DateTime.Now.AddDays(7),
                signingCredentials: creds);

            var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

            if (config.LoginNotify)
            {
                var loginNotifyMessage = @$"量子用户登录提醒。
登录时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}
登录IP：{ip}";
                // App 站内通知（在线 WS 广播/离线厂商推送，通知中心兜底）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await AppPushDispatcher.SendNotificationAsync("登录提醒", loginNotifyMessage, "security");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("App 登录通知失败：" + e.Message);
                    }
                });
            }

            LogServiceHelper.Logs.Enqueue(new LogModel
            {
                CreateTime = DateTime.Now,
                LogType = LogType.操作日志,
                Operator = login.UserName,
                Remark = $"登录成功，登录IP：{ip}",
                Success = true,
                Title = "用户登录"
            });

            MemoryObjectCache.ResetLoginLimit(ip, config.UserName);

            return tokenString;
        }
        else
        {
            // 失败按「IP|账号」记账：其他账号在该 IP 登录成功不会清掉对本管理口令的爆破退避
            MemoryObjectCache.AddLoginFailed(ip, login?.UserName);
            throw new BusinessException("登录失败，用户名密码错误！");
        }
    }
}
