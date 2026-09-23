using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Utils;

namespace Quantum.Application;

public class SystemConfigService
{
    readonly IQuantumDbContext QuantumDbContext;

    /// <summary>
    /// 高敏凭据（Open AppKey）的脱敏掩码（GET 返回该值；PUT 收到空串或该值均不回写，避免掩码覆盖真实值）
    /// </summary>
    public const string SecretMask = "******";

    public SystemConfigService(IQuantumDbContext quantumDbContext)
    {
        QuantumDbContext = quantumDbContext;
    }

    /// <summary>
    /// 获取系统设置（登录后才可访问；敏感字段统一脱敏返回）
    /// </summary>
    /// <returns></returns>
    public Setting GetSetting()
    {
        var ddd = SystemConfigHelper.GetSetting();

        ddd.DBAddress = "";
        ddd.DBType = "";
        ddd.UserName = "";
        ddd.PassWord = "";
        ddd.SecurityIssuer = "";
        ddd.SecurityAudience = "";
        ddd.SymmetricSecurityKey = "";
        // Open AppKey 属高敏凭据：已配置时以掩码返回（未配置保持空串）。
        // 此前 AppKey 直接置空返回，设置页一次保存就会被 PUT 清空——改为掩码约定。
        if (!string.IsNullOrEmpty(ddd.AppKey))
        {
            ddd.AppKey = SecretMask;
        }
        return ddd;
    }

    /// <summary>
    /// 修改登录密码
    /// </summary>
    public async Task<bool> UpdatePassword(UpdatePasswordRequest request)
    {
        if (string.IsNullOrEmpty(request.NewPassword))
        {
            throw new BusinessException("新密码不能为空！");
        }
        var setting = SystemConfigHelper.GetSetting();
        var pwd = setting.PassWord;
        // 与 LoginService 登录口同款常数时间比较（审计「低危杂项」的覆盖补齐：改密口旧口令不再普通 != 比较）
        var passwordOk = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(request.OldPassword ?? string.Empty),
            System.Text.Encoding.UTF8.GetBytes(pwd ?? string.Empty));
        if (!passwordOk || request.OldUserName != setting.UserName)
        {
            throw new BusinessException("验证原用户名密码错误！");
        }
        if (!string.IsNullOrEmpty(request.NewUserName))
        {
            setting.UserName = request.NewUserName;
        }
        setting.PassWord = request.NewPassword;
        // 改密即吊销：早于当前时刻签发的 Manager 令牌（Web 7 天/App 2 小时）全部失效
        setting.ManagerTokenNotBefore = DateTimeOffset.Now.ToUnixTimeSeconds();
        Consts.ManagerTokenNotBefore = setting.ManagerTokenNotBefore;
        SystemConfigHelper.SetSetting(setting);
        // App 刷新令牌同批吊销（access 由 NotBefore 闸作废，refresh 不吊销的话旧 App 仍可凭它续签，穿透改密语义）
        await QuantumDbContext.AppRefreshTokens
            .Where(n => !n.Revoked)
            .ExecuteUpdateAsync(n => n
                .SetProperty(t => t.Revoked, true)
                .SetProperty(t => t.RevokedReason, AppAuthService.RevokedReasonPassword));
        return true;
    }

    /// <summary>
    /// 修改系统设置
    /// </summary>
    public bool Update(Setting setting)
    {
        return Update(setting, out _);
    }

    /// <summary>
    /// 修改系统设置（§5-8：附带检测「启动期三件套」是否变更）
    /// </summary>
    /// <param name="setting">待保存设置</param>
    /// <param name="restartRequired">KnownProxies/AllowedOrigins/EnableSwagger 任一发生变更时为 true。
    /// 这三项在 Startup 只读取一次（ForwardedHeaders/CORS/Swagger），运行期保存不会热生效，须重启服务；
    /// 上层据此回传「需重启生效」提示（本方法不改动热加载机制）。</param>
    public bool Update(Setting setting, out bool restartRequired)
    {
        var currentConfig = SystemConfigHelper.GetSetting();

        // 写入前用旧值对比入参：仅这三项属启动期配置，变更才需重启（其余字段即时生效）
        restartRequired =
            currentConfig.KnownProxies != setting.KnownProxies
            || currentConfig.AllowedOrigins != setting.AllowedOrigins
            || currentConfig.EnableSwagger != setting.EnableSwagger;

        currentConfig.BlackQQ = setting.BlackQQ;
        currentConfig.CommandTimeInterval = setting.CommandTimeInterval;
        currentConfig.MessageQueueInterval = Math.Max(1, setting.MessageQueueInterval);
        currentConfig.ServerPath = setting.ServerPath;
        currentConfig.IntegralProportion = setting.IntegralProportion;
        currentConfig.MessageInterval = setting.MessageInterval;
        currentConfig.LoginNotify = setting.LoginNotify;
        currentConfig.Footer = setting.Footer;
        // 高敏键（Open AppKey）：空串或脱敏掩码不回写（「保存时空字段不覆盖」约定，防止掩码覆盖真实值）。
        // AppKey 例外：未配置过（当前为空）时允许写入新值，否则永远无法首次设置。
        if (!string.IsNullOrEmpty(setting.AppKey) && setting.AppKey != SecretMask)
        {
            currentConfig.AppKey = setting.AppKey;
        }
        // 安全开关三件套（非敏感，随设置页一起管理）：可信代理/跨域白名单/Swagger 暴露
        currentConfig.KnownProxies = setting.KnownProxies;
        currentConfig.AllowedOrigins = setting.AllowedOrigins;
        currentConfig.EnableSwagger = setting.EnableSwagger;
        SystemConfigHelper.SetSetting(currentConfig);
        return true;
    }

    /// <summary>
    /// 获取自定义页脚信息
    /// </summary>
    /// <returns></returns>
    public string GetFooter()
    {
        var setting = SystemConfigHelper.GetSetting();
        return setting?.Footer;
    }

    /// <summary>
    /// 初始化系统
    /// </summary>
    /// <param name="setting"></param>
    /// <returns></returns>
    public bool Install(Setting setting)
    {
        if (SystemConfigHelper.GetSetting() != null)
        {
            throw new BusinessException("系统已经初始化过了，如需重新初始化请删除 appsettings.json 中的 Quantum 配置节");
        }

        if (string.IsNullOrEmpty(setting.PassWord) || string.IsNullOrEmpty(setting.UserName))
        {
            throw new BusinessException("用户名密码不能为空！");
        }

        SystemConfigHelper.SetSetting(setting);
        return true;
    }

    /// <summary>
    /// 收缩数据库（SQLite VACUUM；MySQL 优化高写入表）
    /// </summary>
    public async Task<bool> DatabaseShrink()
    {
        if (SystemConfigHelper.GetSetting().DBType.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
        {
            await QuantumDbContext.Database.ExecuteSqlRawAsync("VACUUM");
        }
        else
        {
            // t_message_record 已随 CommunicationRemoval 迁移删表，OPTIMIZE 清单须与实际表结构同步
            await QuantumDbContext.Database.ExecuteSqlRawAsync("OPTIMIZE TABLE t_log, t_custom_data");
        }
        return true;
    }
}
