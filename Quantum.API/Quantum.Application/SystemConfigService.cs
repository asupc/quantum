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
        // 通道主密钥与 JWT 密钥同级别敏感：永不外发（设置页 Update 是白名单拷贝，本字段不可经页面回写）
        ddd.ChannelMasterKey = "";
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
        long userTokenNotBefore = 0;
        SystemConfigHelper.UpdateSetting(setting =>
        {
            // 与 LoginService 登录口同款常数时间比较。
            var passwordOk = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(request.OldPassword ?? string.Empty),
                System.Text.Encoding.UTF8.GetBytes(setting.PassWord ?? string.Empty));
            if (!passwordOk || request.OldUserName != setting.UserName)
            {
                throw new BusinessException("验证原用户名密码错误！");
            }
            if (!string.IsNullOrEmpty(request.NewUserName))
            {
                if (request.NewUserName == HttpContextExtension.OpenAppTokenName)
                {
                    throw new BusinessException("不能使用保留的外部凭据主体名作为登录账号");
                }
                setting.UserName = request.NewUserName;
            }
            setting.PassWord = request.NewPassword;
            // 同写新旧键，旧版本回退时仍保留吊销下限。
            setting.UserTokenNotBefore = Math.Max(DateTimeOffset.Now.ToUnixTimeSeconds() + 1,
                Math.Max(setting.UserTokenNotBefore, setting.ManagerTokenNotBefore) + 1);
            setting.ManagerTokenNotBefore = setting.UserTokenNotBefore;
            userTokenNotBefore = setting.UserTokenNotBefore;
        });
        Consts.UserTokenNotBefore = userTokenNotBefore;
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
        var requiresRestart = false;
        SystemConfigHelper.UpdateSetting(currentConfig =>
        {
            // 三项在 Startup 只读取一次，变更需重启。
            requiresRestart =
                currentConfig.KnownProxies != setting.KnownProxies
                || currentConfig.AllowedOrigins != setting.AllowedOrigins
                || currentConfig.EnableSwagger != setting.EnableSwagger;

            currentConfig.CommandTimeInterval = setting.CommandTimeInterval;
            currentConfig.MessageQueueInterval = Math.Max(1, setting.MessageQueueInterval);
            currentConfig.ServerPath = setting.ServerPath;
            currentConfig.IntegralProportion = setting.IntegralProportion;
            currentConfig.MessageInterval = setting.MessageInterval;
            currentConfig.LoginNotify = setting.LoginNotify;
            currentConfig.Footer = setting.Footer;
            if (!string.IsNullOrEmpty(setting.AppKey) && setting.AppKey != SecretMask)
            {
                currentConfig.AppKey = setting.AppKey;
            }
            currentConfig.KnownProxies = setting.KnownProxies;
            currentConfig.AllowedOrigins = setting.AllowedOrigins;
            currentConfig.EnableSwagger = setting.EnableSwagger;
        });
        restartRequired = requiresRestart;
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
