using System.IdentityModel.Tokens.Jwt;
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
/// 用户体系移除（2026-09-16）回归：
/// 1) App 登录凭据 = appsettings 管理员账号（与 Web 同源），签发恒带 Manager claim，设备按 DeviceId；
/// 2) 环境变量扁平化：单类型、按名称+值去重、全量注入语义的服务侧前提；
/// 3) 通知偏好全局单行。
/// </summary>
[Collection("ConstsState")]
public class UserSystemRemovalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalConfigPath;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;

    static UserSystemRemovalTests()
    {
        Log4NetTestSetup.EnsureRepository();
        Consts.SymmetricSecurityKey = "user-system-removal-test-symmetric-key-0123456789";
        Consts.SecurityIssuer = "Issuer.UserSystemRemoval";
        Consts.SecurityAudience = "Audience.UserSystemRemoval";
    }

    public UserSystemRemovalTests()
    {
        // 配置指向临时文件，避免触碰真实 appsettings.json（Consts 全局态先存后还）
        _tempDir = Path.Combine(Path.GetTempPath(), "quantum-user-removal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalConfigPath = SystemConfigHelper.configPath;
        SystemConfigHelper.configPath = Path.Combine(_tempDir, "appsettings.json");
        SystemConfigHelper.SetSetting(new Setting
        {
            UserName = "admin",
            PassWord = "secret-pass",
            DBType = "SQLite",
            DBAddress = "user-removal-test.db",
            Port = 5088
        });

        (_connection, _db) = AppTestDb.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SystemConfigHelper.configPath = _originalConfigPath;
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 临时目录清理失败不影响测试结论 */ }
        try
        {
            // 文件库与缓存清理（EnvService 用例的临时库在测试 bin 的 db/ 下；清池后再删避免池句柄占用）
            var dbFile = Path.GetFullPath(Path.Combine("db", "user-removal-test.db"));
            if (File.Exists(dbFile))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                File.Delete(dbFile);
            }
        }
        catch { /* 清理失败不影响测试结论 */ }
        CacheManager.Refresh<EnvModel>();
    }

    private AppAuthService CreateAuthService()
    {
        var pushService = new AppPushService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance),
            new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance), NullLogger<AppPushService>.Instance);
        return new AppAuthService(_db, pushService);
    }

    private static string UniqueIp() => "ip-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task AppLogin_WithSettingCredentials_IssuesManagerToken_AndRegistersDevice()
    {
        var auth = CreateAuthService();

        var (accessToken, refreshToken, expiresAt) = await auth.LoginAsync("admin", "secret-pass", "dev-1", "Pixel 8", "android", UniqueIp());

        // access token：Name/DeviceId/Manager=true（无 UserId claim）
        var principal = JwtTokenValidator.Validate(accessToken);
        Assert.NotNull(principal);
        Assert.Equal("admin", principal.FindFirst("Name")?.Value);
        Assert.Equal("dev-1", principal.FindFirst("DeviceId")?.Value);
        Assert.Equal("true", principal.FindFirst("Manager")?.Value);
        Assert.Null(principal.FindFirst("UserId"));

        // refresh token 落库 30 天；设备行按 DeviceId 注册
        Assert.True(expiresAt > DateTime.Now.AddDays(29));
        Assert.Single(await _db.AppRefreshTokens.ToListAsync());
        var device = await _db.AppDevices.AsNoTracking().SingleAsync(n => n.DeviceId == "dev-1");
        Assert.Equal("Pixel 8", device.DeviceName);
        Assert.True(device.Enabled);
    }

    [Fact]
    public async Task AppLogin_WrongUserNameOrPassword_Throws()
    {
        var auth = CreateAuthService();
        var ip = UniqueIp();

        await Assert.ThrowsAsync<BusinessException>(() => auth.LoginAsync("nope", "secret-pass", "dev-1", null, null, ip));
        await Assert.ThrowsAsync<BusinessException>(() => auth.LoginAsync("admin", "wrong-pass", "dev-1", null, null, ip));

        // 无任何设备/令牌落库
        Assert.Empty(await _db.AppDevices.ToListAsync());
        Assert.Empty(await _db.AppRefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task AppLogin_MissingDeviceId_Throws()
    {
        var auth = CreateAuthService();
        await Assert.ThrowsAsync<BusinessException>(() => auth.LoginAsync("admin", "secret-pass", "", null, null, UniqueIp()));
    }

    [Fact]
    public async Task AppRefresh_RotatesToken_AndOldTokenIsRevoked()
    {
        var auth = CreateAuthService();
        var (_, refreshToken, _) = await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());

        var (newAccess, newRefresh, _) = await auth.RefreshAsync(refreshToken, "dev-1");

        Assert.False(string.IsNullOrEmpty(newAccess));
        Assert.NotEqual(refreshToken, newRefresh);
        // 轮换：旧行吊销、新行生效（吊销=置位，不删行）
        var stored = await _db.AppRefreshTokens.AsNoTracking().ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, n => n.TokenHash == Sha256(refreshToken) && n.Revoked);
        Assert.Contains(stored, n => n.TokenHash == Sha256(newRefresh) && !n.Revoked);
    }

    private static string Sha256(string token)
    {
        return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }

    [Fact]
    public async Task DeviceApi_IsDeviceIdKeyed()
    {
        var auth = CreateAuthService();
        await auth.LoginAsync("admin", "secret-pass", "dev-1", null, null, UniqueIp());

        var devices = await auth.GetDevicesAsync();
        Assert.Single(devices);
        Assert.Equal("dev-1", devices[0].DeviceId);

        await auth.UnbindDeviceAsync("dev-1");
        Assert.False((await auth.GetDevicesAsync())[0].Enabled);
    }

    [Fact]
    public async Task EnvService_Flat_SaveDedupesByNameAndValue_AndGetPageFilters()
    {
        // EnvService 读侧走 CacheManager（经 configPath 指向的文件库），写侧用同一文件库上下文保证读写一致；
        // 先清理上次运行可能遗留的库文件，保证用例自包含（Sqlite 连接池句柄跨用例存留，须先清池）
        var dbFile = Path.GetFullPath(Path.Combine("db", "user-removal-test.db"));
        if (File.Exists(dbFile))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbFile);
        }
        using var fileDb = new QuantumSqliteDbContext();
        fileDb.Database.EnsureCreated();
        var envService = new EnvService(fileDb);

        // 首批保存：全部新增（同批次内不做去重，与既有行为一致）
        await envService.Save(
        [
            new EnvModelPostModel { Name = "varA", Value = "1" },
            new EnvModelPostModel { Name = "varB", Value = "2" }
        ]);
        Assert.Equal(2, CacheManager.Get<EnvModel>().Count);

        // 再次保存同名同值：对照存量去重跳过
        var messages = await envService.Save([new EnvModelPostModel { Name = "varA", Value = "1" }]);
        Assert.Single(messages);
        Assert.Equal(2, CacheManager.Get<EnvModel>().Count);

        // 按值删除（模拟请求级新上下文：Save 后 fileDb 仍跟踪首批实体，与缓存实例同 Id 会撞身份映射）
        using (var deleteDb = new QuantumSqliteDbContext())
        {
            Assert.True(await new EnvService(deleteDb).DeleteByValuesAsync("2"));
        }
        Assert.Single(CacheManager.Get<EnvModel>());

        // 列表：Enable 过滤 + 关键字
        var page = envService.GetPage(new EnvQuery { Key = "varA" });
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("varA", page.Data[0].Name);

        // 精确查询
        Assert.Single(envService.Query("vara", null));
        Assert.Empty(envService.Query("varB", null));

        // 清缓存，避免污染其他用例
        CacheManager.Refresh<EnvModel>();
    }

    [Fact]
    public async Task NotifySetting_IsGlobalSingleRow()
    {
        var service = new AppNotifySettingService(_db);

        // 默认全开（无记录）
        var setting = await service.GetAsync();
        Assert.Equal(AppNotifySettingService.GlobalId, setting.Id);
        Assert.True(setting.TaskPush);

        // 更新写回同一行
        await service.UpdateAsync(new AppNotifySettingModel { TaskPush = false, DndStart = "23:00", DndEnd = "07:00" });
        var reloaded = await service.GetAsync();
        Assert.False(reloaded.TaskPush);
        Assert.True(reloaded.SystemPush);
        Assert.Equal("23:00", reloaded.DndStart);
        Assert.Single(await _db.AppNotifySettings.ToListAsync());

        // 非法免打扰格式拒绝（严格 HH:mm 形态 + 00:00-23:59 数值范围）：
        // 旧校验只查 \d{2}:\d{2} 形态，"25:99" 也能落库 → 客户端解析失败后静默不抑制
        await Assert.ThrowsAsync<BusinessException>(() => service.UpdateAsync(new AppNotifySettingModel { DndStart = "2:5" }));
        await Assert.ThrowsAsync<BusinessException>(() => service.UpdateAsync(new AppNotifySettingModel { DndStart = "25:99" }));
        await Assert.ThrowsAsync<BusinessException>(() => service.UpdateAsync(new AppNotifySettingModel { DndEnd = "24:00" }));

        // 合法值通过；清空（null）= 关闭免打扰
        await service.UpdateAsync(new AppNotifySettingModel { DndStart = "09:05", DndEnd = "18:30" });
        Assert.Equal("09:05", (await service.GetAsync()).DndStart);
        await service.UpdateAsync(new AppNotifySettingModel { DndStart = null, DndEnd = null });
        var cleared = await service.GetAsync();
        Assert.Null(cleared.DndStart);
        Assert.Null(cleared.DndEnd);
    }
}

/// <summary>
/// §5-3 统一全局静态串行集合：成员类切换进程级静态态——Consts（含 SystemConfigHelper.configPath）、
/// CacheManager/MemoryObjectCache、SendMessageHelper.Sender——DisableParallelization=true 使整桶串行、
/// 且不与任何可并行集合重叠。原 CacheManagerState 集合已并入本集合（凡触碰上述任一静态者一律挂此）。
/// </summary>
[CollectionDefinition("ConstsState", DisableParallelization = true)]
public class ConstsStateCollection
{
}
