// ============================================================================
// 保存媒体（2026-09-18 会话窗口批次）：会话里试听/看了脚本推送的外链媒体后，
// 点气泡上的「保存」按钮 → App 发「保存媒体 <url>」→ 本脚本经 ctx.File 落盘到
// saved/ 子目录 → 回推 AppMedia 服务端地址气泡（无时效，可日后随时重播）。
// 与 music_search 的「保存N」互补：那是按最近搜索结果重新取链落盘（直链新鲜），
// 本脚本直接下载当前气泡里的地址（对试听后立即保存的场景直链通常仍有效）。
//
// 推荐任务配置：
//   1) 触发指令填 ^保存媒体.* 并勾选正则；指令环境变量名填 media_save_msg。
//   2) App 会话里任意 http(s) 媒体气泡点「保存」即触发（音频/视频气泡均有按钮），
//      不带名字参数——按类型落默认目录（音频 music/、视频 video/、其它 saved/）。
//   3) 手动可带名字参数（文件不好管理的解法）：「保存媒体 <url> 我的歌」指定文件名，
//      「保存媒体 <url> music2/我的歌.flac」连子目录一起指定（自带可播扩展名则尊重）。
//   4) Web 手动调试：配变量 scripts_media_save=<url>（名字参数同样支持：<url> 空格 名字）。
//   5) 可选变量：
//      scripts_media_save_dir  保存子目录（整体覆盖默认目录；相对下载根目录的相对路径）
//      scripts_media_max_mb    大小上限 MB（默认 2048；Content-Length 缺失时不拦截）
//
// 保存目录优先级（2026-09-19 增）：名字参数内嵌「子目录/」> env scripts_media_save_dir >
//   按类型默认（音频 music / 视频 video / 其它 saved）——音乐气泡直存不再落进 saved 混杂目录。
//   注意：音乐 CDN 直链（如网易 126.net）URL 是 hash 无歌名——音乐脚本推试听气泡时会记录
//   「直链 → 歌名」映射（music_url_name，7 天过期），此处保存前反查，命中即得「歌手 - 歌名.mp3」；
//   非音乐直链或映射过期则回退 URL 尾段推断，也可手动带名字参数。
//
// 落盘：经 ctx.File 受控文件门面（防穿越/文件名清洗/重名不覆盖）；
//   扩展名判定优先响应 Content-Type（外链常无扩展名），回退 URL 尾段，再兜底音视频默认值。
// ============================================================================
using Quantum.Plugins;

public class MediaSaverTask : IQuantumTask
{
    private const string DefaultSaveDir = "saved";
    private const int DefaultMaxMb = 2048;
    private const string UrlNameType = "music_url_name";

    /// <summary>可播扩展名 → 回推气泡类型（与平台 AppMedia 白名单一致）。</summary>
    private static readonly Dictionary<string, string> AudioExts = new()
    {
        [".mp3"] = "audio", [".flac"] = "audio", [".m4a"] = "audio", [".aac"] = "audio", [".wav"] = "audio"
    };

    private static readonly Dictionary<string, string> VideoExts = new()
    {
        [".mp4"] = "video", [".mkv"] = "video", [".webm"] = "video"
    };

    /// <summary>响应 Content-Type → 扩展名（外链直链常无扩展名，以服务端声明为准）。</summary>
    private static readonly Dictionary<string, string> MimeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ".mp3",
        ["audio/flac"] = ".flac",
        ["audio/mp4"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["video/mp4"] = ".mp4",
        ["video/x-matroska"] = ".mkv",
        ["video/webm"] = ".webm"
    };

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var input = Var(ctx, "media_save_msg", "command", "scripts_media_save");
        var url = ExtractUrl(input);
        if (url == null)
        {
            ctx.Log($"输入[{input}]未解析出 http(s) 媒体地址。App 会话媒体气泡点「保存」触发；" +
                    "手动调试配变量 scripts_media_save=<url>。");
            return;
        }

        // 先探响应头：Content-Type 定扩展名（直链常无扩展名），Content-Length 做大小预检
        string contentType = null;
        long? length = null;
        try
        {
            using var probe = await ctx.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!probe.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP {(int)probe.StatusCode}（直链可能已过期，可回源重新获取）");
            }
            contentType = probe.Content?.Headers?.ContentType?.MediaType;
            length = probe.Content?.Headers?.ContentLength;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            await NotifyAsync(ctx, "保存媒体失败", $"地址探测失败：{e.Message}\r\n{url}", ct);
            return;
        }

        var maxMb = ReadInt(ctx, "scripts_media_max_mb", DefaultMaxMb, 1, 1024 * 1024);
        if (length is > 0 && length / 1024.0 / 1024.0 > maxMb)
        {
            await NotifyAsync(ctx, "保存媒体失败",
                $"文件太大（{length / 1024.0 / 1024.0:F0}MB > 上限 {maxMb}MB）。\r\n{url}", ct);
            return;
        }

        // 可选名字参数：URL 之后的剩余文本，形如「保存媒体 <url> 我的歌」或「保存媒体 <url> music2/我的歌」
        //（子目录/名字 连保存目录一起指定；App 气泡「保存」按钮不带名字，走类型默认目录）
        var nameArg = ExtractNameArg(input, url);

        // 文件名：名字参数优先（自带可播扩展名则尊重）；无名字时 URL 尾段优先；
        // 扩展名不可播时用 Content-Type 推断；仍无则音/视频默认值
        var path = url.Split('?')[0].TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var rawName = slash >= 0 ? path[(slash + 1)..] : path;
        var ext = ExtOf(rawName);
        if (!AudioExts.ContainsKey(ext) && !VideoExts.ContainsKey(ext))
        {
            ext = "";
        }
        if (ext.Length == 0 && contentType != null && MimeExts.TryGetValue(contentType, out var mimeExt))
        {
            ext = mimeExt;
        }
        if (ext.Length == 0 && contentType != null)
        {
            ext = contentType.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".mp4";
        }
        if (ext.Length == 0)
        {
            ext = ".mp4";
        }
        string stem;
        string dirFromName = null;
        if (!string.IsNullOrWhiteSpace(nameArg))
        {
            // 名字参数：最后一个 / 前是子目录、之后是主干（主干自带可播扩展名则原样用）
            var norm = nameArg.Trim().Replace('\\', '/');
            var nameSlash = norm.LastIndexOf('/');
            dirFromName = nameSlash >= 0 ? norm[..nameSlash].Trim() : null;
            stem = nameSlash >= 0 ? norm[(nameSlash + 1)..].Trim() : norm.Trim();
            var stemExt = ExtOf(stem);
            if (stemExt.Length > 0 && (AudioExts.ContainsKey(stemExt) || VideoExts.ContainsKey(stemExt)))
            {
                stem = stem[..^stemExt.Length];
                ext = stemExt;
            }
        }
        else
        {
            // 无名字参数：先查音乐脚本推气泡时记录的「直链 → 歌名」映射（App 气泡「保存」按钮场景，
            // CDN 直链文件名是 hash 无从辨认），命中即「歌手 - 歌名」；未命中走 URL 尾段推断
            var songName = await TrySongNameByUrlAsync(ctx, url, ct);
            if (songName != null)
            {
                stem = songName;
            }
            else
            {
                var stemDot = rawName.LastIndexOf('.');
                stem = stemDot > 0 ? rawName[..stemDot] : rawName;
            }
        }
        if (stem.Length == 0)
        {
            stem = "media_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }
        if (stem.Length > 100)
        {
            stem = stem.Substring(0, 100);
        }
        var fileName = stem + ext;

        // 保存目录优先级：名字参数内嵌子目录 > env scripts_media_save_dir > 按类型默认
        //（音频 music / 视频 video / 其它 saved——音乐气泡直存不再落进 saved 混杂目录）
        string saveDir;
        if (!string.IsNullOrWhiteSpace(dirFromName))
        {
            saveDir = dirFromName;
        }
        else if (string.IsNullOrWhiteSpace(Var(ctx, "scripts_media_save_dir")))
        {
            saveDir = AudioExts.ContainsKey(ext) ? "music" : (VideoExts.ContainsKey(ext) ? "video" : DefaultSaveDir);
        }
        else
        {
            saveDir = Var(ctx, "scripts_media_save_dir").Trim();
        }

        try
        {
            var file = await ctx.File.DownloadAsync(url, fileName, saveDir, ct);
            ctx.Log($"已保存：{file.FullPath}，{file.Length} 字节。");

            // 回推服务端地址气泡（无时效）：音频走 audio 气泡、视频走 video 气泡，App 端内即点即播
            if (ctx.EnablePush)
            {
                var serverUrl = "api/AppMedia/file?path=" + System.Uri.EscapeDataString(file.RelativePath);
                var dot = file.FileName.LastIndexOf('.');
                var caption = dot > 0 ? file.FileName[..dot] : file.FileName;
                if (AudioExts.ContainsKey(ext))
                {
                    await ctx.Notify.SendAudioAsync(serverUrl, caption, ct);
                }
                else
                {
                    await ctx.Notify.SendVideoAsync(serverUrl, caption, ct: ct);
                }
            }
            var sizeMb = file.Length / 1024.0 / 1024.0;
            await NotifyAsync(ctx, "媒体已保存", $"✓ {file.FileName}（{sizeMb:F1}MB）→ {file.FullPath}\r\n可播放气泡已回推。", ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            await NotifyAsync(ctx, "保存媒体失败", $"{fileName}：{e.Message}\r\n{url}", ct);
        }
    }

    // ------------------------------------------------------------------ 工具
    /// <summary>音乐脚本（music_search/music_daily）推试听气泡时记录的「直链 → 歌名」映射反查：
    /// App 音频气泡「保存」按钮只发 URL，CDN 直链（如网易 126.net）文件名是 hash 无从辨认；
    /// 命中返回「歌手 - 歌名」，未命中/查询失败返回 null 走 URL 尾段推断（不影响保存主流程）。</summary>
    private static async Task<string> TrySongNameByUrlAsync(QuantumTaskContext ctx, string url, CancellationToken ct)
    {
        try
        {
            var hit = await ctx.CustomData.QueryAsync(UrlNameType, url, ct: ct);
            var row = hit.FirstOrDefault();
            if (row?.Data2 is { Length: > 0 })
            {
                var artist = string.IsNullOrWhiteSpace(row.Data3) ? "" : row.Data3 + " - ";
                ctx.Log($"直链映射命中：{row.Data2}（音乐脚本试听气泡记录）。");
                return artist + row.Data2;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"直链→歌名映射反查失败（不影响保存）：{e.Message}");
        }
        return null;
    }

    /// <summary>取消息里第一个 http(s) 地址（App 发「保存媒体 <url>」，url 是独立 token）。</summary>
    private static string ExtractUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }
        foreach (var token in input.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return token.TrimEnd('.', '，', ',');
            }
        }
        return null;
    }

    /// <summary>URL 之后的剩余文本 = 可选名字参数（可含「子目录/」前缀；自带可播扩展名则尊重）。
    /// 「保存媒体 <url> 我的歌」「保存媒体 <url> music2/我的歌.flac」；无剩余返回 null。
    /// 名字中的非法文件字符与路径穿越由 ctx.File 门面清洗/拒绝，脚本侧不重复处理。</summary>
    private static string ExtractNameArg(string input, string url)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        var idx = input.IndexOf(url, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }
        var rest = input[(idx + url.Length)..].Trim('\r', '\n', ' ', '\t', '.', '，', ',');
        return rest.Length == 0 ? null : rest;
    }

    private static string ExtOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && name.Length - dot <= 6 ? name[dot..].ToLowerInvariant() : "";
    }

    private static async Task NotifyAsync(QuantumTaskContext ctx, string title, string content, CancellationToken ct)
    {
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendAsync(title, content, ct);
        }
    }

    private static int ReadInt(QuantumTaskContext ctx, string name, int fallback, int min, int max)
    {
        if (ctx.Variables.TryGetValue(name, out var text) && int.TryParse(text?.Trim(), out var value))
        {
            return Math.Clamp(value, min, max);
        }
        return fallback;
    }

    private static string Var(QuantumTaskContext ctx, params string[] names)
    {
        foreach (var name in names)
        {
            if (ctx.Variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }
}
