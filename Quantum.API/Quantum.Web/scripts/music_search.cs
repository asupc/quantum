// ============================================================================
// 音乐搜索下载（2026-09-18 新写）：App 会话发「音乐搜索 关键字」→ 推送歌曲列表
// （序号/歌名/歌手/专辑/时长/歌曲ID），回复「序号」或「歌曲ID」（如 3 或 1,3 或 228908）
// 即自动下载落盘并回执。参照 dygangs_search 单指令分流模式（子任务已下线）。
//
// 数据链路（默认零配置模式）：
//   搜索 = 网易云公开搜索接口（music.163.com/api/search/get/web，免鉴权）；
//   取链 = GD 音乐台节点域 music-api.gdstudio.xyz/api.php（types=url，netease 源），
//          空链 = 该曲在网易无可用资源（版权/VIP 限制），回执如实提示。
// 增强模式（可选）：配 scripts_music_lx_url 指向自建 lxserver（github.com/XCQ0607/lxserver，
//   docker 一键部署，默认端口 9527、API 默认无鉴权）。搜索改走 lxserver /api/music/search
//   （内置 wy/tx/kw/kg/mg 五源 SDK），取链优先 lxserver /api/music/url（需在 lxserver
//   管理后台导入自定义音源脚本），失败自动回落 GD 节点域（回落仅对 wy id 有效）。
//
// 落盘：经 ctx.File 受控文件门面（2026-09-18 平台新增，脚本门禁仍禁 System.IO）：
//   保存到「下载根目录（appsettings Quantum:FileDownloadRoot，缺省 ./downloads）/子目录」，
//   子目录由变量 scripts_music_save_dir 配置；文件名自动清洗非法字符，重名自动追加 (n) 不覆盖。
//
// 交互流（2026-09-20 回执瘦身：App 音频卡片自带「保存」按钮，全部成功不再推回执与保存点选；
//   2026-09-19 直接试听优先；2026-09-18 试听回执带「保存」点选）：
//   「音乐搜索 关键字」→ 逐曲取链直接推音频气泡（点开即听，卡片「保存」按钮走 media_saver 落盘）；
//   全部成功完全静默（原逐首「试听已推送」明细与「保存」点选已按用户要求移除），仅部分失败时推失败明细；
//   配 scripts_music_auto_preview=0 退回「点选列表」模式（列表为可点选项卡片，点按歌曲即回复
//   序号触发试听；手打路径不变）。
//   手打「保存3」「下载 1,3」「保存228908」（歌曲ID）路径完整保留；保存后默认只发文字回执（含路径），
//   配 scripts_music_push_audio=1 可再回推服务端地址音频气泡（无时效，适合未试听直接保存）。
//
// 推荐任务配置：
//   1) 触发指令填 ^音乐搜索.*|^[0-9,，\s]+$|^保存[\d,，\s]+$|^下载[\d,，\s]+$ 并勾选正则；指令环境变量名填 music_msg。
//   2) App 端发「音乐搜索 周杰伦 晴天」即搜索，结果音频气泡直接点播试听；满意点卡片上的「保存」按钮
//      （走 media_saver 反查歌名落盘）或回「保存3」落盘；对结果回复「3」或「1,3」可重推试听，
//      「保存228908」按歌曲ID 落盘——纯数字优先按序号匹配最近一次搜索结果，超出序号范围的数字视为
//      歌曲ID（数字歌名搜索请带指令词）。
//   3) Web 手动调试：配变量 scripts_music_search=关键字（搜索）/ scripts_music_preview=序号/ID（试听）/
//      scripts_music_download=序号/ID（保存落盘）。
//   4) 可选变量：
//      scripts_music_auto_preview 搜索完自动逐曲取链直推试听音频（默认开；置 0 退回点选列表模式）
//      scripts_music_count       列表条数（默认 8，上限 20）
//      scripts_music_br          音质 128/192/320/740/999（默认 320；740/999 为无损，lx 模式映射 flac/flac24bit）
//      scripts_music_save_dir    保存子目录（默认 music；只能填相对下载根目录的相对路径，如 music/周杰伦；
//                                绝对路径会被文件门面拒绝——绝对目录请配 appsettings 的 Quantum:FileDownloadRoot）
//      scripts_music_source      搜索源（默认 wy；lx 模式可选 wy/tx/kw/kg/mg）
//      scripts_music_lx_url      lxserver 基地址（如 http://192.168.x.x:9527；不配则默认模式）
//      scripts_music_push_audio  保存落盘后回推服务端可播地址音频气泡（默认关——试听后保存已听过，
//                                不重复推；盲下场景可设 1 开启）
//
// 迁移映射（计划 2.7）：axios → ctx.Http；JSON.parse → Newtonsoft JObject/JArray；
//   console.log → ctx.Log；sendNotify → ctx.Notify（推送开关自判 ctx.EnablePush）；
//   fs 下载落盘 → ctx.File（脚本侧唯一合法通道）。
// 有意差异：
//   1) GD 音乐台网页版已上 Cloudflare+混淆校验和（同域 api.php 恒 401、节点域搜索忽略关键词），
//      故搜索走网易公开接口、取链用节点域 types=url（实测免校验和、真链可直接下载）；
//   2) 网易 VIP/版权曲常无直链（取链返回空 url），属源侧限制；换 lx 模式并导入自定义音源可解大部分；
//   3) 「回复序号」只认最近一次搜索（缓存 music_search_cache 清空重建），回复歌曲ID不受此限；
//   4) GD 节点域 kuwo 等非网易通道实测不可用（br=-1 空链），回落取链仅对 wy id 尝试。
//   5) 「保存/试听 歌曲ID」直下路径先经网易 song/detail 反查歌名（每日热歌等榜单回执点「保存」即此
//      形态），落盘名「歌手 - 歌名.mp3」、试听配文可读；反查失败维持 ID:xxx 形态继续，不阻塞保存。
//   6) 试听推音频气泡时记录「直链 → 歌名」映射（music_url_name，7 天过期）——App 气泡上的「保存」
//      按钮只发 URL、CDN 直链是 hash，media_saver 保存时反查映射同样得「歌手 - 歌名.mp3」；
//      逐首在推气泡前先写映射（用户秒点保存早于整轮推送完成时反查必中，2026-09-20 竞态修复）。
// ============================================================================
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class MusicSearchTask : IQuantumTask
{
    private const string NeteaseSearchUrl = "https://music.163.com/api/search/get/web";
    private const string NeteaseDetailUrl = "https://music.163.com/api/song/detail";
    private const string UrlNameType = "music_url_name";
    private const string GdUrlApi = "https://music-api.gdstudio.xyz/api.php";
    private const string CacheType = "music_search_cache";
    private const int DefaultCount = 8;
    private const int DefaultBr = 320;
    private const string DefaultSaveDir = "music";

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        // 手动调试变量：Web 配 scripts_music_preview=序号/ID 推试听 / scripts_music_download=序号/ID 保存落盘
        var manualPreview = Var(ctx, "scripts_music_preview");
        if (!string.IsNullOrWhiteSpace(manualPreview))
        {
            await PreviewAsync(ctx, manualPreview, ct);
            return;
        }
        var manualDownload = Var(ctx, "scripts_music_download");
        if (!string.IsNullOrWhiteSpace(manualDownload))
        {
            await DownloadAsync(ctx, manualDownload, ct);
            return;
        }

        // 单指令模式：整条消息经 music_msg 注入，按形态分流（保存N/下载N = 落盘；纯数字 = 试听）
        var input = Var(ctx, "music_msg", "command", "scripts_music_search");
        if (string.IsNullOrWhiteSpace(input))
        {
            ctx.Log("未发现触发变量：发「音乐搜索 关键字」搜索；回复序号试听、回复 保存序号 落盘；" +
                    "手动运行请配变量 scripts_music_search / scripts_music_preview / scripts_music_download。");
            return;
        }

        if (TryStripSaveCommand(input, out var saveTokens))
        {
            await DownloadAsync(ctx, saveTokens, ct);
            return;
        }

        if (IsReplyTokens(input))
        {
            await PreviewAsync(ctx, input, ct);
            return;
        }

        var keyword = NormalizeKeyword(input);
        if (string.IsNullOrWhiteSpace(keyword))
        {
            ctx.Log($"输入[{input}]未解析出搜索关键字。发「音乐搜索 关键字」搜索；回复序号或歌曲ID下载。");
            return;
        }

        await SearchAsync(ctx, keyword, ct);
    }

    // ------------------------------------------------------------------ 搜索
    private static async Task SearchAsync(QuantumTaskContext ctx, string keyword, CancellationToken ct)
    {
        var count = ReadInt(ctx, "scripts_music_count", DefaultCount, 1, 20);
        var source = (Var(ctx, "scripts_music_source") ?? "wy").Trim().ToLowerInvariant();
        if (source.Length == 0)
        {
            source = "wy";
        }
        var lxUrl = NormalizeBaseUrl(Var(ctx, "scripts_music_lx_url"));

        // 搜索结果统一为 (id, 歌名, 歌手, 专辑, 时长, 来源)
        List<Song> songs;
        try
        {
            songs = lxUrl != null
                ? await SearchViaLxAsync(ctx, lxUrl, keyword, source, count, ct)
                : await SearchViaNeteaseAsync(ctx, keyword, count, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"搜索请求失败：{e.Message}");
            await NotifyAsync(ctx, $"音乐搜索失败：{keyword}", $"请求异常：{e.Message}", ct);
            return;
        }

        if (songs.Count == 0)
        {
            ctx.Log($"[{keyword}]未搜索到结果（源 {source}）。");
            await NotifyAsync(ctx, $"音乐搜索：{keyword}", "未搜索到结果，请换个关键字或音源。", ct);
            return;
        }

        // 搜索缓存清空重建（回复序号只认最近一次搜索；Delete+Add 避开同执行内 UpdateAsync 主键冲突）
        var old = await ctx.CustomData.QueryAsync(CacheType, ct: ct);
        if (old.Count > 0)
        {
            await ctx.CustomData.DeleteAsync(old.Select(n => n.Id).ToArray(), ct);
        }
        await ctx.CustomData.SaveTitleAsync(CacheType, "音乐搜索缓存",
            ["序号", "歌曲ID", "歌名", "歌手", "专辑", "时长", "搜索词", "来源"], ct);
        await ctx.CustomData.AddAsync(songs.Select((n, i) => new QuantumCustomDataValue
        {
            Type = CacheType,
            Data1 = (i + 1).ToString(),
            Data2 = n.Id,
            Data3 = n.Name,
            Data4 = n.Artist,
            Data5 = n.Album,
            Data6 = n.Interval,
            Data7 = keyword,
            Data8 = n.Source
        }).ToList(), ct);

        var br = ReadInt(ctx, "scripts_music_br", DefaultBr, 128, 999);
        var header = $"🎵 音乐搜索「{keyword}」命中 {songs.Count} 条（源 {source} / 音质 {BrLabel(br)}）";
        ctx.Log(header);
        foreach (var line in songs.Select((s, i) => $"{i + 1}. {s.Name} - {s.Artist}《{s.Album}》{s.Interval} {s.Source}"))
        {
            ctx.Log(line);
        }
        // 直接试听（2026-09-19）：搜索完即为全部结果逐曲取链推音频气泡（点开即听，免去先点选），
        // 推完附带逐首「保存」点选回执（复用 PreviewAsync，序号 1..N 即缓存序号）；
        // scripts_music_auto_preview=0（或推送关闭）退回「点选列表」模式
        if (!IsFalse(Var(ctx, "scripts_music_auto_preview")) && ctx.EnablePush)
        {
            ctx.Log($"搜索完成：命中 {songs.Count} 条，缓存已重建（{CacheType}），逐曲取链推送试听。");
            // 搜索直推：header 只承担「直推模式」标记与气泡 caption 序号前缀——成功回执与保存点选不再推
            // （App 音频卡片自带「保存」按钮），失败明细由 PreviewAsync 按需推
            await PreviewAsync(ctx, string.Join(",", songs.Select((s, i) => (i + 1).ToString())), ct, header);
            return;
        }
        // 点选列表模式：列表以可点选项推送——点按即回复序号（与手打完全等价），
        // App 端旧版本不可见选项，正文保留用法提示；手打「序号/ID」路径完整保留
        var options = songs.Select((s, i) => new QuantumOption(
            Key: (i + 1).ToString(),
            Label: $"{i + 1}. {s.Name} - {s.Artist}",
            Desc: (string.IsNullOrWhiteSpace(s.Album) ? "" : $"《{s.Album}》") + $" {s.Interval} · {s.Source}")).ToList();
        var text = header + "\r\n点选歌曲试听（或回复序号/歌曲ID）；满意后点「保存」选项或回「保存3」落盘。";
        ctx.Log($"搜索完成：命中 {songs.Count} 条，缓存已重建（{CacheType}）。");
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendOptionsAsync(text, options, ct);
        }
    }

    /// <summary>网易公开搜索接口（默认模式）：music.163.com/api/search/get/web。</summary>
    private static async Task<List<Song>> SearchViaNeteaseAsync(QuantumTaskContext ctx, string keyword, int count, CancellationToken ct)
    {
        var body = "s=" + Uri.EscapeDataString(keyword) + $"&type=1&offset=0&total=true&limit={count}";
        using var content = new StringContent(body, null, "application/x-www-form-urlencoded");
        using var response = await ctx.Http.PostAsync(NeteaseSearchUrl, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"网易搜索接口 HTTP {(int)response.StatusCode}");
        }
        var root = JObject.Parse(json);
        var list = root["result"]?["songs"] as JArray ?? new JArray();
        var songs = new List<Song>();
        foreach (var item in list)
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
        }
        return songs;
    }

    /// <summary>lxserver 搜索（增强模式）：GET /api/music/search，内置 wy/tx/kw/kg/mg 五源。</summary>
    private static async Task<List<Song>> SearchViaLxAsync(QuantumTaskContext ctx, string lxUrl, string keyword, string source, int count, CancellationToken ct)
    {
        var url = $"{lxUrl}/api/music/search?name={Uri.EscapeDataString(keyword)}&source={Uri.EscapeDataString(source)}&type=song&page=1&pages=1";
        var json = await ctx.Http.GetStringAsync(url, ct);
        var list = JArray.Parse(json);
        var songs = new List<Song>();
        foreach (var item in list)
        {
            var id = item["songmid"]?.ToString();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }
            songs.Add(new Song(
                id,
                item["name"]?.ToString() ?? "",
                JoinArtist(item["singer"]),
                item["albumName"]?.ToString() ?? "",
                item["interval"]?.ToString() ?? "",
                item["source"]?.ToString() ?? source));
            if (songs.Count >= count)
            {
                break;
            }
        }
        return songs;
    }

    // ------------------------------------------------------------------ 试听（回复序号：推直链气泡，不落盘）
    /// <summary>试听：取直链推音频气泡（App 端内即点即播）；满意再回「保存N」落盘。直链有时效，请尽快决定。
    /// header 非空 = 搜索直推场景（气泡 caption 带序号；全部成功不再推回执与「保存」点选——App 音频卡片
    /// 自带保存按钮，仅失败时推失败明细）；空 = 手动回复序号试听（保留逐首回执与保存点选）。</summary>
    private static async Task PreviewAsync(QuantumTaskContext ctx, string reply, CancellationToken ct, string header = null)
    {
        var tokens = SplitTokens(reply).Distinct().Take(10).ToList();
        if (tokens.Count == 0)
        {
            ctx.Log($"回复内容[{reply}]未解析出序号/歌曲ID，忽略。");
            return;
        }
        var cache = await ctx.CustomData.QueryAsync(CacheType, ct: ct);
        var (picks, missed) = ResolvePicks(ctx, cache, tokens);

        var results = new List<string>();
        var fails = new List<string>();
        foreach (var miss in missed)
        {
            var line = $"{QuantumText.Color("red", "✗")} [{miss}] 无法识别（不在最近一次搜索结果里，也不是数字歌曲ID），已跳过。";
            results.Add(line);
            fails.Add(line);
        }

        var br = ReadInt(ctx, "scripts_music_br", DefaultBr, 128, 999);
        var lxUrl = NormalizeBaseUrl(Var(ctx, "scripts_music_lx_url"));
        var pushed = 0;
        // 富交互（2026-09-18 B 批）：试听回执带「保存」点选，点按=等价回复「保存N」，免去手打
        var saveOptions = new List<QuantumOption>();
        // 直链→歌曲信息映射（2026-09-20 改逐首「先写映射再推气泡」）：App 气泡「保存」按钮只发 URL、
        // CDN 直链是 hash 无歌名，media_saver 靠 music_url_name 反查得「歌手 - 歌名.mp3」。
        // 实测竞态——原实现整轮推完才批量写映射，用户秒点保存（media_saver 数百 ms 即落盘）早于
        // 映射写入，反查落空只得 hash 文件名（16:46:47 落盘 vs 16:46:50 写映射）——逐首先写彻底消除
        await CleanStaleUrlNamesAsync(ctx, ct);
        foreach (var (token, rawSong) in picks)
        {
            ct.ThrowIfCancellationRequested();
            var song = await EnrichByIdAsync(ctx, rawSong, ct);
            var (url, _, error) = await ResolveSongUrlAsync(ctx, song, br, lxUrl, ct);
            if (string.IsNullOrEmpty(url))
            {
                var reason = error == null
                    ? "该曲在当前源无可用直链（版权/VIP 限制），可试其它版本、降低音质或配置 lxserver 音源"
                    : $"{error.Message}——音源接口暂不可用，稍后重试；长期稳定建议配置 scripts_music_lx_url 自建 lxserver";
                var line = $"{QuantumText.Color("red", "✗")} {SongLabel(song)}：{reason}";
                results.Add(line);
                fails.Add(line);
                continue;
            }
            // 推链前探测（Range 0-1）：直链可能已被风控（返回 JSON 错误页）或已失效，
            // 死链推到 App 只会表现「播放失败」，不如在回执里说清
            var probe = await ProbePlayableAsync(ctx, url, ct);
            if (!probe.Ok)
            {
                var line = $"{QuantumText.Color("red", "✗")} {SongLabel(song)}：直链不可用（{probe.Reason}），可稍后重试或配置 lxserver 音源";
                results.Add(line);
                fails.Add(line);
                continue;
            }
            var caption = string.IsNullOrWhiteSpace(header)
                ? $"{SongLabel(song)}（试听，满意点下方「保存」或回复 保存{token}）"
                : $"{token}. {SongLabel(song)}（试听）";
            // 先写映射再推气泡：气泡到达手机即可点「保存」，media_saver 反查必须已能命中
            await TrySaveUrlNameAsync(ctx, url, song, ct);
            await ctx.Notify.SendAudioAsync(url, caption, ct);
            pushed++;
            results.Add($"{QuantumText.Color("blue", "▶")} {SongLabel(song)} 试听已推送");
            // 「保存」点选仅手动试听场景需要（搜索直推的卡片自带保存按钮，回执不再推）
            if (string.IsNullOrWhiteSpace(header))
            {
                saveOptions.Add(new QuantumOption(
                    Key: $"save{token}",
                    Label: picks.Count > 1 ? $"保存 · {SongLabel(song)}" : "保存这首",
                    Reply: $"保存{token}",
                    Color: "green"));
            }
        }

        var title = pushed == picks.Count && missed.Count == 0 ? "试听已推送" : $"试听推送（{pushed}/{picks.Count + missed.Count}）";
        var text = string.Join("\r\n", results);
        ctx.Log(text);
        if (string.IsNullOrWhiteSpace(header))
        {
            // 手动回复序号试听：逐首结果回执 + 「保存」点选（原交互不变）
            if (saveOptions.Count > 0 && ctx.EnablePush)
            {
                var parts = new List<string>();
                if (title != "试听已推送")
                {
                    parts.Add(title);
                }
                parts.Add(text);
                await ctx.Notify.SendOptionsAsync(string.Join("\r\n", parts), saveOptions, ct);
            }
            else
            {
                await NotifyAsync(ctx, title, text, ct);
            }
        }
        else if (fails.Count > 0 && ctx.EnablePush)
        {
            // 搜索直推（2026-09-20 用户要求）：App 音频卡片自带「保存」按钮，全部成功即完全静默——
            // 不再推回执文字与逐首保存点选；仅失败/无法识别时推失败明细，让用户知道哪些没出来
            await ctx.Notify.SendAsync(title, string.Join("\r\n", fails), ct);
        }
    }

    // ------------------------------------------------------------------ 保存（保存N/下载N：落盘并回推服务端地址）
    private static async Task DownloadAsync(QuantumTaskContext ctx, string reply, CancellationToken ct)
    {
        var tokens = SplitTokens(reply).Distinct().Take(10).ToList();
        if (tokens.Count == 0)
        {
            ctx.Log($"回复内容[{reply}]未解析出序号/歌曲ID，忽略。");
            return;
        }
        var cache = await ctx.CustomData.QueryAsync(CacheType, ct: ct);
        var (picks, missed) = ResolvePicks(ctx, cache, tokens);

        var results = new List<string>();
        foreach (var miss in missed)
        {
            results.Add($"{QuantumText.Color("red", "✗")} [{miss}] 无法识别（不在最近一次搜索结果里，也不是数字歌曲ID），已跳过。");
        }

        var br = ReadInt(ctx, "scripts_music_br", DefaultBr, 128, 999);
        var saveDir = (Var(ctx, "scripts_music_save_dir") ?? DefaultSaveDir).Trim();
        if (saveDir.Length == 0)
        {
            saveDir = DefaultSaveDir;
        }
        var lxUrl = NormalizeBaseUrl(Var(ctx, "scripts_music_lx_url"));
        // 保存后回推服务端可播地址气泡（默认关）：试听→保存路径下与试听气泡重复，回执文字已含保存路径；
        // 盲下场景（未试听直接 保存N/下载N）可配 scripts_music_push_audio=1 开启
        var pushAudio = IsTrue(Var(ctx, "scripts_music_push_audio"));
        var ok = 0;

        foreach (var (token, rawSong) in picks)
        {
            ct.ThrowIfCancellationRequested();
            var song = await EnrichByIdAsync(ctx, rawSong, ct);
            var (url, via, error) = await ResolveSongUrlAsync(ctx, song, br, lxUrl, ct);
            if (string.IsNullOrEmpty(url))
            {
                var reason = error == null
                    ? "该曲在当前源无可用直链（版权/VIP 限制），可试其它版本、降低音质或配置 lxserver 音源"
                    : error.Message;
                results.Add($"{QuantumText.Color("red", "✗")} {SongLabel(song)}：{reason}");
                continue;
            }

            try
            {
                var fileName = BuildFileName(song, url, br);
                var file = await ctx.File.DownloadAsync(url, fileName, saveDir, ct);
                ok++;
                var sizeMb = file.Length / 1024.0 / 1024.0;
                results.Add($"{QuantumText.Color("green", "✓")} {SongLabel(song)}（{sizeMb:F1}MB, {BrLabel(br)}）→ {file.FullPath} [via:{via}]");
                ctx.Log($"已下载：{song.Name} - {song.Artist}，{file.Length} 字节，保存至 {file.FullPath}。");
                if (pushAudio)
                {
                    await NotifyMediaAsync(ctx, file.RelativePath, $"{song.Name} - {song.Artist}", ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                results.Add($"{QuantumText.Color("red", "✗")} {SongLabel(song)}：下载失败 {e.Message}");
            }
        }

        var title = ok == picks.Count && missed.Count == 0 ? "音乐保存完成" : $"音乐保存完成（{ok}/{picks.Count + missed.Count}）";
        var text = string.Join("\r\n", results);
        ctx.Log(text);
        await NotifyAsync(ctx, title, text, ct);
    }

    /// <summary>回复令牌 → 歌曲选择：纯数字优先按序号（Data1 精确比对，QueryAsync 是包含匹配不能直接用），
    /// 序号范围外再按歌曲ID（Data2）匹配，仍无则视为直下 ID（按当前配置源解析）。</summary>
    private static (List<(string Token, Song Song)> Picks, List<string> Missed) ResolvePicks(
        QuantumTaskContext ctx, IReadOnlyList<QuantumCustomDataValue> cache, List<string> tokens)
    {
        var picks = new List<(string, Song)>();
        var missed = new List<string>();
        foreach (var token in tokens)
        {
            Song song = null;
            if (int.TryParse(token, out var index))
            {
                song = cache.FirstOrDefault(n => n.Data1 == index.ToString()) is { } row
                    ? RowToSong(row)
                    : (cache.FirstOrDefault(n => n.Data2 == token) is { } byId ? RowToSong(byId) : null);
            }
            else
            {
                song = cache.FirstOrDefault(n => n.Data2 == token) is { } row ? RowToSong(row) : null;
            }
            if (song == null && long.TryParse(token, out _))
            {
                var fallbackSource = (Var(ctx, "scripts_music_source") ?? "wy").Trim().ToLowerInvariant();
                song = new Song(token, "ID:" + token, "", "", "", fallbackSource);
            }
            if (song == null)
            {
                missed.Add(token);
            }
            else
            {
                picks.Add((token, song));
            }
        }
        return (picks, missed);
    }

    /// <summary>取链：lx 模式优先 lxserver /api/music/url，失败/未配置回落 GD 节点域（仅 wy）。返回 (直链, via, 异常)。</summary>
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
        // 跨源兜底（lxserver 模式）：原源取链失败（lx 分支抽风/GD 挂/无链）时，按 tx→kw→mg→kg
        // 搜索同名曲再取链——单一音源分支随时可能故障，跨源让「同一首歌总有源能出链」
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

    /// <summary>
    /// 跨源兜底（lxserver 模式）：lxserver 搜索是五源内置的，用「歌名 歌手」在备用源搜同名曲、
    /// 校验歌名后按该源 songmid 取链。顺序 tx→kw→mg→kg（tx 对 VIP 曲出链能力最好）。
    /// </summary>
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

    /// <summary>
    /// 推链前探测直链真伪（Range bytes=0-1 只取响应头）：2xx 且 Content-Type 非文本/JSON 才算可播。
    /// 拦截两类死链——音源风控返回的 JSON 错误页、已过期的直链。
    /// </summary>
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

    /// <summary>GD 音乐台节点域取链：POST api.php（types=url&source=netease，s 为标准 CRC32 十六进制，节点域实测不校验）。</summary>
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

    // ------------------------------------------------------------------ 解析与工具
    private sealed record Song(string Id, string Name, string Artist, string Album, string Interval, string Source);

    /// <summary>清理 7 天前旧映射（直链本身短命，行也就几十字节）；失败只落日志不影响推送主流程。</summary>
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
            ctx.Log($"直链→歌名映射写入失败（不影响推送）：{e.Message}");
        }
    }

    /// <summary>直下 ID 形态（Name="ID:xxx"，如每日热歌回执点「保存」回的 保存&lt;歌曲ID&gt;）先按
    /// 网易 song/detail 免鉴权接口反查歌名/歌手/专辑——试听配文与落盘文件名（歌手 - 歌名.mp3）可读；
    /// 查不到（非网易 ID/已下架）或接口失败则维持 ID 形态继续，不阻塞保存。</summary>
    private static async Task<Song> EnrichByIdAsync(QuantumTaskContext ctx, Song song, CancellationToken ct)
    {
        if (!song.Name.StartsWith("ID:", StringComparison.Ordinal))
        {
            return song;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{NeteaseDetailUrl}?id={Uri.EscapeDataString(song.Id)}&ids=[{Uri.EscapeDataString(song.Id)}]");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            using var response = await ctx.Http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return song;
            }
            if (JObject.Parse(json)["songs"] is not JArray { Count: > 0 } list)
            {
                return song;
            }
            var item = list[0];
            var name = item["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return song;
            }
            ctx.Log($"直下 ID 反查歌名：{song.Id} → {name}。");
            return new Song(song.Id, name, JoinArtist(item["artists"]),
                item["album"]?["name"]?.ToString() ?? "", FormatDuration(item["duration"]?.ToObject<long?>() ?? null), song.Source);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"直下 ID 反查歌名失败（{song.Id}）：{e.Message}，按 ID 形态继续。");
            return song;
        }
    }

    private static Song RowToSong(QuantumCustomDataValue row) => new(
        row.Data2, row.Data3, row.Data4, row.Data5, row.Data6, string.IsNullOrWhiteSpace(row.Data8) ? "wy" : row.Data8);

    private static string SongLabel(Song song)
    {
        var artist = string.IsNullOrWhiteSpace(song.Artist) ? "" : $" - {song.Artist}";
        return $"{song.Name}{artist}";
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

    /// <summary>毫秒时长 → m:ss（网易接口给毫秒，lx 给已格式化字符串则原样）。</summary>
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

    /// <summary>纯数字/逗号/空白（如 3 或 1,3 或 228908）判定为选歌回复；数字歌名请用「音乐搜索 关键字」形式。</summary>
    private static bool IsReplyTokens(string text)
    {
        var tokens = SplitTokens(text);
        return tokens.Length > 0 && tokens.All(t => t.Length <= 15 && t.All(char.IsDigit));
    }

    /// <summary>「保存N / 下载N / save N」判定并剥离前缀：剩余必须是序号/ID 串——
    /// 「保存媒体 http…」等非数字形态返回 false，交给别的任务处理，避免误吞。</summary>
    private static bool TryStripSaveCommand(string text, out string tokensRaw)
    {
        tokensRaw = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }
        foreach (var prefix in new[] { "保存", "下载", "save" })
        {
            if (trimmed.Length > prefix.Length && trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = trimmed[prefix.Length..].Trim([' ', ':', '：', ',', '，']);
                var tokens = SplitTokens(rest);
                if (tokens.Length > 0 && tokens.All(t => t.Length <= 15 && t.All(char.IsDigit)))
                {
                    tokensRaw = string.Join(",", tokens);
                }
                return tokensRaw != null;
            }
        }
        return false;
    }

    /// <summary>按逗号/空白拆分（兼容半角/全角逗号与制表空白）。</summary>
    private static string[] SplitTokens(string reply) => reply.Split([',', '，', ' ', '　', '\n', '\r', '\t'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>指令整条消息触发时剥离命令词：「音乐搜索 凡人」→「凡人」，兼容无空格「音乐搜索凡人」。</summary>
    private static string NormalizeKeyword(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var keyword = raw.Trim();
        foreach (var prefix in new[] { "音乐搜索", "音乐查找", "搜歌", "找歌", "音乐", "搜", "music" })
        {
            if (keyword.Length > prefix.Length && keyword.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                keyword = keyword[prefix.Length..].Trim([' ', ':', '：', '，', ',']);
                break;
            }
        }
        return keyword.Length == 0 ? null : keyword;
    }

    /// <summary>lxserver 基地址规范化：去尾部斜杠；非 http(s) 绝对地址视为无效配置（返回 null 走默认模式）。
    /// 同名变量多条时平台按多账号语义用 & 合并投递（如新旧两份 lxserver 地址），合并值整段当 URL 会因
    /// authority 含 "端口&amp;http" 被 Uri 解析判死（Invalid URI: Invalid port specified，搜索直接炸）——
    /// 按 &amp; 分段取第一个能通过绝对 URI 校验的 http(s) 段，全部非法才回落默认模式。</summary>
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

    private static string BrLabel(int br) => br >= 740 ? $"无损{br}" : $"{br}k";

    /// <summary>显式开启（1/true/yes/on/是/开）：未配置不算开启（保存后回推气泡默认关）。</summary>
    private static bool IsTrue(string value) =>
        value is "1" or "true" or "True" or "yes" or "on" or "是" or "开";

    /// <summary>显式关闭（0/false/no/off/否/关）：未配置不算关闭（搜索直推试听默认开）。</summary>
    private static bool IsFalse(string value) =>
        value is "0" or "false" or "False" or "no" or "off" or "否" or "关";

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
    /// 音频气泡（M2 起）：改推服务端可播相对地址（api/AppMedia/file?path=…，App 端内播放、无直链时效）；
    /// GD 直链不再上气泡（时效有限只在即时试听有价值），来源信息保留在下载回执与日志里。
    /// </summary>
    private static async Task NotifyMediaAsync(QuantumTaskContext ctx, string relativePath, string caption, CancellationToken ct)
    {
        if (!ctx.EnablePush)
        {
            return;
        }
        var serverUrl = "api/AppMedia/file?path=" + System.Uri.EscapeDataString(relativePath);
        await ctx.Notify.SendAudioAsync(serverUrl, caption, ct);
    }
}
