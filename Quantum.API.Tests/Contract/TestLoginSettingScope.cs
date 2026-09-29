using Quantum.Entities.Config;
using Quantum.Utils;

namespace Quantum.API.Tests.Contract;

internal sealed class TestLoginSettingScope : IDisposable
{
    private readonly string _oldPath = SystemConfigHelper.configPath;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "quantum-login-test-" + Guid.NewGuid().ToString("N"));

    public TestLoginSettingScope(string userName = "tester")
    {
        Directory.CreateDirectory(_directory);
        SystemConfigHelper.configPath = Path.Combine(_directory, "appsettings.json");
        SystemConfigHelper.SetSetting(new Setting
        {
            UserName = userName,
            PassWord = "test-password",
            DBType = "SQLite",
            DBAddress = "test.db"
        });
    }

    public void Dispose()
    {
        SystemConfigHelper.configPath = _oldPath;
        Directory.Delete(_directory, true);
    }
}
