using System.Collections.Concurrent;
using Quantum.Utils;
using TagLibFile = TagLib.File;

namespace Quantum.Application;

/// <summary>
/// 服务端媒体产物通道（M2）：枚举下载根目录（ctx.File 门面落盘产物）中的可播媒体，
/// 供 App 列表浏览与鉴权流式播放。路径校验统一走 <see cref="SafeMediaPath"/>（与落盘门面同一份实现）。
/// </summary>
public class AppMediaService
{
    private const int ScanCacheSeconds = 30;

    /// <summary>端内可播扩展名白名单（与 App 端 core:media MediaTypes 对齐；ape 排除——ExoPlayer 不支持）。</summary>
    public static readonly HashSet<string> PlayableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".mp4", ".mkv", ".webm"
    };

    /// <summary>Content-Type 按扩展名固定映射（nosniff 下浏览器/播放器不再猜测）。</summary>
    private static readonly Dictionary<string, string> ContentTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm"
    };

    /// <summary>扫描快照（不可变记录，引用整体替换）：大目录 <see cref="ScanCacheSeconds"/> 内不重复 IO。
    /// volatile 保证跨请求可见、读侧无锁；发布后 Items 不再变更，可安全共享给并发读取方。</summary>
    private sealed record ScanSnapshot(string Root, List<MediaItem> Items, DateTime At);

    private static volatile ScanSnapshot _scanCache = new(null, new(), DateTime.MinValue);

    /// <summary>扫描单飞闸：并发冷请求共享同一次扫描、异步等待（不各自阻塞线程池线程）——取代原同步 lock(ScanLock)。</summary>
    private static readonly SemaphoreSlim ScanGate = new(1, 1);

    /// <summary>元数据缓存：路径 + mtime(Ticks) 为键（文件被覆盖即自动失效），避免每次列表重复读标签。</summary>
    private static readonly ConcurrentDictionary<string, MediaMeta> MetaCache = new();

    /// <summary>
    /// 内嵌封面缓存：同键策略，避免每次封面请求重复读文件。
    /// 键含 mtime(Ticks)——文件被覆盖/删除后旧键永不再命中：孤儿键由目录扫描（BuildScan）顺带清理，
    /// 条目另有 MaxCoverCacheEntries 封顶，防超大媒体库把封面字节累成上百 MB。
    /// </summary>
    private static readonly ConcurrentDictionary<string, (byte[] Cover, string Mime)> CoverCache = new();

    /// <summary>封面缓存条目上限：超限先丢一个任意存活键（下次请求重新读文件）。</summary>
    private const int MaxCoverCacheEntries = 500;

    private readonly string _rootFullPath;

    /// <summary>生产构造：根目录取 appsettings Quantum:FileDownloadRoot（与 ctx.File 门面同源）。</summary>
    public AppMediaService()
        : this(SystemConfigHelper.GetSetting().FileDownloadRoot)
    {
    }

    /// <summary>测试/显式构造：直接指定下载根目录（相对运行目录或绝对路径）。</summary>
    internal AppMediaService(string configuredRoot)
    {
        _rootFullPath = SafeMediaPath.ResolveRoot(configuredRoot);
    }

    /// <summary>媒体条目（列表下发形态，PascalCase 由 ResultFilter 原样序列化）。</summary>
    public sealed class MediaItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public long Size { get; set; }
        public string Ext { get; set; }
        public DateTime Mtime { get; set; }
    }

    /// <summary>列表结果（Items 为当页条目）。</summary>
    public sealed class MediaListResult
    {
        public List<MediaItem> Items { get; set; } = new();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    private sealed record MediaMeta(string Title, string Artist);

    /// <summary>
    /// ctx.File 门面落盘成功后同进程调用：立即可见（脚本下载完推气泡 → 用户立刻开媒体库无 30s 盲区）。
    /// </summary>
    public static void InvalidateScanCache()
    {
        // 原子替换为空快照（volatile 写）：下一次读取重新扫描，保证「新落盘文件立即可见」——此为刻意的零盲区保证，不做惰性标记。
        _scanCache = new(null, new(), DateTime.MinValue);
    }

    /// <summary>
    /// 媒体列表：mtime 倒序；keyword 匹配文件名（包含，忽略大小写）；dir 限定一级子目录（缺省全部）。
    /// </summary>
    public async Task<MediaListResult> ListAsync(int page, int pageSize, string keyword, string dir)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200);

        var items = await ScanAsync();
        if (!string.IsNullOrWhiteSpace(dir))
        {
            // 子目录过滤本身就是受控参数：直接复用文件路径校验的段规则，非法 dir 一律回空结果
            var prefix = dir.Trim().Replace('\\', '/').TrimEnd('/') + "/";
            items = items.Where(i => i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            items = items.Where(i => i.Name.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }
        items = items.OrderByDescending(i => i.Mtime).ToList();
        return new MediaListResult
        {
            Items = items.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Total = items.Count,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 解析可播文件：路径防穿越 + 扩展名白名单 + 存在性校验。失败统一 false（调用方直出 404，不区分原因）。
    /// </summary>
    public bool TryGetFile(string path, out string absolutePath, out string contentType, out DateTime lastModified, out long size)
    {
        absolutePath = null;
        contentType = null;
        lastModified = default;
        size = 0;
        if (!SafeMediaPath.TryResolveFile(_rootFullPath, path, out absolutePath))
        {
            return false;
        }
        var ext = Path.GetExtension(absolutePath);
        if (!PlayableExtensions.Contains(ext) || !ContentTypeMap.TryGetValue(ext, out contentType))
        {
            return false;
        }
        var info = new FileInfo(absolutePath);
        if (!info.Exists)
        {
            return false;
        }
        lastModified = info.LastWriteTimeUtc;
        size = info.Length;
        return true;
    }

    /// <summary>读取内嵌封面（ID3v2/FLAC VorbisComment picture）：无封面或不可解析返回 false。</summary>
    public bool TryGetCover(string path, out byte[] cover, out string mime)
    {
        cover = null;
        mime = null;
        if (!TryGetFile(path, out var absolutePath, out _, out var mtime, out _))
        {
            return false;
        }
        var cacheKey = $"{absolutePath}|{mtime.Ticks}";
        var cached = CoverCache.GetOrAdd(cacheKey, _ => LoadCover(absolutePath));
        if (CoverCache.Count > MaxCoverCacheEntries)
        {
            var victim = CoverCache.Keys.FirstOrDefault(k => k != cacheKey);
            if (victim != null)
            {
                CoverCache.TryRemove(victim, out _);
            }
        }
        if (cached.Cover == null || cached.Cover.Length == 0)
        {
            return false;
        }
        cover = cached.Cover;
        mime = cached.Mime;
        return true;
    }

    private static (byte[] Cover, string Mime) LoadCover(string absolutePath)
    {
        try
        {
            using var tagFile = TagLibFile.Create(absolutePath);
            var picture = tagFile.Tag.Pictures.FirstOrDefault(p => p.Data.Count > 0);
            if (picture == null)
            {
                return (null, null);
            }
            var mime = string.IsNullOrWhiteSpace(picture.MimeType) ? "image/jpeg" : picture.MimeType;
            return (picture.Data.Data, mime);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// 取当前扫描结果（异步单飞）：命中新鲜缓存直接返回；否则进 <see cref="ScanGate"/>，双重检查后由一个请求执行扫描、
    /// 并发冷请求异步 await 同一趟扫描（不各自占用线程池线程）。扫描完成前不返回空态——保留「新落盘文件立即可见」保证。
    /// </summary>
    private async Task<List<MediaItem>> ScanAsync()
    {
        var snapshot = _scanCache;
        if (IsFresh(snapshot))
        {
            return snapshot.Items;
        }
        await ScanGate.WaitAsync();
        try
        {
            snapshot = _scanCache;
            if (IsFresh(snapshot))
            {
                return snapshot.Items;
            }
            var items = BuildScan();
            _scanCache = new ScanSnapshot(_rootFullPath, items, DateTime.UtcNow);
            return items;
        }
        finally
        {
            ScanGate.Release();
        }
    }

    /// <summary>缓存是否对当前根目录仍新鲜（沿用旧 GetScan 判定：有内容 + 根一致 + 未过期）。</summary>
    private bool IsFresh(ScanSnapshot snapshot) =>
        snapshot.Items.Count > 0
        && string.Equals(snapshot.Root, _rootFullPath, StringComparison.OrdinalIgnoreCase)
        && DateTime.UtcNow - snapshot.At < TimeSpan.FromSeconds(ScanCacheSeconds);

    /// <summary>实际目录扫描（仅在 ScanGate 单飞保护下调用，故内部无锁）：枚举白名单媒体、填充 MetaCache、顺带清孤儿键。</summary>
    private List<MediaItem> BuildScan()
    {
        var items = new List<MediaItem>();
        var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_rootFullPath))
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            };
            foreach (var file in Directory.EnumerateFiles(_rootFullPath, "*", options))
            {
                var info = new FileInfo(file);
                var ext = info.Extension;
                if (!PlayableExtensions.Contains(ext))
                {
                    continue;
                }
                currentPaths.Add(file);
                var meta = MetaCache.GetOrAdd($"{file}|{info.LastWriteTimeUtc.Ticks}",
                    _ => LoadMeta(file, Path.GetFileNameWithoutExtension(file)));
                items.Add(new MediaItem
                {
                    Name = info.Name,
                    Path = Path.GetRelativePath(_rootFullPath, file).Replace('\\', '/'),
                    Title = meta.Title,
                    Artist = meta.Artist,
                    Size = info.Length,
                    Ext = ext.ToLowerInvariant().TrimStart('.'),
                    Mtime = info.LastWriteTimeUtc
                });
            }
        }
        // 扫描顺带清孤儿键：缓存键含 mtime，文件被覆盖（新 mtime）/删除后旧键永不再命中，不清则只增不减
        foreach (var key in MetaCache.Keys)
        {
            if (!currentPaths.Contains(PathPartOfCacheKey(key)))
            {
                MetaCache.TryRemove(key, out _);
            }
        }
        foreach (var key in CoverCache.Keys)
        {
            if (!currentPaths.Contains(PathPartOfCacheKey(key)))
            {
                CoverCache.TryRemove(key, out _);
            }
        }
        return items;
    }

    /// <summary>缓存键「绝对路径|mtimeTicks」取路径段（Windows 路径不会含 '|'，按最后一个切分安全）。</summary>
    private static string PathPartOfCacheKey(string key)
    {
        var index = key.LastIndexOf('|');
        return index > 0 ? key[..index] : key;
    }

    /// <summary>ID3/VorbisComment 元数据；解析失败回退文件名「歌手 - 歌名」拆分。</summary>
    private static MediaMeta LoadMeta(string absolutePath, string nameWithoutExt)
    {
        MediaMeta meta = null;
        try
        {
            using var tagFile = TagLibFile.Create(absolutePath);
            var title = tagFile.Tag.Title?.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                var artist = string.Join(" / ", tagFile.Tag.Performers
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim()));
                meta = new MediaMeta(title, string.IsNullOrEmpty(artist) ? null : artist);
            }
        }
        catch
        {
            // 标签解析失败走文件名回退
        }
        return meta ?? ParseFromFileName(nameWithoutExt);
    }

    /// <summary>文件名回退：「歌手 - 歌名」（音乐脚本落盘命名），无分隔符时整体作歌名。</summary>
    private static MediaMeta ParseFromFileName(string nameWithoutExt)
    {
        var separator = nameWithoutExt.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0 && separator < nameWithoutExt.Length - 3)
        {
            var artist = nameWithoutExt[..separator].Trim();
            var title = nameWithoutExt[(separator + 3)..].Trim();
            if (artist.Length > 0 && title.Length > 0)
            {
                return new MediaMeta(title, artist);
            }
        }
        return new MediaMeta(nameWithoutExt, null);
    }
}
