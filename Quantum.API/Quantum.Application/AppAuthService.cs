using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Quantum.Entities;
using Quantum.Utils;
using Quantum.Entities.Model;
using Quantum.Data;

namespace Quantum.Application;

/// <summary>
/// App（安卓客户端）认证服务：账号密码换 access token（2 小时）+ refresh token（滑动窗口，默认 365 天，可吊销轮换）。
/// 单管理员体系：凭据即 appsettings 的 Quantum:UserName/PassWord（与 Web 管理端登录同源），
/// 签发的令牌恒带 Manager=true；设备按 DeviceId 维度注册/解绑/推送。
/// 吊销链（长效窗口的安全前提）：改密全量吊销 / 解绑设备吊销其令牌 / 旧令牌重放或跨设备使用按疑似泄露全量吊销并推送安全提醒。
/// </summary>
public class AppAuthService
{
    private readonly IQuantumDbContext _dbContext;
    private readonly AppPushService _pushService;

    public const int AccessTokenMinutes = 120;

    /// <summary>刷新窗口缺省天数（Setting.RefreshTokenDays 未配置/非法时）。</summary>
    public const int DefaultRefreshTokenDays = 365;

    /// <summary>刷新窗口 clamp 下限。</summary>
    public const int MinRefreshTokenDays = 7;

    /// <summary>刷新窗口 clamp 上限（防止「永不过期」的不可逆配置）。</summary>
    public const int MaxRefreshTokenDays = 365;

    /// <summary>已吊销/已过期行保留天数：重放检测需要查到哈希，超过该窗口的行由滚动清理删除。</summary>
    public const int RevokedRowRetentionDays = 7;

    /// <summary>吊销原因：轮换（旧令牌重现按疑似泄露处置）。</summary>
    public const string RevokedReasonRotate = "rotate";

    /// <summary>吊销原因：登出。</summary>
    public const string RevokedReasonLogout = "logout";

    /// <summary>吊销原因：远程解绑设备。</summary>
    public const string RevokedReasonUnbind = "unbind";

    /// <summary>吊销原因：修改密码全量吊销。</summary>
    public const string RevokedReasonPassword = "password";

    /// <summary>吊销原因：疑似泄露全量吊销。</summary>
    public const string RevokedReasonLeak = "leak";

    public AppAuthService(IQuantumDbContext dbContext, AppPushService pushService)
    {
        _dbContext = dbContext;
        _pushService = pushService;
    }

    /// <summary>
    /// 登录：校验管理员凭据（appsettings），注册/更新设备，签发 access + refresh。
    /// </summary>
    public async Task<(string accessToken, string refreshToken, DateTime refreshExpiresAt)> LoginAsync(
        string userName, string password, string deviceId, string deviceName, string platform, string ip)
    {
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
        {
            throw new BusinessException("用户名密码不能为空！");
        }
        if (string.IsNullOrEmpty(deviceId))
        {
            throw new BusinessException("缺少设备标识 deviceId！");
        }

        // 登录限流（与 Web 管理登录同款：按 IP 线性退避，失败越多等待越久），防公网无限爆破
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
            Encoding.UTF8.GetBytes(password ?? string.Empty),
            Encoding.UTF8.GetBytes(config.PassWord ?? string.Empty));
        if (string.IsNullOrEmpty(config.PassWord) || !passwordOk || userName != config.UserName)
        {
            // 失败按「IP|账号」记账：攻击者用自己账号登录成功只清自己的记录，清不掉对他人账号的爆破退避
            MemoryObjectCache.AddLoginFailed(ip, userName);
            throw new BusinessException("用户名密码错误！");
        }
        MemoryObjectCache.ResetLoginLimit(ip, userName);

        await RegisterDeviceAsync(deviceId, deviceName, platform);

        var accessToken = IssueAccessToken(config.UserName, deviceId);
        var refreshToken = await IssueRefreshTokenAsync(deviceId);

        // 安全提醒进通知中心（在线 WS 直推/离线厂商推送）。
        // 令牌已签发，推送属旁路：通知落库/推送失败不得让登录整体失败（否则用户收到 500，令牌其实已经生效）
        try
        {
            var deviceLabel = string.IsNullOrEmpty(deviceName) ? deviceId : $"{deviceName}（{platform ?? "android"}）";
            await _pushService.SendNotificationAsync("登录提醒",
                $"您的账号于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} 在设备 {deviceLabel} 登录。如非本人操作，请及时修改密码。",
                "security");
        }
        catch (Exception e)
        {
            LogServiceHelper.Warn("App 登录提醒发送失败（不影响登录）", $"deviceId={deviceId}，异常：{e.Message}", "AppAuthService");
        }

        return (accessToken, refreshToken.refreshToken, refreshToken.expiresAt);
    }

    /// <summary>
    /// 刷新：校验 refresh token → 轮换旧令牌 → 签发新对。
    /// 确定性拒绝（无效/过期/重放/跨设备）抛 <see cref="UnauthorizedBusinessException"/>（信封 Code=401，
    /// 客户端据此与临时故障区分）；重放与跨设备按疑似泄露处置：全量吊销 + 安全提醒。
    /// </summary>
    public async Task<(string accessToken, string refreshToken, DateTime refreshExpiresAt)> RefreshAsync(string refreshToken, string deviceId)
    {
        var hash = HashToken(refreshToken);
        var stored = await _dbContext.AppRefreshTokens.AsNoTracking().SingleOrDefaultAsync(n => n.TokenHash == hash);
        if (stored == null || stored.ExpiresAt < DateTime.Now)
        {
            throw new UnauthorizedBusinessException("刷新令牌无效或已过期，请重新登录！");
        }
        if (!string.IsNullOrEmpty(stored.DeviceId) && stored.DeviceId != deviceId)
        {
            await HandleSuspectedLeakAsync("检测到刷新令牌跨设备使用");
            throw new UnauthorizedBusinessException("检测到令牌异常使用，已强制下线，请重新登录！");
        }

        // 轮换走数据库级 CAS（条件更新）：并发用同一令牌刷新时只有一个能成功，
        // 失败方按吊销原因分流——rotate（轮换旧令牌重现）= 疑似泄露全量吊销；
        // logout/unbind/password 等管理性吊销的令牌被客户端重试属正常时序，普通拒绝即可
        var rotated = await _dbContext.AppRefreshTokens
            .Where(n => n.Id == stored.Id && !n.Revoked)
            .ExecuteUpdateAsync(n => n
                .SetProperty(t => t.Revoked, true)
                .SetProperty(t => t.RevokedReason, RevokedReasonRotate));
        if (rotated == 0)
        {
            var fresh = await _dbContext.AppRefreshTokens.AsNoTracking().SingleOrDefaultAsync(n => n.Id == stored.Id);
            if (fresh?.RevokedReason == RevokedReasonRotate)
            {
                await HandleSuspectedLeakAsync("检测到已作废的刷新令牌被重复使用");
                throw new UnauthorizedBusinessException("检测到令牌异常使用，已强制下线，请重新登录！");
            }
            throw new UnauthorizedBusinessException("刷新令牌无效或已过期，请重新登录！");
        }

        await RegisterDeviceAsync(deviceId, null, null);
        var accessToken = IssueAccessToken(SystemConfigHelper.GetSetting()?.UserName ?? string.Empty, deviceId);
        var newRefresh = await IssueRefreshTokenAsync(deviceId);
        return (accessToken, newRefresh.refreshToken, newRefresh.expiresAt);
    }

    /// <summary>
    /// 登出：吊销指定刷新令牌（AccessToken 自然过期即可）。
    /// </summary>
    public async Task<bool> LogoutAsync(string refreshToken)
    {
        var hash = HashToken(refreshToken);
        var stored = await _dbContext.AppRefreshTokens.SingleOrDefaultAsync(n => n.TokenHash == hash);
        if (stored != null && !stored.Revoked)
        {
            stored.Revoked = true;
            stored.RevokedReason = RevokedReasonLogout;
            await _dbContext.SaveChangesAsync();
        }
        return true;
    }

    /// <summary>
    /// 多端设备列表（最近活跃倒序）。
    /// </summary>
    public async Task<List<AppDeviceModel>> GetDevicesAsync()
    {
        return await _dbContext.AppDevices.AsNoTracking()
            .OrderByDescending(n => n.LastSeenAt)
            .ToListAsync();
    }

    /// <summary>
    /// 远程下线（解绑）设备：Enabled=false 后不再参与厂商推送，并吊销该设备全部刷新令牌
    /// （「远程下线」语义完整：推送停 + 会话死，该设备下次刷新收 401 跳登录）；后续登录会自动重新绑定。
    /// </summary>
    public async Task<bool> UnbindDeviceAsync(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            throw new BusinessException("缺少设备标识 deviceId！");
        }
        await _dbContext.AppDevices
            .Where(n => n.DeviceId == deviceId)
            .ExecuteUpdateAsync(n => n.SetProperty(t => t.Enabled, false));
        await _dbContext.AppRefreshTokens
            .Where(n => n.DeviceId == deviceId && !n.Revoked)
            .ExecuteUpdateAsync(n => n
                .SetProperty(t => t.Revoked, true)
                .SetProperty(t => t.RevokedReason, RevokedReasonUnbind));
        return true;
    }

    private async Task RegisterDeviceAsync(string deviceId, string deviceName, string platform)
    {
        var device = await _dbContext.AppDevices.SingleOrDefaultAsync(n => n.DeviceId == deviceId);
        if (device == null)
        {
            _dbContext.AppDevices.Add(new AppDeviceModel
            {
                Id = Guid.NewGuid().ToString().Replace("-", ""),
                DeviceId = deviceId,
                DeviceName = deviceName,
                Platform = platform,
                LastSeenAt = DateTime.Now
            });
        }
        else
        {
            device.LastSeenAt = DateTime.Now;
            if (!string.IsNullOrEmpty(deviceName))
            {
                device.DeviceName = deviceName;
            }
            if (!string.IsNullOrEmpty(platform))
            {
                device.Platform = platform;
            }
            device.Enabled = true;
        }
        await _dbContext.SaveChangesAsync();
    }

    private string IssueAccessToken(string userName, string deviceId)
    {
        var time = DateTime.Now;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Nbf, $"{new DateTimeOffset(time).ToUnixTimeSeconds()}"),
            new(JwtRegisteredClaimNames.Exp, $"{new DateTimeOffset(time.AddMinutes(AccessTokenMinutes)).ToUnixTimeSeconds()}"),
            new("Name", userName ?? string.Empty),
            new("DeviceId", deviceId ?? string.Empty),
            new("LoginTime", time.ToUnix().ToString()),
            // 单管理员：App 令牌恒带正向 Manager claim（[ManagerOnly] 唯一放行依据；
            // Open AppKey 令牌/任务临时令牌无该 claim，天然被 [ManagerOnly] 拒绝）
            new("Manager", "true"),
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Consts.SecurityIssuer,
            Consts.SecurityAudience,
            claims: claims,
            expires: time.AddMinutes(AccessTokenMinutes),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<(string refreshToken, DateTime expiresAt)> IssueRefreshTokenAsync(string deviceId)
    {
        // 滚动清理：已吊销/已过期超过保留期的行不再需要（重放检测窗口即保留期），防轮换积累无限增长
        await _dbContext.AppRefreshTokens
            .Where(n => (n.Revoked && n.CreateTime < DateTime.Now.AddDays(-RevokedRowRetentionDays))
                     || n.ExpiresAt < DateTime.Now.AddDays(-RevokedRowRetentionDays))
            .ExecuteDeleteAsync();

        var raw = RandomStringBuilder.Create(64);
        var model = new AppRefreshTokenModel
        {
            Id = Guid.NewGuid().ToString().Replace("-", ""),
            TokenHash = HashToken(raw),
            DeviceId = deviceId ?? string.Empty,
            ExpiresAt = DateTime.Now.AddDays(ResolveRefreshDays())
        };
        _dbContext.AppRefreshTokens.Add(model);
        await _dbContext.SaveChangesAsync();
        return (raw, model.ExpiresAt);
    }

    /// <summary>
    /// 疑似泄露处置（单管理员体系取最简最狠）：全量吊销刷新令牌 + 安全提醒进通知中心。
    /// 推送属旁路：通知落库/推送失败不得让处置本身失败。
    /// </summary>
    private async Task HandleSuspectedLeakAsync(string reason)
    {
        await _dbContext.AppRefreshTokens
            .Where(n => !n.Revoked)
            .ExecuteUpdateAsync(n => n
                .SetProperty(t => t.Revoked, true)
                .SetProperty(t => t.RevokedReason, RevokedReasonLeak));
        try
        {
            await _pushService.SendNotificationAsync("安全提醒",
                $"{reason}，已强制所有设备退出登录。如非本人操作，请立即修改密码。", "security");
        }
        catch (Exception e)
        {
            LogServiceHelper.Warn("App 疑似泄露安全提醒发送失败（不影响处置）", $"异常：{e.Message}", "AppAuthService");
        }
    }

    /// <summary>
    /// 刷新窗口天数：Setting.RefreshTokenDays 未配置/非法取默认 365，配置值 clamp 到 [7, 365]。
    /// </summary>
    private static int ResolveRefreshDays()
    {
        var days = SystemConfigHelper.GetSetting()?.RefreshTokenDays ?? 0;
        if (days <= 0)
        {
            return DefaultRefreshTokenDays;
        }
        return Math.Clamp(days, MinRefreshTokenDays, MaxRefreshTokenDays);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }
}
