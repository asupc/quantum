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
        // 后台线程（并行集合经生产代码的 GetSetting 自动回填）可能还在写本作用域的配置文件：
        // 短暂重试删除，仍被占用则放弃清理（留给临时目录回收），不把清理竞态当用例失败
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Delete(_directory, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    return;
                }
                Thread.Sleep(50);
            }
        }
    }
}
