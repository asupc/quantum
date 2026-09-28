using System.Security.Cryptography;
using Quantum.Entities.Config;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 通道主密钥三级来源：QUANTUM_CHANNEL_KEY_FILE > QUANTUM_CHANNEL_MASTER_KEY > appsettings.json
/// Quantum 节（缺失时 GetSetting 自动生成并落盘，一次生成终身使用）。
/// 通过切换 configPath 指向临时文件 + 清空环境变量隔离；全局态先存后还。
/// </summary>
[Collection("ConstsState")]
public class ChannelMasterKeyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalConfigPath;
    private readonly string _originalKeyFile;
    private readonly string _originalMasterKey;

    public ChannelMasterKeyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "quantum-channel-key-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalConfigPath = SystemConfigHelper.configPath;
        SystemConfigHelper.configPath = Path.Combine(_tempDir, "appsettings.json");
        // 环境变量是进程级全局态：进用例前清空，确保走 appsettings 来源；Dispose 还原
        _originalKeyFile = Environment.GetEnvironmentVariable("QUANTUM_CHANNEL_KEY_FILE");
        _originalMasterKey = Environment.GetEnvironmentVariable("QUANTUM_CHANNEL_MASTER_KEY");
        Environment.SetEnvironmentVariable("QUANTUM_CHANNEL_KEY_FILE", null);
        Environment.SetEnvironmentVariable("QUANTUM_CHANNEL_MASTER_KEY", null);
    }

    public void Dispose()
    {
        SystemConfigHelper.configPath = _originalConfigPath;
        Environment.SetEnvironmentVariable("QUANTUM_CHANNEL_KEY_FILE", _originalKeyFile);
        Environment.SetEnvironmentVariable("QUANTUM_CHANNEL_MASTER_KEY", _originalMasterKey);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private static string RandomKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void GetSetting_MissingKey_AutoGeneratesOnceAndPersists()
    {
        // 新环境无主密钥：首个 GetSetting 生成 32 字节 Base64 并写回 appsettings.json
        var first = SystemConfigHelper.GetSetting();
        Assert.False(string.IsNullOrEmpty(first.ChannelMasterKey));
        Assert.Equal(32, Convert.FromBase64String(first.ChannelMasterKey).Length);
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SystemConfigHelper.configPath)))
        {
            var persisted = doc.RootElement.GetProperty("Quantum").GetProperty("ChannelMasterKey").GetString();
            Assert.Equal(first.ChannelMasterKey, persisted);
        }

        // 密钥必须终身稳定：重复读取绝不换新（换新 = 已存通道凭据全部无法解密）
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(first.ChannelMasterKey, SystemConfigHelper.GetSetting().ChannelMasterKey);
        }
    }

    [Fact]
    public void GetSetting_ExistingKey_NeverRegenerated()
    {
        var key = RandomKeyBase64();
        SystemConfigHelper.SetSetting(new Setting { UserName = "u", PassWord = "p", Port = 5088, ChannelMasterKey = key });

        Assert.Equal(key, SystemConfigHelper.GetSetting().ChannelMasterKey);
    }

    [Fact]
    public void Protector_NoEnv_UsesAppsettingsKey()
    {
        var key = RandomKeyBase64();
        SystemConfigHelper.SetSetting(new Setting { UserName = "u", PassWord = "p", Port = 5088, ChannelMasterKey = key });

        var protector = new Quantum.Application.Channels.ChannelSecretProtector();
        Assert.True(protector.Available);
        // 参数less 实例加密的密文，只能被 appsettings 里那把密钥解开——证明来源确实是配置文件
        var ciphertext = protector.Protect("credential-plain", "account-1", "login");
        Assert.Equal("credential-plain", new Quantum.Application.Channels.ChannelSecretProtector(Convert.FromBase64String(key)).Unprotect(ciphertext, "account-1", "login"));
    }

    [Fact]
    public void Protector_EnvMasterKey_OverridesAppsettings()
    {
        var envKey = RandomKeyBase64();
        SystemConfigHelper.SetSetting(new Setting
        {
            UserName = "u", PassWord = "p", Port = 5088,
            ChannelMasterKey = RandomKeyBase64() // 配置里放另一把有效密钥，验证环境变量优先
        });
        Environment.SetEnvironmentVariable("QUANTUM_CHANNEL_MASTER_KEY", envKey);

        var protector = new Quantum.Application.Channels.ChannelSecretProtector();
        var ciphertext = protector.Protect("credential-plain", "account-1", "login");
        Assert.Equal("credential-plain",
            new Quantum.Application.Channels.ChannelSecretProtector(Convert.FromBase64String(envKey)).Unprotect(ciphertext, "account-1", "login"));
    }

    [Fact]
    public void Protector_KeyFile_OverridesAppsettings()
    {
        var fileKey = RandomKeyBase64();
        var keyFile = Path.Combine(_tempDir, "channel.key");
        File.WriteAllText(keyFile, fileKey + "\n");
        SystemConfigHelper.SetSetting(new Setting
        {
            UserName = "u", PassWord = "p", Port = 5088,
            ChannelMasterKey = RandomKeyBase64()
        });
        Environment.SetEnvironmentVariable("QUANTUM_CHANNEL_KEY_FILE", keyFile);

        var protector = new Quantum.Application.Channels.ChannelSecretProtector();
        var ciphertext = protector.Protect("credential-plain", "account-1", "login");
        Assert.Equal("credential-plain",
            new Quantum.Application.Channels.ChannelSecretProtector(Convert.FromBase64String(fileKey)).Unprotect(ciphertext, "account-1", "login"));
    }

    [Theory]
    [InlineData("not-a-valid-base64!!")]
    [InlineData("dG9vLXNob3J0")] // 合法 Base64 但不足 32 字节
    public void Protector_InvalidAppsettingsKey_UnavailableWithReason(string badKey)
    {
        SystemConfigHelper.SetSetting(new Setting { UserName = "u", PassWord = "p", Port = 5088, ChannelMasterKey = badKey });

        var protector = new Quantum.Application.Channels.ChannelSecretProtector();
        Assert.False(protector.Available);
        // 坏值必须大声报错而非悄悄换新：静默换新会让既有凭据全部「无法解密」且无人知晓根因
        Assert.Contains("32 字节", protector.AvailabilityError);
    }

    [Fact]
    public void SystemConfigService_GetSetting_MasksChannelMasterKey()
    {
        SystemConfigHelper.SetSetting(new Setting { UserName = "u", PassWord = "p", Port = 5088, ChannelMasterKey = RandomKeyBase64() });

        var masked = new Quantum.Application.SystemConfigService(null!).GetSetting();

        // 主密钥与 JWT 密钥同级敏感：接口层永不外发，设置页回存也不回写该字段
        Assert.Equal(string.Empty, masked.ChannelMasterKey);
    }
}
