// ============================================================================
// 每日热歌推送（2026-09-19 新写）：定时抓网易云榜单前 N 首（默认 10）逐曲取链推
// 音频气泡（点开即听）。定位 = music_search 的定时榜单伴生脚本：搜索交互（音乐
// 搜索/序号/保存N）仍归 music_search，本脚本只做每日定点推送。
//
// 数据链路（全部复用 music_search 已验证的通道）：
//   榜单 = 网易云公开歌单接口 music.163.com/api/playlist/detail（免鉴权，实测 2026-09-19
//          可用；老榜单接口 /api/top/list 已 404 弃用）。接口恒返回全量 200 首（&n= 不生效，
//          约 620KB），解析后取前 N 首；
//   取链 = lxserver 优先（scripts_music_lx_url）→ GD 节点域回落（仅 wy id）→ 跨源兜底
//          （tx→kw→mg→kg 同名曲），与 music_search.ResolveSongUrlAsync 同逻辑；
//   探测 = 推链前 Range 0-1 只看响应头，拦截风控 JSON 错误页/过期直链。
//
// 推送形态（2026-09-27 修订，修音频一小时失效）：音源 CDN 直链时效约 1 小时（7 点推的
//   气泡 8 点点开即 403 失效），不再直推直链。改为逐曲先经 ctx.File 落盘到 music/ 子目录
//   （下载根目录内，无时效），气泡推服务端地址 api/AppMedia/file?path=<相对路径>
//   （App 端内播放、随时可重播；同 music_search.NotifyMediaAsync / media_saver 回推链路）。
//   不推榜单列表文字回执与逐首「保存」点选；仅取链/探测/落盘失败时推失败明细文字。
//   直链→歌名映射（music_url_name，7 天过期）仍逐首照写，点旧直链气泡「保存」可反查直存。
//
// 推荐任务配置：定时 Cron 0 0 8 * * ?（每天 08:00，Quartz 六段式，按服务器本地时区，
//   Docker 部署确认容器时区 Asia/Shanghai），开启推送即可；无需指令触发。
//   可选：配 scripts_music_daily_test=1 跳过「同一天已推过」防重（见下），便于手动调试。
//
// 环境变量（全部可选）：
//   scripts_music_daily_rank    榜单：hot=云音乐热歌榜（默认）/ new=新歌榜 / soar=飙升榜 /
//                               original=原创榜，亦可直填网易数字歌单 ID（如 60198）
//   scripts_music_daily_count   推送条数（默认 10，1-20）
//   scripts_music_daily_test    =1 时跳过当日防重检查（手动重推用；防重记录见下）
//   scripts_music_br            音质 128/192/320/740/999（复用 music_search 同名变量，默认 320）
//   scripts_music_lx_url        lxserver 基地址（复用同名变量；不配则走 GD 直取，VIP 曲易空链）
//
// 防重：每日一次由 Cron 保证；为防手动执行/误触发造成一天连推多轮打扰，默认记录
//   CustomData（类型 music_daily_push_log，Data1=日期）当日已推即跳过，配
//   scripts_music_daily_test=1 或换榜单 ID（Data2）可绕过。
// 迁移映射与取舍同 music_search（2026-09-18）：axios→ctx.Http、JSON.parse→Newtonsoft、
//   console.log→ctx.Log、sendNotify→ctx.Notify。
// ============================================================================
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class MusicDailyTask : IQuantumTask
{
    private const string PlaylistApi = "https://music.163.com/api/playlist/detail";
    private const string PushLogType = "music_daily_push_log";
    private const string UrlNameType = "music_url_name";
    private const string SaveDir = "music";
    private const int DefaultCount = 10;
    private const int DefaultBr = 320;

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var rankRaw = (Var(ctx, "scripts_music_daily_rank") ?? "hot").Trim();
        var rankId = ResolveRankId(rankRaw);
        var count = ReadInt(ctx, "scripts_music_daily_count", DefaultCount, 1, 20);
        var br = ReadInt(ctx, "scripts_music_br", DefaultBr, 128, 999);
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        // 当日防重：music_daily_push_log 已有当日+该榜单记录则跳过（防手动误触/一天连推）；
        // scripts_music_daily_test=1 跳过检查便于调试重推
        if (!IsTrue(Var(ctx, "scripts_music_daily_test")))
        {
            var pushed = await ctx.CustomData.QueryAsync(PushLogType, today, rankId, ct: ct);
            if (pushed.Count > 0)
            {
                ctx.Log($"今日（{today}）榜单 {rankId} 已推送过，跳过；重推请配 scripts_music_daily_test=1。");
                return;
            }
        }

        List<Song> songs;
        string chartName;
        try
        {
            (songs, chartName) = await LoadChartAsync(ctx, rankId, count, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"榜单获取失败：{e.Message}");
            await NotifyAsync(ctx, "每日热歌推送失败", $"榜单 {rankId} 获取异常：{e.Message}", ct);
            return;
        }
        if (songs.Count == 0)
        {
            ctx.Log($"榜单 {rankId} 未解析出曲目。");
            await NotifyAsync(ctx, "每日热歌推送失败", $"榜单 {rankId} 返回空曲目，请稍后重试或检查接口。", ct);
            return;
        }

        var header = $"🎵 每日热歌 · {DateTime.Now.Month}月{DateTime.Now.Day}日 {chartName}（{songs.Count} 首 / 音质 {BrLabel(br)}）";
        var listText = string.Join("\r\n", songs.Select((s, i) => $"{i + 1}. {s.Name} - {s.Artist}{(string.IsNullOrWhiteSpace(s.Album) ? "" : $"《{s.Album}》")} {s.Interval}"));
        ctx.Log(header + "\r\n" + listText);

        // 推送关闭（任务级 EnablePush=false）时只落日志
        if (!ctx.EnablePush)
        {
            ctx.Log("[推送关闭] 未发音频气泡。");
            await MarkPushedAsync(ctx, today, rankId, ct);
            return;
        }

        // 逐曲取链 → 探测 → 落盘 → 服务端地址音频气泡（caption 带「N. 歌名 - 歌手」序号前缀）；
        // 直链时效约 1 小时不能直推，必须先落盘为无时效文件再推 api/AppMedia 相对地址。
        // 全部成功即完全静默——不推列表文字回执（音频已落盘随时可重播，无需再保存），
        // 仅失败/无法推出时推失败明细
        var lxUrl = NormalizeBaseUrl(Var(ctx, "scripts_music_lx_url"));
        var results = new List<string>();
        // 直链→歌曲信息映射（2026-09-20 改逐首「先写映射再推气泡」）：App 气泡「保存」按钮只发 URL、
        // CDN 直链是 hash 无歌名，media_saver 靠 music_url_name 反查得「歌手 - 歌名.mp3」；整轮推完
        // 才写映射的话，用户秒点保存早于映射写入，反查落空只得 hash 文件名——逐首先写彻底消除
        await CleanStaleUrlNamesAsync(ctx, ct);
        var pushedCount = 0;
        foreach (var (song, i) in songs.Select((s, idx) => (s, idx)))
        {
            ct.ThrowIfCancellationRequested();
            var (url, _, error) = await ResolveSongUrlAsync(ctx, song, br, lxUrl, ct);
            if (string.IsNullOrEmpty(url))
            {
                var reason = error == null
                    ? "当前源无可用直链（版权/VIP 限制）"
                    : $"{error.Message}——音源接口暂不可用";
                results.Add($"{QuantumText.Color("red", "✗")} {i + 1}. {SongLabel(song)}：{reason}");
                continue;
            }
            var probe = await ProbePlayableAsync(ctx, url, ct);
            if (!probe.Ok)
            {
                results.Add($"{QuantumText.Color("red", "✗")} {i + 1}. {SongLabel(song)}：直链不可用（{probe.Reason}）");
                continue;
            }
            // 落盘再推服务端地址：直链约 1 小时过期，下载到 music/ 后文件无时效，
            // 气泡点开走 App 端内播放（api/AppMedia/file），任何时候点都还能听
            try
            {
                var fileName = BuildFileName(song, url, br);
                var file = await ctx.File.DownloadAsync(url, fileName, SaveDir, ct);
                // 先写映射再推气泡：旧直链气泡点「保存」时 media_saver 反查必已能命中
                await TrySaveUrlNameAsync(ctx, url, song, ct);
                await NotifyMediaAsync(ctx, file.RelativePath, $"{i + 1}. {SongLabel(song)}", ct);
                pushedCount++;
                ctx.Log($"已落盘并推送：{fileName}（{file.Length / 1024.0 / 1024.0:F1}MB）→ {file.RelativePath}。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                results.Add($"{QuantumText.Color("red", "✗")} {i + 1}. {SongLabel(song)}：下载落盘失败（{e.Message}）");
            }
        }

        // 全部成功即完全静默（与 music_search 搜索直推一致）；有失败时推失败明细，
        // 让用户知道榜单里哪些没出来
        if (results.Count > 0)
        {
            var title = pushedCount == songs.Count
                ? "每日热歌推送"
                : $"每日热歌推送（{pushedCount}/{songs.Count}）";
            var text = string.Join("\r\n", new[] { header }
                .Concat(results.Count > 0 ? results : Array.Empty<string>()));
            await ctx.Notify.SendAsync(title, text, ct);
        }

        ctx.Log($"每日热歌推送完成：气泡 {pushedCount}/{songs.Count}，榜单 {chartName}（{rankId}）。");
        await MarkPushedAsync(ctx, today, rankId, ct);
    }

    // ------------------------------------------------------------------ 榜单
    /// <summary>拉网易歌单榜单并取前 count 首。返回 (曲目列表, 榜单名)。</summary>
    private static async Task<(List<Song> Songs, string ChartName)> LoadChartAsync(
        QuantumTaskContext ctx, string rankId, int count, CancellationToken ct)
    {
        // 带 UA/Referer 与实测条件一致（网易部分接口裸 UA 会风控）；接口恒回全量约 620KB
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PlaylistApi}?id={Uri.EscapeDataString(rankId)}");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
        using var response = await ctx.Http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"网易歌单接口 HTTP {(int)response.StatusCode}");
        }
        var root = JObject.Parse(json);
        var pl = root["result"];
        var tracks = pl?["tracks"] as JArray ?? new JArray();
        var songs = new List<Song>();
        foreach (var item in tracks)
        {
            var id = item["id"]?.ToString();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }
            songs.Add(new Song(
                id,
                item["name"]?.ToString() ?? "",
                JoinArtist(item["artists"]),
                item["album"]?["name"]?.ToString() ?? "",
                FormatDuration(item["duration"]?.ToObject<long?>() ?? null),
                "wy"));
            if (songs.Count >= count)
            {
                break;
            }
        }
        var chartName = pl?["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(chartName))
        {
            chartName = "榜单" + rankId;
        }
        return (songs, chartName);
    }

    /// <summary>防重记录：当日+榜单各写一行（Data1=日期 / Data2=榜单ID），失败不影响推送主流程。</summary>
    private static async Task MarkPushedAsync(QuantumTaskContext ctx, string today, string rankId, CancellationToken ct)
    {
        try
        {
            await ctx.CustomData.AddAsync(new List<QuantumCustomDataValue>
            {
                new() { Type = PushLogType, Data1 = today, Data2 = rankId }
            }, ct);
        }
        catch (Exception e)
        {
            ctx.Log($"防重记录写入失败（不影响本次推送）：{e.Message}");
        }
    }

    /// <summary>清理 7 天前旧映射（直链本身短命）；失败只落日志不影响推送。</summary>
    private static async Task CleanStaleUrlNamesAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            var stale = await ctx.CustomData.QueryAsync(UrlNameType,
                new QuantumCustomDataFilter { CreateTimeEnd = DateTime.Now.AddDays(-7) }, ct);
            if (stale.Count > 0)
            {
                await ctx.CustomData.DeleteAsync(stale.Select(n => n.Id).ToList(), ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"旧映射清理失败（不影响推送）：{e.Message}");
        }
    }

    /// <summary>单首映射写入（Data1=直链 Data2=歌名 Data3=歌手 Data4=专辑）：逐首在推气泡**前**调用，
    /// 保证气泡可点「保存」的任意时刻 media_saver 反查必中；失败只落日志不影响推送。</summary>
    private static async Task TrySaveUrlNameAsync(QuantumTaskContext ctx, string url, Song song, CancellationToken ct)
    {
        try
        {
            await ctx.CustomData.AddAsync(new List<QuantumCustomDataValue>
            {
                new QuantumCustomDataValue
                {
                    Type = UrlNameType, Data1 = url, Data2 = song.Name, Data3 = song.Artist, Data4 = song.Album
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"直链→歌名映射写入失败（不影响本次推送）：{e.Message}");
        }
    }

    /// <summary>榜单别名 → 网易歌单 ID；数字直填；其它垃圾值回热歌榜。</summary>
    private static string ResolveRankId(string raw)
    {
        var v = string.IsNullOrWhiteSpace(raw) ? "hot" : raw.Trim().ToLowerInvariant();
        return v switch
        {
            "hot" => "3778678",
            "new" => "3779629",
            "soar" => "19723756",
            "original" => "2884035",
            _ => v.Length > 0 && v.All(char.IsDigit) ? v : "3778678"
        };
    }

    // ------------------------------------------------------------------ 取链（与 music_search 同逻辑，单文件内联）
    private sealed record Song(string Id, string Name, string Artist, string Album, string Interval, string Source);

    private static string SongLabel(Song song)
    {
        var artist = string.IsNullOrWhiteSpace(song.Artist) ? "" : $" - {song.Artist}";
        return $"{song.Name}{artist}";
    }

    /// <summary>取链：lxserver 优先 → GD 节点域回落（仅 wy）→ 跨源兜底（tx→kw→mg→kg）。返回 (直链, via, 异常)。</summary>
    private static async Task<(string Url, string Via, Exception Error)> ResolveSongUrlAsync(
        QuantumTaskContext ctx, Song song, int br, string lxUrl, CancellationToken ct)
    {
        string url = null;
        string via = null;
        Exception lastError = null;
        if (lxUrl != null)
        {
            try
            {
                url = await ResolveUrlViaLxAsync(ctx, lxUrl, song, br, ct);
                via = "lx";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                lastError = e;
                ctx.Log($"lxserver 取链失败（{song.Name}）：{e.Message}，尝试回落。");
            }
        }
        if (string.IsNullOrEmpty(url) && song.Source == "wy")
        {
            try
            {
                url = await ResolveUrlViaGdAsync(ctx, song.Id, br, ct);
                via = "gd";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                lastError = e;
                ctx.Log($"GD 取链失败（{song.Name}）：{e.Message}");
            }
        }
        if (lxUrl != null && string.IsNullOrEmpty(url))
        {
            var (crossUrl, crossVia) = await ResolveViaLxCrossAsync(ctx, lxUrl, song, br, ct);
            if (!string.IsNullOrEmpty(crossUrl))
            {
                url = crossUrl;
                via = crossVia;
                lastError = null;
            }
        }
        return (url, via, lastError);
    }

    /// <summary>跨源兜底（lxserver 模式）：备用源搜同名曲、校验歌名后取链。顺序 tx→kw→mg→kg。</summary>
    private static async Task<(string Url, string Via)> ResolveViaLxCrossAsync(
        QuantumTaskContext ctx, string lxUrl, Song song, int br, CancellationToken ct)
    {
        foreach (var src in new[] { "tx", "kw", "mg", "kg" })
        {
            if (src == song.Source)
            {
                continue;
            }
            try
            {
                var query = Uri.EscapeDataString($"{song.Name} {song.Artist}".Trim());
                var json = await ctx.Http.GetStringAsync(
                    $"{lxUrl}/api/music/search?name={query}&source={src}&page=1&count=1", ct);
                var list = JArray.Parse(json);
                if (list.Count == 0)
                {
                    continue;
                }
                var hit = list[0];
                var name = hit["name"]?.ToString();
                if (string.IsNullOrEmpty(name) || !name.Contains(song.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var mid = hit["songmid"]?.ToString();
                if (string.IsNullOrEmpty(mid))
                {
                    continue;
                }
                var singer = hit["singer"]?.ToString() ?? "";
                var url = await ResolveUrlViaLxAsync(ctx, lxUrl, new Song(mid, name, singer, "", "", src), br, ct);
                if (!string.IsNullOrEmpty(url))
                {
                    ctx.Log($"跨源兜底命中：{song.Name} 由 {song.Source} 切 {src} 出链。");
                    return (url, $"lx:{src}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单个备用源失败继续尝试下一个
            }
        }
        return (null, null);
    }

    /// <summary>推链前探测直链真伪（Range 0-1 只看响应头）：拦截音源风控 JSON 错误页与过期直链。</summary>
    private static async Task<(bool Ok, string Reason)> ProbePlayableAsync(QuantumTaskContext ctx, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
            using var response = await ctx.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)response.StatusCode}");
            }
            var mime = response.Content?.Headers?.ContentType?.MediaType ?? "";
            if (mime.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                mime.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                mime.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"内容类型 {mime}（疑似风控/错误页）");
            }
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    private const string GdUrlApi = "https://music-api.gdstudio.xyz/api.php";

    /// <summary>GD 音乐台节点域取链：POST api.php（types=url&source=netease，s 为标准 CRC32 十六进制）。</summary>
    private static async Task<string> ResolveUrlViaGdAsync(QuantumTaskContext ctx, string songId, int br, CancellationToken ct)
    {
        var body = $"types=url&id={Uri.EscapeDataString(songId)}&source=netease&br={br}&s={Crc32Hex(songId)}";
        using var content = new StringContent(body, null, "application/x-www-form-urlencoded");
        using var response = await ctx.Http.PostAsync(GdUrlApi, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"GD 接口 HTTP {(int)response.StatusCode}");
        }
        var root = JObject.Parse(json);
        return root["url"]?.ToString();
    }

    /// <summary>lxserver 取链：POST /api/music/url（songInfo+quality；需 lxserver 已导入自定义音源脚本）。</summary>
    private static async Task<string> ResolveUrlViaLxAsync(QuantumTaskContext ctx, string lxUrl, Song song, int br, CancellationToken ct)
    {
        var quality = br switch
        {
            128 => "128k",
            192 => "192k",
            320 => "320k",
            740 => "flac",
            _ => "flac24bit"
        };
        var payload = new JObject
        {
            ["songInfo"] = new JObject
            {
                ["source"] = song.Source,
                ["songmid"] = song.Id,
                ["name"] = song.Name,
                ["singer"] = song.Artist
            },
            ["quality"] = quality
        };
        using var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), null, "application/json");
        using var response = await ctx.Http.PostAsync($"{lxUrl}/api/music/url", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = JObject.Parse(json)["error"]?.ToString();
            throw new Exception(string.IsNullOrWhiteSpace(err) ? $"lxserver HTTP {(int)response.StatusCode}" : err);
        }
        return JObject.Parse(json)["url"]?.ToString();
    }

    // ------------------------------------------------------------------ 工具（与 music_search 同逻辑）
    /// <summary>歌手字段兼容三态：字符串（lx kw/kg/mg）、对象数组（网易 artists[{name}]）、字符串数组（lx wy）。</summary>
    private static string JoinArtist(JToken singer)
    {
        if (singer == null)
        {
            return "";
        }
        if (singer.Type == JTokenType.String)
        {
            return singer.ToString();
        }
        if (singer is JArray list)
        {
            var names = new List<string>();
            foreach (var item in list)
            {
                var name = item.Type == JTokenType.String ? item.ToString() : item["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
            return string.Join("/", names);
        }
        return "";
    }

    /// <summary>毫秒时长 → m:ss。</summary>
    private static string FormatDuration(long? milliseconds)
    {
        if (milliseconds is not > 0)
        {
            return "";
        }
        var span = TimeSpan.FromMilliseconds(milliseconds.Value);
        return $"{(int)span.TotalMinutes:D2}:{span.Seconds:D2}";
    }

    /// <summary>标准 CRC-32（IEEE，同 zlib），输出 8 位大写十六进制（GD api.php 的 s 参数形态）。</summary>
    private static string Crc32Hex(string text)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
            }
        }
        return (crc ^ 0xFFFFFFFF).ToString("X8");
    }

    /// <summary>lxserver 基地址规范化：去尾部斜杠；非 http(s) 绝对地址视为无效配置（返回 null 走 GD 默认链路）。
    /// 同名变量多条时平台按多账号语义用 &amp; 合并投递（如新旧两份 lxserver 地址），合并值整段当 URL 会因
    /// authority 含 "端口&amp;http" 被 Uri 解析判死（Invalid URI: Invalid port specified）——按 &amp; 分段
    /// 取第一个能通过绝对 URI 校验的 http(s) 段，全部非法才回落默认链路。</summary>
    private static string NormalizeBaseUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        foreach (var part in raw.Split('&'))
        {
            var url = part.Trim().TrimEnd('/');
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return url;
            }
        }
        return null;
    }

    /// <summary>落盘文件名：歌手 - 歌名.扩展名；扩展名优先取直链尾部（mp3/flac/m4a 等），取不到按音质推断。</summary>
    private static string BuildFileName(Song song, string url, int br)
    {
        var bare = url.Split('?')[0];
        var dot = bare.LastIndexOf('.');
        var ext = dot > 0 && bare.Length - dot <= 5 ? bare[(dot + 1)..].ToLowerInvariant() : "";
        if (ext != "mp3" && ext != "flac" && ext != "m4a" && ext != "aac" && ext != "wav" && ext != "ape")
        {
            ext = br >= 740 ? "flac" : "mp3";
        }
        var stem = string.IsNullOrWhiteSpace(song.Artist) ? song.Name : $"{song.Artist} - {song.Name}";
        if (stem.Length > 100)
        {
            stem = stem[..100];
        }
        return $"{stem}.{ext}";
    }

    private static string BrLabel(int br) => br >= 740 ? $"无损{br}" : $"{br}k";

    /// <summary>显式开启（1/true/yes/on/是/开）：未配置不算开启。</summary>
    private static bool IsTrue(string value) =>
        value is "1" or "true" or "True" or "yes" or "on" or "是" or "开";

    private static int ReadInt(QuantumTaskContext ctx, string name, int fallback, int min, int max)
    {
        if (ctx.Variables.TryGetValue(name, out var text) && int.TryParse(text?.Trim(), out var value))
        {
            return Math.Clamp(value, min, max);
        }
        return fallback;
    }

    /// <summary>多变量取值：命中第一个非空变量。</summary>
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

    /// <summary>推送开关关闭时只落日志。</summary>
    private static async Task NotifyAsync(QuantumTaskContext ctx, string title, string content, CancellationToken ct)
    {
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendAsync(title, content, ct);
        }
    }

    /// <summary>
    /// 音频气泡：推服务端可播相对地址（api/AppMedia/file?path=…，App 端内播放、无直链时效）。
    /// CDN 直链时效约 1 小时，2026-09-27 起不再上气泡，改推落盘文件的服务端地址
    /// （同 music_search.NotifyMediaAsync / media_saver 回推链路）。仅推送开启时由调用方保证进入。
    /// </summary>
    private static async Task NotifyMediaAsync(QuantumTaskContext ctx, string relativePath, string caption, CancellationToken ct)
    {
        var serverUrl = "api/AppMedia/file?path=" + Uri.EscapeDataString(relativePath);
        await ctx.Notify.SendAudioAsync(serverUrl, caption, ct);
    }
}
