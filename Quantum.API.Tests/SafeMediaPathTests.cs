using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 下载根目录受控相对路径解析：ctx.File 门面（ResolveDirectory，异常文案语义）与
/// AppMedia 通道（TryResolveFile，含文件名段校验）共用实现的安全基线。
/// </summary>
public class SafeMediaPathTests : IDisposable
{
    private readonly string _root = SafeMediaPath.ResolveRoot(
        Path.Combine(Path.GetTempPath(), "qmediapath-" + Guid.NewGuid().ToString("N")));

    public SafeMediaPathTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 临时目录清理尽力而为 */ }
    }

    [Fact]
    public void ResolveRoot_空目录_回退downloads()
    {
        var resolved = SafeMediaPath.ResolveRoot("");
        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith("downloads", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDirectory_空子目录_返回根()
    {
        Assert.Equal(_root, SafeMediaPath.ResolveDirectory(_root, null));
        Assert.Equal(_root, SafeMediaPath.ResolveDirectory(_root, "  "));
    }

    [Fact]
    public void ResolveDirectory_合法嵌套_反斜杠也接受()
    {
        var resolved = SafeMediaPath.ResolveDirectory(_root, "music\\周杰伦");
        Assert.StartsWith(_root + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("music", "周杰伦"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../etc")]            // .. 穿越
    [InlineData("music/../../etc")]   // 混在合法段中的 ..
    [InlineData("..")]
    [InlineData("C:\\absolute")]      // 盘符绝对路径
    [InlineData("/etc")]              // 根绝对路径
    [InlineData("http://x/y")]        // 冒号（盘符/协议）
    public void ResolveDirectory_非法子目录_抛业务异常(string subDir)
    {
        Assert.Throws<BusinessException>(() => SafeMediaPath.ResolveDirectory(_root, subDir));
    }

    [Fact]
    public void TryResolveFile_合法相对路径_返回根内绝对路径()
    {
        Assert.True(SafeMediaPath.TryResolveFile(_root, "music/周杰伦 - 晴天.mp3", out var absolute));
        Assert.StartsWith(_root + Path.DirectorySeparatorChar, absolute, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("music", "周杰伦 - 晴天.mp3"), absolute, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]                                  // 空
    [InlineData("   ")]
    [InlineData("music/../../etc/passwd")]            // .. 穿越
    [InlineData("..\\music\\a.mp3")]
    [InlineData("C:/abs/a.mp3")]                      // 绝对路径
    [InlineData("/abs/a.mp3")]
    [InlineData("music/a.mp3.")]                      // 结尾点（Windows 截断语义）
    [InlineData("music/CON.mp3")]                     // 保留设备名
    [InlineData("music/nul.mp3")]                     // 保留设备名（大小写不敏感）
    [InlineData("music/COM1.mp3")]
    [InlineData("music/a<b.mp3")]                     // 非法字符
    [InlineData("music/a|b.mp3")]
    [InlineData("music/ a.mp3")]                      // 段首空白
    [InlineData(".")]
    [InlineData("./music/a.mp3")]                     // 当前目录段
    public void TryResolveFile_非法路径_拒绝(string relative)
    {
        Assert.False(SafeMediaPath.TryResolveFile(_root, relative, out _));
    }

    [Fact]
    public void TryResolveFile_根本身_拒绝()
    {
        // 不含文件名的路径（纯目录）不是文件
        Assert.False(SafeMediaPath.TryResolveFile(_root, "music", out _));
    }
}
