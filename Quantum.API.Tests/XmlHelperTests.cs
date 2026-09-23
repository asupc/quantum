using Quantum.Entities.Config;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// XmlHelper&lt;Setting&gt;：Setting.PassWord/DBType 标注 [JsonIgnore] 不序列化，Get/Set 往返只保留 XML 可表达字段。
/// </summary>
public class XmlHelperTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "qtest-setting-" + Guid.NewGuid().ToString("N") + ".xml");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Get_MissingFileReturnsDefault()
    {
        Assert.Null(XmlHelper<Setting>.Get(_path));
    }

    [Fact]
    public void SetThenGet_RoundTripsSerializableFields()
    {
        var setting = new Setting
        {
            UserName = "admin",
            PassWord = "secret",      // [JsonIgnore]：不应持久化
            DBType = "SQLite",        // [JsonIgnore]：不应持久化
            DBAddress = "quantum.db",
            Port = 5088,
            Host = "http://*",
            ServerPath = "https://q.example.com"
        };

        XmlHelper<Setting>.Set(setting, _path);
        var loaded = XmlHelper<Setting>.Get(_path);

        Assert.NotNull(loaded);
        Assert.Equal("admin", loaded.UserName);
        Assert.Equal("quantum.db", loaded.DBAddress);
        Assert.Equal(5088, loaded.Port);
        Assert.Equal("http://*", loaded.Host);
        Assert.Equal("https://q.example.com", loaded.ServerPath);
    }
}
