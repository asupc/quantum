using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// SystemConfigHelper 配置往返：appsettings.json Quantum 节的自动生成、全量字段读写
/// （含密钥）、外部节（Logging/AllowedHosts）保留、缺失字段的容错回填。
/// 通过切换 configPath 指向临时文件隔离，避免触碰真实配置；Consts 全局态先存后还。
/// </summary>
[Collection("ConstsState")]
public class SystemConfigHelperTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalConfigPath;
    private readonly (string key, string issuer, string audience) _originalConsts;

    public SystemConfigHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "quantum-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalConfigPath = SystemConfigHelper.configPath;
        _originalConsts = (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience);
        SystemConfigHelper.configPath = Path.Combine(_tempDir, "appsettings.json");
    }

    public void Dispose()
    {
        SystemConfigHelper.configPath = _originalConfigPath;
        (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience) = _originalConsts;
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Update_ClampsMessageQueueIntervalToMinOne(int incoming)
    {
        // §1-1：保存侧把 MessageQueueInterval 钳到 ≥1，防止 0/负值进入消费循环 Thread.Sleep 忙等
        var service = new Quantum.Application.SystemConfigService(null!);
        service.Update(new Setting { MessageQueueInterval = incoming });

        Assert.Equal(1, SystemConfigHelper.GetSetting().MessageQueueInterval);
    }

    [Fact]
    public void Update_DetectsRestartGatedChanges()
    {
        // §5-8：可信代理/跨域白名单/Swagger 属启动期配置，变更需重启；其余字段即时生效。
        // 预置基准配置后，只有这三项变更才让 restartRequired=true。
        SystemConfigHelper.SetSetting(new Setting
        {
            UserName = "u",
            PassWord = "p",
            Port = 5088,
            KnownProxies = "10.0.0.1",
            AllowedOrigins = "https://a.com",
            EnableSwagger = false,
            Footer = "base"
        });
        var service = new Quantum.Application.SystemConfigService(null!);

        // 三项原样、仅改即时生效字段（Footer）→ 不需重启
        var restartOnFooter = false;
        Assert.True(service.Update(new Setting
        {
            KnownProxies = "10.0.0.1",
            AllowedOrigins = "https://a.com",
            EnableSwagger = false,
            Footer = "changed-footer"
        }, out restartOnFooter));
        Assert.False(restartOnFooter);

        // 仅 AllowedOrigins 变更 → 需重启
        var restartOnOrigins = false;
        Assert.True(service.Update(new Setting
        {
            KnownProxies = "10.0.0.1",
            AllowedOrigins = "https://b.com",
            EnableSwagger = false,
            Footer = "changed-footer"
        }, out restartOnOrigins));
        Assert.True(restartOnOrigins);
    }

    [Fact]
    public void Update_StaleSettingsForm_DoesNotRestoreOldPasswordOrCutoff()
    {
        SystemConfigHelper.SetSetting(new Setting
        {
            UserName = "tester", PassWord = "old-password", DBType = "SQLite", DBAddress = "test.db"
        });
        var service = new Quantum.Application.SystemConfigService(null!);
        var staleForm = service.GetSetting();

        SystemConfigHelper.UpdateSetting(current =>
        {
            current.PassWord = "new-password";
            current.UserTokenNotBefore = 42;
            current.ManagerTokenNotBefore = 42;
        });
        service.Update(staleForm);

        var saved = SystemConfigHelper.GetSetting();
        Assert.Equal("new-password", saved.PassWord);
        Assert.Equal(42, saved.UserTokenNotBefore);
        Assert.Equal(42, saved.ManagerTokenNotBefore);
    }

    [Fact]
    public async Task UpdatePassword_FailedConfigWrite_DoesNotAdvanceInMemoryCutoff()
    {
        if (!OperatingSystem.IsWindows()) return;

        SystemConfigHelper.SetSetting(new Setting
        {
            UserName = "tester", PassWord = "old-password", DBType = "SQLite",
            DBAddress = "test.db", Host = "http://127.0.0.1", Port = 5088
        });
        SystemConfigHelper.GetSetting();
        var originalCutoff = Consts.UserTokenNotBefore;

        using (new FileStream(SystemConfigHelper.configPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var service = new Quantum.Application.SystemConfigService(null!);
            var error = await Record.ExceptionAsync(() => service.UpdatePassword(new UpdatePasswordRequest
            {
                OldUserName = "tester", OldPassword = "old-password", NewPassword = "new-password"
            }));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }

        Assert.Equal(originalCutoff, Consts.UserTokenNotBefore);
        Assert.Equal("old-password", SystemConfigHelper.GetSetting().PassWord);
    }

    [Fact]
    public void GetSetting_MutationOnReturnedObject_DoesNotLeakIntoCache()
    {
        // §1-7：GetSetting 命中 memoize 后返回克隆而非缓存实例——
        // 调用方（SystemConfigService.GetSetting 脱敏置空、UpdatePassword 改密）就地改对象，
        // 绝不能污染后续读取看到的权威配置（否则一次脱敏即把 UserName/PassWord 从缓存抹掉）。
        SystemConfigHelper.SetSetting(new Setting { UserName = "canonical", PassWord = "p", Port = 5088 });

        var first = SystemConfigHelper.GetSetting();
        first.UserName = "";
        first.PassWord = "";

        var second = SystemConfigHelper.GetSetting();
        Assert.Equal("canonical", second.UserName);
        Assert.Equal("p", second.PassWord);
    }

    [Fact]
    public void GetSetting_MissingFile_AutoGeneratesSkeletonWithQuantumSection()
    {
        var setting = SystemConfigHelper.GetSetting();

        Assert.True(File.Exists(SystemConfigHelper.configPath), "自动生成应落盘 appsettings.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SystemConfigHelper.configPath));
        var root = doc.RootElement;
        // 骨架包含宿主所需节 + Quantum 节
        Assert.True(root.TryGetProperty("Quantum", out var quantum));
        Assert.True(root.TryGetProperty("Logging", out _));
        Assert.True(root.TryGetProperty("AllowedHosts", out _));
        // 必填项已回填（Consts 刷新在测试装配内被 ModuleInitializer 关闭，此处不断言全局态）
        Assert.False(string.IsNullOrEmpty(setting.PassWord));
        Assert.False(string.IsNullOrEmpty(setting.SymmetricSecurityKey));
        Assert.True(setting.Port > 0);
    }

    [Fact]
    public void SetThenGetSetting_RoundTripsAllFieldsIncludingSecrets()
    {
        var config = new Setting
        {
            UserName = "round-admin",
            PassWord = "secret-pass",
            DBType = "SQLite",
            DBAddress = "roundtrip.db",
            Port = 5089,
            Host = "http://0.0.0.0",
            SymmetricSecurityKey = "round-trip-symmetric-key",
            SecurityIssuer = "Issuer.RoundTrip",
            SecurityAudience = "Audience.RoundTrip",
            LoginNotify = true
        };
        SystemConfigHelper.SetSetting(config);

        var loaded = SystemConfigHelper.GetSetting();

        // 密钥类字段必须完整往返（配置文件是唯一存储，丢密钥 = 全员掉登录）
        Assert.Equal("round-admin", loaded.UserName);
        Assert.Equal("secret-pass", loaded.PassWord);
        Assert.Equal("roundtrip.db", loaded.DBAddress);
        Assert.Equal(5089, loaded.Port);
        Assert.Equal("round-trip-symmetric-key", loaded.SymmetricSecurityKey);
        Assert.Equal("Issuer.RoundTrip", loaded.SecurityIssuer);
        Assert.Equal("Audience.RoundTrip", loaded.SecurityAudience);
        Assert.True(loaded.LoginNotify);
    }

    [Fact]
    public void SetSetting_PreservesForeignSections()
    {
        // 先造一份带 Logging/AllowedHosts 的配置
        SystemConfigHelper.SetSetting(new Setting { UserName = "u", PassWord = "p", Port = 5088 });

        var updated = SystemConfigHelper.GetSetting();
        updated.Footer = "footer-424242";
        SystemConfigHelper.SetSetting(updated);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SystemConfigHelper.configPath));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("Logging", out var logging), "写回应保留 Logging 节");
        Assert.True(root.TryGetProperty("AllowedHosts", out _), "写回应保留 AllowedHosts 节");
        Assert.Equal("Warning", logging.GetProperty("LogLevel").GetProperty("Default").GetString());
        Assert.Equal("footer-424242", root.GetProperty("Quantum").GetProperty("Footer").GetString());
    }

    [Fact]
    public void GetSetting_FaultyValues_FilledWithDefaults()
    {
        var json = """
        {
          "Quantum": {
            "Port": 0,
            "CommandTimeInterval": -5,
            "DBAddress": "",
            "DBType": "",
            "UserName": "",
            "PassWord": "",
            "SymmetricSecurityKey": "",
            "SecurityIssuer": "",
            "SecurityAudience": ""
          },
          "AllowedHosts": "*"
        }
        """;
        File.WriteAllText(SystemConfigHelper.configPath, json);

        var setting = SystemConfigHelper.GetSetting();

        Assert.Equal(5088, setting.Port);
        Assert.Equal(3, setting.CommandTimeInterval);
        Assert.Equal("SQLite", setting.DBType);
        Assert.False(string.IsNullOrEmpty(setting.DBAddress));
        Assert.False(string.IsNullOrEmpty(setting.UserName));
        Assert.False(string.IsNullOrEmpty(setting.PassWord));
        Assert.False(string.IsNullOrEmpty(setting.SymmetricSecurityKey));
        // 容错回填写回原文件
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SystemConfigHelper.configPath));
        Assert.Equal(5088, doc.RootElement.GetProperty("Quantum").GetProperty("Port").GetInt32());
    }

    [Fact]
    public void GetSetting_MalformedRoot_ThrowsInsteadOfOverwriting()
    {
        // 不是 JSON 对象的配置必须 fail-fast，禁止自动覆盖用户配置
        File.WriteAllText(SystemConfigHelper.configPath, "[1,2,3]");

        Assert.Throws<InvalidOperationException>(() => SystemConfigHelper.GetSetting());
        Assert.Equal("[1,2,3]", File.ReadAllText(SystemConfigHelper.configPath));
    }

    [Fact]
    public void GetSetting_TextCache_InvalidatedByFileChange()
    {
        // 消息泵每百毫秒级读配置：mtime 未变命中文本缓存（不再每次读盘+全树解析），文件变化后读到新值
        var setting = new Setting
        {
            AppKey = "cache-test",
            DBType = "SQLite",
            DBAddress = "cache-t.db",
            PassWord = "p",
            UserName = "u1",
            Port = 5088,
            Host = "http://*"
        };
        SystemConfigHelper.SetSetting(setting);
        Assert.Equal("u1", SystemConfigHelper.GetSetting().UserName);

        // 改写配置并显式推进 mtime（规避同 tick 写入缓存未失效的边界），应读到新值
        setting.UserName = "u2";
        SystemConfigHelper.SetSetting(setting);
        File.SetLastWriteTimeUtc(SystemConfigHelper.configPath,
            File.GetLastWriteTimeUtc(SystemConfigHelper.configPath).AddSeconds(2));
        Assert.Equal("u2", SystemConfigHelper.GetSetting().UserName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("42")]
    public void GetSetting_CorruptConfigFile_FailsFastWithFriendlyError(string content)
    {
        // 空文件/残缺 JSON（历史版本写一半崩溃的遗留）必须与「非对象」一样 fail-fast 且给出可操作提示，
        // 而不是抛裸 JsonException；同时绝不自动覆盖坏文件
        File.WriteAllText(SystemConfigHelper.configPath, content);

        var ex = Assert.Throws<InvalidOperationException>(() => SystemConfigHelper.GetSetting());

        Assert.Contains("不是 JSON 对象", ex.Message);
        Assert.Equal(content, File.ReadAllText(SystemConfigHelper.configPath));
    }

    [Fact]
    public void SetSetting_AtomicWrite_LeavesNoTempResidue()
    {
        // 原子替换：先写 *.tmp 再 rename，正常路径结束后目录里只应剩配置文件本身
        SystemConfigHelper.SetSetting(new Setting { UserName = "u1", PassWord = "p", Port = 5088 });
        SystemConfigHelper.SetSetting(new Setting { UserName = "u2", PassWord = "p", Port = 5088 });

        var files = Directory.GetFiles(Path.GetDirectoryName(SystemConfigHelper.configPath)!);
        Assert.All(files, f => Assert.Equal("appsettings.json", Path.GetFileName(f)));
    }

    [Fact]
    public void SetSetting_ConcurrentWriters_FileRemainsConsistent()
    {
        // 写侧互斥下的不变量：全程没有写坏文件；最终盘上内容可解析，
        // 且缓存与磁盘一致（无「A 文本 @ B mtime」错配导致读到旧值）
        const int writers = 4, rounds = 5;
        var barrier = new System.Threading.Barrier(writers);
        var tasks = Enumerable.Range(0, writers).Select(t => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < rounds; i++)
            {
                var setting = SystemConfigHelper.GetSetting();
                setting.Footer = $"f-{t}-{i}";
                SystemConfigHelper.SetSetting(setting);
            }
        })).ToArray();
        Task.WaitAll(tasks);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SystemConfigHelper.configPath));
        var footerOnDisk = doc.RootElement.GetProperty("Quantum").GetProperty("Footer").GetString();
        Assert.StartsWith("f-", footerOnDisk);
        Assert.Equal(footerOnDisk, SystemConfigHelper.GetSetting().Footer);
    }
}
