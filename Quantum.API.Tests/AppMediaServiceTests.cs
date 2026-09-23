using Quantum.Application;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 服务端媒体通道：白名单过滤、ID3 元数据与文件名回退、分页/关键字/子目录、
/// 路径穿越与不存在统一 404 语义（TryGetFile=false）、内嵌封面读取。
/// 扫描缓存是静态单槽，须与真实文件门面用例（QuantumFileFacadeTests）串行，见 AppMediaScanStateCollection。
/// </summary>
[Collection("AppMediaScanState")]
public class AppMediaServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppMediaService _service;

    public AppMediaServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qmedia-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "music"));

        // 1) 带 ID3v2 标签（歌名/歌手/内嵌封面）的 mp3：
        //    TagLib 对 mp3 要求真实 MPEG 帧头（仅 ID3 头会被判 CorruptFile），
        //    故用 8 帧「MPEG1 Layer3 128kbps 44.1kHz 静音帧」（帧长 417 字节）打底再写标签
        var tagged = Path.Combine(_root, "music", "tagged.mp3");
        var frame = new byte[417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;
        var audio = new List<byte>();
        for (var i = 0; i < 8; i++)
        {
            audio.AddRange(frame);
        }
        File.WriteAllBytes(tagged, audio.ToArray());
        var tagFile = TagLib.File.Create(tagged);
        tagFile.Tag.Title = "晴天ID3";
        tagFile.Tag.Performers = new[] { "周杰伦ID3" };
        tagFile.Tag.Pictures = new TagLib.IPicture[]
        {
            new TagLib.Picture(new TagLib.ByteVector(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 }))
            {
                MimeType = "image/png",
                Type = TagLib.PictureType.FrontCover
            }
        };
        tagFile.Save();

        // 2) 无标签 flac（垃圾字节 → 标签解析失败 → 文件名「歌手 - 歌名」回退）
        File.WriteAllBytes(Path.Combine(_root, "music", "周杰伦 - 晴天.flac"), new byte[] { 1, 2, 3, 4, 5 });

        // 3) 白名单外：不进列表、不可播
        File.WriteAllText(Path.Combine(_root, "music", "readme.txt"), "not media");
        File.WriteAllBytes(Path.Combine(_root, "music", "bad.ape"), new byte[] { 9, 9 });

        _service = new AppMediaService(_root);
        // 扫描缓存是静态的（跨请求/跨用例共享），用例间必须显式失效
        AppMediaService.InvalidateScanCache();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 临时目录清理尽力而为 */ }
    }

    [Fact]
    public async Task 列表_只含白名单媒体_且元数据取ID3优先文件名回退()
    {
        var result = await _service.ListAsync(1, 50, null, null);

        Assert.Equal(2, result.Total);
        var tagged = result.Items.Single(i => i.Name == "tagged.mp3");
        Assert.Equal("晴天ID3", tagged.Title);
        Assert.Equal("周杰伦ID3", tagged.Artist);
        Assert.Equal("mp3", tagged.Ext);
        Assert.Equal("music/tagged.mp3", tagged.Path);
        var plain = result.Items.Single(i => i.Name == "周杰伦 - 晴天.flac");
        Assert.Equal("晴天", plain.Title);
        Assert.Equal("周杰伦", plain.Artist);
    }

    [Fact]
    public async Task 列表_关键字与子目录过滤_分页()
    {
        Assert.Equal(1, (await _service.ListAsync(1, 50, "晴天", null)).Total);
        Assert.Equal(2, (await _service.ListAsync(1, 50, null, "music")).Total);
        Assert.Equal(0, (await _service.ListAsync(1, 50, null, "none")).Total);

        var page = await _service.ListAsync(2, 1, null, "music");
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task 列表_非法子目录_回空结果而非异常()
    {
        var result = await _service.ListAsync(1, 50, null, "../etc");
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public void 取文件_合法路径_返回类型与元信息()
    {
        Assert.True(_service.TryGetFile("music/tagged.mp3", out var absolute, out var contentType, out var mtime, out var size));
        Assert.True(File.Exists(absolute));
        Assert.Equal("audio/mpeg", contentType);
        Assert.True(mtime > DateTime.MinValue);
        Assert.True(size > 0);
    }

    [Theory]
    [InlineData("music/../../etc/passwd.mp3")]  // 穿越
    [InlineData("music/readme.txt")]            // 白名单外扩展名
    [InlineData("music/bad.ape")]               // ape 不可播（ExoPlayer 不支持）
    [InlineData("music/notexist.mp3")]          // 不存在
    [InlineData("C:/abs/a.mp3")]                // 绝对路径
    [InlineData("")]                            // 空路径
    public void 取文件_非法或不存在_统一拒绝(string path)
    {
        Assert.False(_service.TryGetFile(path, out _, out _, out _, out _));
    }

    [Fact]
    public void 封面_有内嵌封面_返回字节与Mime()
    {
        Assert.True(_service.TryGetCover("music/tagged.mp3", out var cover, out var mime));
        Assert.True(cover.Length > 0);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void 封面_无内嵌封面_返回false()
    {
        Assert.False(_service.TryGetCover("music/周杰伦 - 晴天.flac", out _, out _));
    }

    [Fact]
    public async Task 缓存失效_新落盘文件立即可见()
    {
        // 首次扫描建立缓存
        Assert.Equal(2, (await _service.ListAsync(1, 50, null, null)).Total);
        File.WriteAllBytes(Path.Combine(_root, "music", "later.mp3"), new byte[] { 1, 2, 3 });
        // 缓存窗口内不可见（30s）
        Assert.Equal(2, (await _service.ListAsync(1, 50, null, null)).Total);
        // ctx.File 门面落盘后失效 → 立即可见
        AppMediaService.InvalidateScanCache();
        Assert.Equal(3, (await _service.ListAsync(1, 50, null, null)).Total);
    }

    /// <summary>
    /// §2-5：异步单飞不得引入「冷启动空/半态」——失效后并发发起多路列表，每一路都必须拿到完整结果。
    /// 双重检查把并发冷请求收敛到同一趟完整扫描，重复多轮以放大竞态窗口。
    /// </summary>
    [Fact]
    public async Task 扫描单飞_并发冷请求_全部返回完整结果()
    {
        for (var round = 0; round < 5; round++)
        {
            AppMediaService.InvalidateScanCache();
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => _service.ListAsync(1, 50, null, null))
                .ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.All(results, r => Assert.Equal(2, r.Total));
        }
    }
}
