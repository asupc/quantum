using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.API.Tests.Contract;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// App 令牌长效化与安全加固（2026-09-19 方案 D1~D6）回归：
/// 1) 刷新窗口可配置默认 365 天（clamp 7~365）；
/// 2) 吊销链：改密全量吊销 / 解绑设备吊销其令牌；
/// 3) 疑似泄露处置：轮换旧令牌重放或跨设备使用 → 全量吊销 + 安全提醒（信封 401 语义见 ExceptionFilterContractTests）；
/// 4) 滚动清理：超过保留期的已吊销/已过期行删除，保留期内已吊销行留存供重放检测。
/// </summary>
[Collection("ConstsState")]
public class AppAuthLongLivedTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalConfigPath;
    private readonly long _originalManagerNotBefore;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;

    static AppAuthLongLivedTests()
    {
        Log4NetTestSetup.EnsureRepository();
        Consts.SymmetricSecurityKey = "app-auth-longlived-test-symmetric-key-0123456789";
        Consts.SecurityIssuer = "Issuer.AppAuthLongLived";
        Consts.SecurityAudience = "Audience.AppAuthLongLived";
    }

    public AppAuthLongLivedTests()
    {
        // 配置指向临时文件，避免触碰真实 appsettings.json（Consts 全局态先存后还）
        _tempDir = Path.Combine(Path.GetTempPath(), "quantum-app-auth-longlived-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalConfigPath = SystemConfigHelper.configPath;
        SystemConfigHelper.configPath = Path.Combine(_tempDir, "appsettings.json");
        SystemConfigHelper.SetSetting(new Setting
        {
            UserName = "admin",
            PassWord = "secret-pass",
            DBType = "SQLite",
            DBAddress = "app-auth-longlived-test.db",
            Port = 5088
        });
        _originalManagerNotBefore = Consts.ManagerTokenNotBefore;

        (_connection, _db) = AppTestDb.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SystemConfigHelper.configPath = _originalConfigPath;
        Consts.ManagerTokenNotBefore = _originalManagerNotBefore;
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private AppAuthService CreateAuthService()
    {
        var pushService = new AppPushService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance),
            new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance), NullLogger<AppPushService>.Instance);
        return new AppAuthService(_db, pushService);
    }

    private static string UniqueIp() => "ip-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>直接落一枚指定形态的刷新令牌行（哈希由原始串现算），绕过登录用于精确构造场景。</summary>
    private async Task<string> SeedRefreshRow(bool revoked = false, string deviceId = "dev-seed",
        DateTime? expiresAt = null, DateTime? createTime = null)
    {
        var raw = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        _db.AppRefreshTokens.Add(new AppRefreshTokenModel
        {
            Id = Guid.NewGuid().ToString().Replace("-", ""),
            TokenHash = Sha256(raw),
            DeviceId = deviceId,
            Revoked = revoked,
            ExpiresAt = expiresAt ?? DateTime.Now.AddDays(30),
            CreateTime = createTime ?? DateTime.Now
        });
        await _db.SaveChangesAsync();
        return raw;
    }

    private static string Sha256(string token)
    {
        return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }

    [Fact]
    public async Task Login_DefaultWindow_Is365Days()
    {
        // D1：未配置 RefreshTokenDays（=0）取默认 365（滑动续期：每次刷新都是全新窗口）
        var auth = CreateAuthService();
        var (_, _, expiresAt) = await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());
        Assert.True(expiresAt > DateTime.Now.AddDays(360), $"默认窗口应约 365 天，实际 {expiresAt}");
    }

    [Fact]
    public async Task Login_ConfiguredWindow_IsClamped()
    {
        var auth = CreateAuthService();

        // 配 5 → clamp 到下限 7
        var setting = SystemConfigHelper.GetSetting();
        setting.RefreshTokenDays = 5;
        SystemConfigHelper.SetSetting(setting);
        var (_, _, low) = await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());
        Assert.True(low > DateTime.Now.AddDays(6) && low < DateTime.Now.AddDays(8), $"clamp 下限应生效为 7 天，实际 {low}");

        // 配 9999 → clamp 到上限 365
        setting = SystemConfigHelper.GetSetting();
        setting.RefreshTokenDays = 9999;
        SystemConfigHelper.SetSetting(setting);
        var (_, _, high) = await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());
        Assert.True(high > DateTime.Now.AddDays(360) && high < DateTime.Now.AddDays(366), $"clamp 上限应生效为 365 天，实际 {high}");
    }

    [Fact]
    public async Task Refresh_ReplayedRotatedToken_RevokesAll_And_PushesSecurityNotice()
    {
        // D3：轮换后的旧令牌再次出现 = 疑似泄露 → 全量吊销 + 安全提醒
        var auth = CreateAuthService();
        var (_, first, _) = await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());
        var (_, second, _) = await auth.RefreshAsync(first, "dev-1"); // 正常轮换一次

        // 重放第一枚（已轮换吊销）
        var ex = await Assert.ThrowsAsync<UnauthorizedBusinessException>(() => auth.RefreshAsync(first, "dev-1"));
        Assert.Contains("强制下线", ex.Message);

        // 全量吊销：轮换产物(second)也被连带
        var rows = await _db.AppRefreshTokens.AsNoTracking().ToListAsync();
        Assert.All(rows, n => Assert.True(n.Revoked));

        // 安全提醒进通知中心（旁路失败不阻断，此处验证落库；登录提醒同类别，按文案区分）
        Assert.Contains(await _db.AppNotifications.AsNoTracking().ToListAsync(),
            n => n.Category == "security" && n.Content.Contains("所有设备退出登录"));

        // 连正常令牌也无法再刷：被踢干净
        await Assert.ThrowsAsync<UnauthorizedBusinessException>(() => auth.RefreshAsync(second, "dev-1"));
    }

    [Fact]
    public async Task Refresh_DeviceMismatch_RevokesAll()
    {
        // D4：绑定了设备的令牌跨设备使用 = 疑似泄露
        var auth = CreateAuthService();
        var (_, token, _) = await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());

        await Assert.ThrowsAsync<UnauthorizedBusinessException>(() => auth.RefreshAsync(token, "dev-other"));

        var rows = await _db.AppRefreshTokens.AsNoTracking().ToListAsync();
        Assert.All(rows, n => Assert.True(n.Revoked));
        Assert.Contains(await _db.AppNotifications.AsNoTracking().ToListAsync(),
            n => n.Category == "security" && n.Content.Contains("所有设备退出登录"));
    }

    [Fact]
    public async Task Refresh_LegacyRow_WithoutDeviceId_IsTolerated()
    {
        // D4 存量兼容：升级前的老令牌行 DeviceId=NULL，不绑定放行，轮换后自然变绑定行
        var auth = CreateAuthService();
        var raw = await SeedRefreshRow(deviceId: null);

        var (access, newToken, _) = await auth.RefreshAsync(raw, "dev-legacy");

        Assert.False(string.IsNullOrEmpty(access));
        var rotated = await _db.AppRefreshTokens.AsNoTracking().SingleAsync(n => n.TokenHash == Sha256(newToken));
        Assert.Equal("dev-legacy", rotated.DeviceId);
        Assert.False(rotated.Revoked);
    }

    [Fact]
    public async Task Refresh_UnknownOrExpiredToken_Is_Rejected_WithoutCascade()
    {
        // 确定性拒绝但不连带：不存在的令牌/自然过期令牌只拒自己，不动其他设备会话
        var auth = CreateAuthService();
        var (_, alive, _) = await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());

        await Assert.ThrowsAsync<UnauthorizedBusinessException>(() => auth.RefreshAsync("no-such-token", "dev-1"));
        var expired = await SeedRefreshRow(expiresAt: DateTime.Now.AddDays(-1));
        await Assert.ThrowsAsync<UnauthorizedBusinessException>(() => auth.RefreshAsync(expired, "dev-seed"));

        var aliveRow = await _db.AppRefreshTokens.AsNoTracking().SingleAsync(n => n.TokenHash == Sha256(alive));
        Assert.False(aliveRow.Revoked);
        Assert.DoesNotContain(await _db.AppNotifications.AsNoTracking().ToListAsync(), n => n.Content.Contains("所有设备退出登录"));
    }

    [Fact]
    public async Task UnbindDevice_Revokes_Its_RefreshTokens_Only()
    {
        // D4：远程下线语义完整（推送停 + 会话死），且只影响该设备；
        // 被解绑设备重试刷新是普通拒绝（不触发疑似泄露连带，其他设备会话不受波及）
        var auth = CreateAuthService();
        var (_, t1, _) = await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());
        var (_, t2, _) = await auth.LoginAsync("admin", "secret-pass", "dev-2", null, null, UniqueIp());

        await auth.UnbindDeviceAsync("dev-1");

        var ex = await Assert.ThrowsAsync<UnauthorizedBusinessException>(() => auth.RefreshAsync(t1, "dev-1"));
        Assert.DoesNotContain("强制下线", ex.Message); // 管理性吊销：普通拒绝文案
        var (_, refreshed, _) = await auth.RefreshAsync(t2, "dev-2"); // 其他设备不受影响
        Assert.False(string.IsNullOrEmpty(refreshed));
    }

    [Fact]
    public async Task UpdatePassword_Revokes_AllRefreshTokens()
    {
        // D2：改密 = access（NotBefore 闸）+ refresh 双路全废，旧 App 无法凭旧 refresh 穿透续签；
        // 改密后的重试是管理性吊销普通拒绝，不触发疑似泄露通知
        var auth = CreateAuthService();
        var (_, token, _) = await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());

        var configService = new SystemConfigService(_db);
        await configService.UpdatePassword(new UpdatePasswordRequest
        {
            OldUserName = "admin",
            OldPassword = "secret-pass",
            NewPassword = "brand-new-pass"
        });

        var ex = await Assert.ThrowsAsync<UnauthorizedBusinessException>(() => auth.RefreshAsync(token, "dev-1"));
        Assert.Contains("重新登录", ex.Message);
        // 新密码可正常登录
        var (_, newToken, _) = await auth.LoginAsync("admin", "brand-new-pass", "dev-1", null, null, UniqueIp());
        Assert.False(string.IsNullOrEmpty(newToken));
    }

    [Fact]
    public async Task IssueRefreshToken_RollingCleanup_RemovesStaleRows_Only()
    {
        // D6：清理只删超过保留期（7 天）的已吊销/已过期行；保留期内已吊销行留存供重放检测
        var auth = CreateAuthService();
        await SeedRefreshRow(revoked: true, createTime: DateTime.Now.AddDays(-8));           // 陈旧已吊销 → 删
        await SeedRefreshRow(revoked: false, createTime: DateTime.Now.AddDays(-8),
            expiresAt: DateTime.Now.AddDays(-8));                                              // 陈旧已过期 → 删
        await SeedRefreshRow(revoked: true, createTime: DateTime.Now.AddDays(-1));            // 新近已吊销 → 留
        await SeedRefreshRow(revoked: false, createTime: DateTime.Now.AddDays(-8),
            expiresAt: DateTime.Now.AddDays(30));                                              // 陈旧但未过期未吊销 → 留

        await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());       // 触发清理+签发

        var rows = await _db.AppRefreshTokens.AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count); // 新近已吊销 + 陈旧有效 + 本次签发
        Assert.DoesNotContain(rows, n => n.Revoked && n.CreateTime < DateTime.Now.AddDays(-7));
        Assert.DoesNotContain(rows, n => n.ExpiresAt < DateTime.Now.AddDays(-7));
    }
}
