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
        // 首次部署语义（2026-10-10 初始化向导）：登录凭据刻意留空并标记未初始化，
        // 由 Web 初始化向导引导用户自建；机器侧密钥仍自动生成（Consts 刷新在测试装配内关闭，不断言全局态）
        Assert.True(SystemConfigHelper.NeedSetup(setting), "缺配置自动生成 = 待初始化向导");
        Assert.Equal(false, setting.SetupCompleted);
        Assert.True(string.IsNullOrEmpty(setting.PassWord));
        Assert.True(string.IsNullOrEmpty(setting.UserName));
        Assert.False(string.IsNullOrEmpty(setting.SymmetricSecurityKey));
        Assert.True(setting.Port > 0);
    }

    [Fact]
    public void NeedSetup_LegacyConfigWithoutMarker_IsTreatedAsInitialized()
    {
        // 存量部署（配置里已有账号密码、无 SetupCompleted 键）升级后不得被向导劫持
        SystemConfigHelper.SetSetting(new Setting { UserName = "legacy-admin", PassWord = "legacy-pass", Port = 5088 });

        var loaded = SystemConfigHelper.GetSetting();
        Assert.Null(loaded.SetupCompleted);
        Assert.False(SystemConfigHelper.NeedSetup(loaded));
    }

    [Fact]
    public void NeedSetup_EmptyCredentials_TriggersWizard_EvenWithoutMarker()
    {
        // appsettings.example.json 留空账号密码 = 主动选择走向导
        SystemConfigHelper.SetSetting(new Setting { UserName = "", PassWord = "", Port = 5088 });

        Assert.True(SystemConfigHelper.NeedSetup(SystemConfigHelper.GetSetting()));
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
        // 登录凭据不再随机回填（2026-10-10 初始化向导）：留空 = 引导用户走 Web 初始化向导自建账号
        Assert.True(string.IsNullOrEmpty(setting.UserName));
        Assert.True(string.IsNullOrEmpty(setting.PassWord));
        Assert.True(SystemConfigHelper.NeedSetup(setting));
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
    public void SetSetting_RenameUnavailable_FallsBackToInPlaceWrite()
    {
        // Docker 单文件 bind-mount 部署形态（compose 挂载 ./app/appsettings.json:/app/appsettings.json）：
        // 目标路径是挂载点，Linux rename 固定 EBUSY（非瞬时占用，重试无解）——
        // 原子替换穷尽重试后必须降级为原地直写（挂载文件可直写且写穿宿主），否则 Web 端保存设置永远失败。
        SystemConfigHelper.SetSetting(new Setting { UserName = "u0", PassWord = "p", Port = 5088 });
        SystemConfigHelper.TestHookMoveFile = (_, _) => throw new IOException("Device or resource busy");
        try
        {
            SystemConfigHelper.UpdateSetting(current => current.TaskNetworkAllowlist = "192.168.1.50,qb.lan");

            // 读回（缓存 + 解析）与磁盘都应看到新值：降级路径同样同步文本缓存，不得停在上轮快照
            Assert.Equal("192.168.1.50,qb.lan", SystemConfigHelper.GetSetting().TaskNetworkAllowlist);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SystemConfigHelper.configPath));
            Assert.Equal("192.168.1.50,qb.lan",
                doc.RootElement.GetProperty("Quantum").GetProperty("TaskNetworkAllowlist").GetString());
            // 降级路径不得残留 *.tmp
            Assert.All(Directory.GetFiles(Path.GetDirectoryName(SystemConfigHelper.configPath)!),
                f => Assert.Equal("appsettings.json", Path.GetFileName(f)));
        }
        finally
        {
            SystemConfigHelper.TestHookMoveFile = null;
        }
    }

    [Fact]
    public void SetSetting_RenameUnavailable_InPlaceWriteAlsoLocked_Throws()
    {
        // rename 不可行 + 原地直写也被独占锁挡住 = 写真的失败：必须抛错冒泡，不得静默吞成「保存成功」
        SystemConfigHelper.SetSetting(new Setting { UserName = "u0", PassWord = "p", Port = 5088 });
        SystemConfigHelper.TestHookMoveFile = (_, _) => throw new IOException("Device or resource busy");
        try
        {
            using (new FileStream(SystemConfigHelper.configPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() =>
                    SystemConfigHelper.SetSetting(new Setting { UserName = "u1", PassWord = "p", Port = 5088 }));
            }
            // 失败后磁盘与读取保持旧值
            Assert.Equal("u0", SystemConfigHelper.GetSetting().UserName);
        }
        finally
        {
            SystemConfigHelper.TestHookMoveFile = null;
        }
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

    // ---------- memo 单源一致性（§1-7 快速路径不得钉住错配解析结果） ----------

    [Fact]
    public async Task ConcurrentReadersAndWriters_NeverObserveMixedKeyUserNamePairing()
    {
        // 并发读写下的不变量：返回的配置必须始终是「某一次真实写入」的完整快照——
        // 密钥与账号不得来自不同版本（错配会让改钥后旧签名密钥继续生效）。
        SystemConfigHelper.SetSetting(FullyPopulated(0));
        var written = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        for (var i = 0; i <= 30; i++)
        {
            written[Key(i)] = User(i);
        }
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<string>();
        var stop = false;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var snapshot = SystemConfigHelper.GetSetting();
                if (!written.TryGetValue(snapshot.SymmetricSecurityKey ?? "", out var expectedUser)
                    || snapshot.UserName != expectedUser)
                {
                    mismatches.Add($"key={snapshot.SymmetricSecurityKey} user={snapshot.UserName}");
                }
            }
        })).ToArray();
        var writers = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
        {
            for (var i = 1; i <= 30; i++)
            {
                SystemConfigHelper.UpdateSetting(current =>
                {
                    current.SymmetricSecurityKey = Key(i);
                    current.UserName = User(i);
                });
            }
        })).ToArray();
        await Task.WhenAll(writers);
        Volatile.Write(ref stop, true);
        await Task.WhenAll(readers);

        Assert.Empty(mismatches);
        // 最终写入后读到最终值（写后即新，快速路径不得停留在旧快照）
        SystemConfigHelper.UpdateSetting(current => { current.SymmetricSecurityKey = "FINAL-KEY"; current.UserName = "FINAL-USER"; });
        var final = SystemConfigHelper.GetSetting();
        Assert.Equal("FINAL-KEY", final.SymmetricSecurityKey);
        Assert.Equal("FINAL-USER", final.UserName);
    }

    [Fact]
    public void GetSetting_ParsesTheTextReadOnce_AndNeverPublishesMismatchedMemo()
    {
        // memo 单源一致性：memo.Text 必须与 memo.Setting 出自同一份文本。
        // 修复前发布路径取 _cachedText（发布时刻的缓存文本），解析取的是更早读到的 currentText——
        // 两者之间若有写者换文本，就会把新文本与旧解析结果配成一组，而快速路径按 Text 引用判断命中，
        // 此后每次 GetSetting 都返回那份旧解析结果（喂给 Consts.SetJwtSecrets = 改密钥后旧密钥继续生效）。
        SystemConfigHelper.SetSetting(FullyPopulated(1));
        SystemConfigHelper.GetSetting();
        // 再写一次使文本缓存失效：下一次 GetSetting 必走「解析 + 发布」路径（否则命中快速路径，交错点不触发）
        SystemConfigHelper.SetSetting(FullyPopulated(1));
        var swapped = false;
        SystemConfigHelper.TestHookBeforeMemoPublish = () =>
        {
            if (swapped) return;
            swapped = true;
            // 解析已完成、memo 尚未发布：写者此刻落盘新文本（等价于并发改密/改钥的窗口）
            SystemConfigHelper.SetSetting(FullyPopulated(2));
        };
        try
        {
            var loaded = SystemConfigHelper.GetSetting();

            Assert.True(swapped, "交错点未被触发");
            // memo 的不变量：memo.Text 与 memo.Setting 必须描述同一份文本（同源），
            // 不得出现「文本 = 新版、设置 = 旧解析结果」的错配。
            var memo = SystemConfigHelper.InspectMemoForTest();
            if (memo != null)
            {
                var expectedKey = memo.Value.Setting.SymmetricSecurityKey;
                Assert.Contains(expectedKey, memo.Value.Text);
            }
            // 关键断言：后续读取必须看到最后写入的密钥（旧解析结果不得被钉住）
            Assert.Equal(Key(2), SystemConfigHelper.GetSetting().SymmetricSecurityKey);
            Assert.Equal(User(2), SystemConfigHelper.GetSetting().UserName);
        }
        finally
        {
            SystemConfigHelper.TestHookBeforeMemoPublish = null;
        }
    }

    private static string Key(int i) => $"memo-key-{i}";

    private static string User(int i) => $"memo-user-{i}";

    /// <summary>全部字段齐备的配置：避免容错回填在用例中途改钥（回填会替换 SymmetricSecurityKey/Issuer 等）。</summary>
    private static Setting FullyPopulated(int i) => new()
    {
        UserName = User(i),
        PassWord = "p",
        DBType = "SQLite",
        DBAddress = "memo.db",
        Port = 5088,
        Host = "http://*",
        SymmetricSecurityKey = Key(i),
        SecurityIssuer = "Issuer.Memo",
        SecurityAudience = "Audience.Memo",
        ChannelMasterKey = "bWVtby1tYXN0ZXIta2V5",
        MaxConcurrentTasks = 8,
        MessageQueueInterval = 100,
        CommandTimeInterval = 3
    };
}
