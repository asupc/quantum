using System.Net;
using Quantum.Application;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// ctx.File 受控文件落盘门面（QuantumFileFacade）安全基线：
/// 根目录 confinement（拒绝绝对路径 / .. 穿越）、文件名清洗、重名不覆盖、失败清理残片。
/// 落盘成功会清 AppMediaService 静态扫描缓存，须与 AppMediaServiceTests 串行，见 AppMediaScanStateCollection。
/// </summary>
[Collection("AppMediaScanState")]
public class QuantumFileFacadeTests : IDisposable
{
    private readonly string _root;

    public QuantumFileFacadeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "quantum-file-facade-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 临时目录清理失败不影响用例 */ }
    }

    private static QuantumFileFacade CreateFacade(string root, byte[] content, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new QuantumFileFacade(root, new HttpClient(new FakeHandler(content, status)));
    }

    [Fact]
    public async Task DownloadAsync_SavesUnderRootAndCreatesSubDir()
    {
        var payload = "hello-music"u8.ToArray();
        var facade = CreateFacade(_root, payload);

        var result = await facade.DownloadAsync("https://example.com/a.mp3", "晴天.mp3", "music/周杰伦");

        Assert.True(File.Exists(result.FullPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FullPath));
        Assert.Equal(payload.Length, result.Length);
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar,
            result.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("晴天.mp3", result.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a<b>c:d*.mp3")]      // Windows 非法字符
    [InlineData("name\twith\rcontrol.mp3")]
    [InlineData("trailing.dot..mp3. ")] // 结尾点/空格（Windows 语义）
    public async Task DownloadAsync_SanitizesFileName(string rawName)
    {
        var facade = CreateFacade(_root, [1, 2, 3]);

        var result = await facade.DownloadAsync("https://example.com/x", rawName);

        Assert.NotEqual(rawName, result.FileName);
        Assert.True(File.Exists(result.FullPath));
        Assert.Empty(Path.GetInvalidFileNameChars().Intersect(result.FileName));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("..")]
    [InlineData("D:\\outside")]       // 绝对路径
    [InlineData("/absolute")]
    public async Task DownloadAsync_RejectsSubDirOutsideRoot(string subDir)
    {
        var facade = CreateFacade(_root, [1]);

        await Assert.ThrowsAsync<BusinessException>(() =>
            facade.DownloadAsync("https://example.com/a.mp3", "a.mp3", subDir));
    }

    [Fact]
    public async Task DownloadAsync_RejectsNonHttpUrl()
    {
        var facade = CreateFacade(_root, [1]);

        await Assert.ThrowsAsync<BusinessException>(() =>
            facade.DownloadAsync("ftp://example.com/a.mp3", "a.mp3"));
        await Assert.ThrowsAsync<BusinessException>(() =>
            facade.DownloadAsync("", "a.mp3"));
    }

    [Fact]
    public async Task DownloadAsync_AppendsNumberInsteadOfOverwriting()
    {
        var facade = CreateFacade(_root, [1, 2, 3]);
        var first = await facade.DownloadAsync("https://example.com/a.mp3", "song.mp3");
        await File.WriteAllTextAsync(first.FullPath, "old");

        var second = await facade.DownloadAsync("https://example.com/a.mp3", "song.mp3");

        Assert.NotEqual(first.FullPath, second.FullPath);
        Assert.Equal("song (1).mp3", second.FileName);
        Assert.Equal("old", await File.ReadAllTextAsync(first.FullPath)); // 原文件未被覆盖
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(second.FullPath));
    }

    [Fact]
    public async Task DownloadAsync_HttpErrorCleansPartialFile()
    {
        var facade = CreateFacade(_root, [1, 2, 3], HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<BusinessException>(() =>
            facade.DownloadAsync("https://example.com/a.mp3", "broken.mp3"));

        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _content;

        public FakeHandler(byte[] content, HttpStatusCode status)
        {
            _content = content;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_content)
            });
        }
    }
}
