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
// 缓存去重（2026-10-05 修订，治热歌重复下载）：热歌榜曲目连日在榜，此前逐日对同一首歌
//   重复下载，文件门面对同名文件自动加 (N) 后缀各存一份（实测单曲攒到第 9 份副本、日重复率
//   高达九成）。改为落盘独立目录 musicCache/（与 music_search 手动保存、media_saver 直存的
//   music/ 分离互不影响），并以 CustomData music_daily_cached（Data1=曲目标识）作存在性索引：
//   命中即免取链/免探测/免下载，直接复用既有文件推气泡。注意：若手动删除 musicCache/ 下
//   的文件，需同步清理 music_daily_cached 对应记录，否则气泡会指向已不存在的文件。
//
// 轮换选歌（2026-10-06 修订，治每日几乎一样）：热歌榜头部曲目连日在榜，固定推前 N 首导致
//   每天几乎同一批歌（叠加缓存复用后更是天天推同一组文件）。改为候选池=榜单前「推送条数×
//   窗口倍数」，从中轮换选歌：从未推过的新歌优先（按榜单名次），已缓存曲按 music_daily_cached
//   Data5（最近推送日期）最久未推优先；选出后按榜单名次排序推送。Data5 为本次新增列，老数据
//   空值回退 Data4（首次缓存日期）参与排序；窗口倍数可配 scripts_music_daily_window（=1 退化
//   为原始「榜单前 N」行为）。
//
// 下载交互（2026-10-08 新增，App 端一键下载）：音频气泡推完后追加一张「下载」点选卡片，点按
//   即回复 热歌下载N（裸「热歌下载」= 全部当日已推）→ DownloadPushedAsync 按已推快照把曲目
//   从 musicCache/ 经服务端 AppMedia 地址复制下载到 music/ 目录（与 music_search「保存N」、
//   media_saver 直存同目录同命名）。已推快照 = CustomData music_daily_push_songs（Data1=推送
//   日期 Data2=榜单ID Data3=推送序号即气泡序号 Data4=musicCache 相对路径 Data5=文件名
//   Data6=歌手 Data7=歌名 Data8=专辑），今日无快照自动回退昨日；music_daily_saved 台账
//   （Data1=相对路径）跳过已下载曲目防 (N) 副本；快照与台账 3 天自动清理（music/ 文件不动）。
//   任务触发指令配 ^热歌下载[\d,，\s]*$（勾选正则）+ 指令环境变量 music_daily_msg。
//
// 推荐任务配置：定时 Cron 0 0 7 * * ?（每天 07:00，Quartz 六段式，按服务器本地时区，
//   Docker 部署确认容器时区 Asia/Shanghai），开启推送即可；另配触发指令 ^热歌下载[\d,，\s]*$
//   （正则、指令环境变量 music_daily_msg）承接 App「下载」点选，无需其它指令。
//   可选：配 scripts_music_daily_test=1 跳过「同一天已推过」防重（见下），便于手动调试。
//
// 环境变量（全部可选）：
//   scripts_music_daily_rank    榜单：hot=云音乐热歌榜（默认）/ new=新歌榜 / soar=飙升榜 /
//                               original=原创榜，亦可直填网易数字歌单 ID（如 60198）
//   scripts_music_daily_count   推送条数（默认 10，1-20）
//   scripts_music_daily_window  轮换窗口倍数（默认 3，1-10）：候选池=推送条数×窗口，新歌优先、
//                               老歌按最久未推轮换，治每天推的几乎一样；=1 退化为榜单前 N 原行为
//   scripts_music_daily_test    =1 时跳过当日防重检查（手动重推用；防重记录见下）
//   scripts_music_daily_download  手动触发下载（App 点选无需本变量）：已推序号串（如 2,5）或 all
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
    private const string CachedType = "music_daily_cached";
    private const string SnapshotType = "music_daily_push_songs";
    private const string SavedType = "music_daily_saved";
    private const string SaveDir = "musicCache";
    private const string MusicDir = "music";
    private const int DefaultCount = 10;
    private const int DefaultBr = 320;
    private const int DefaultWindow = 3;

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        // 下载交互（2026-10-08 新增）：App「下载」点选回复 热歌下载N / 裸 热歌下载（全部）/
        // 手动变量 scripts_music_daily_download → 把当日（无则回退昨日）已推曲目从 musicCache/
        // 复制下载到 music/。放在防重检查之前——早间推送完成后当天任意时刻点下载都不受
        // 「今日已推过」拦截
        var manualDownload = Var(ctx, "scripts_music_daily_download");
        if (!string.IsNullOrWhiteSpace(manualDownload))
        {
            if (TryStripDailyDownload(manualDownload, out var manualTokens))
            {
                await DownloadPushedAsync(ctx, manualTokens, ct);
            }
            else
            {
                ctx.Log($"scripts_music_daily_download[{manualDownload}]格式无效：应为已推序号串（如 2,5）或 all。");
            }
            return;
        }
        var message = Var(ctx, "music_daily_msg", "command");
        if (!string.IsNullOrWhiteSpace(message) && TryStripDailyDownload(message, out var msgTokens))
        {
            await DownloadPushedAsync(ctx, msgTokens, ct);
            return;
        }

        var rankRaw = (Var(ctx, "scripts_music_daily_rank") ?? "hot").Trim();
        var rankId = ResolveRankId(rankRaw);
        var count = ReadInt(ctx, "scripts_music_daily_count", DefaultCount, 1, 20);
        var window = ReadInt(ctx, "scripts_music_daily_window", DefaultWindow, 1, 10);
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
            (songs, chartName) = await LoadChartAsync(ctx, rankId, count * window, ct);
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

        // 轮换选歌（2026-10-06，治「每日热歌几乎一样」）：热歌榜头部连日在榜，固定推前 N 首导致
        // 每天几乎同一批歌。改为从榜单前 count×window 的候选池选：从未推过的新歌优先（按名次），
        // 已缓存曲按最久未推优先（music_daily_cached Data5=最近推送日期，空回退 Data4 首次缓存
        // 日期），选满后按榜单名次排序推送；window=1 退化为原始「榜单前 N」行为。
        var cacheIndex = await LoadCacheIndexAsync(ctx, ct);
        var candidateCount = songs.Count;
        songs = SelectRotated(songs, cacheIndex, count);
        var freshCount = songs.Count(s => !cacheIndex.ContainsKey(BuildFileKey(s)));
        ctx.Log($"轮换选歌：候选 {candidateCount} 首（推送 {count} × 窗口 {window}），选出 {songs.Count} 首：新歌 {freshCount}、缓存复用 {songs.Count - freshCount}。");

        var header = $"🎵 每日热歌 · {DateTime.Now.Month}月{DateTime.Now.Day}日 {chartName}（{songs.Count} 首 / 音质 {BrLabel(br)}）";
        var listText = string.Join("\r\n", songs.Select((s, i) => $"{i + 1}. {(s.Rank > 0 ? $"#{s.Rank} " : "")}{s.Name} - {s.Artist}{(string.IsNullOrWhiteSpace(s.Album) ? "" : $"《{s.Album}》")} {s.Interval}"));
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
        await CleanStaleSnapshotsAsync(ctx, ct);
        var pushedCount = 0;
        var reusedCount = 0;
        var touchedToday = new HashSet<string>(StringComparer.Ordinal);
        // 已推快照（2026-10-08 下载交互）：气泡推送成功的曲目逐首登记（序号=气泡序号），
        // 供「下载」点选按 日期+序号 反查 musicCache 相对路径
        var pushedSnapshot = new List<(int Order, Song Song, string RelativePath, string FileName)>();
        foreach (var (song, i) in songs.Select((s, idx) => (s, idx)))
        {
            ct.ThrowIfCancellationRequested();
            // 缓存命中（2026-10-05）：music_daily_cached 已登记该曲（musicCache/ 已有文件）→
            // 免取链/免探测/免下载，直接复用既有文件推气泡。文件门面没有「存在即跳过」能力，
            // 故以缓存记录行做存在性索引；命中推送成功后回写 Data5=今日（轮换选歌依据）
            var fileKey = BuildFileKey(song);
            cacheIndex.TryGetValue(fileKey, out var cached);
            if (cached != null && !string.IsNullOrWhiteSpace(cached.Data2))
            {
                try
                {
                    await NotifyMediaAsync(ctx, cached.Data2, $"{i + 1}. {SongLabel(song)}", ct);
                    pushedCount++;
                    reusedCount++;
                    pushedSnapshot.Add((i + 1, song, cached.Data2, cached.Data3));
                    await TryTouchCachedAsync(ctx, cached, fileKey, touchedToday, today, ct);
                    ctx.Log($"命中缓存复用（免下载）：{cached.Data3} → {cached.Data2}。");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    results.Add($"{QuantumText.Color("red", "✗")} {i + 1}. {SongLabel(song)}：缓存复用推送失败（{e.Message}）");
                }
                continue;
            }
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
            // 落盘再推服务端地址：直链约 1 小时过期，下载到 musicCache/ 后文件无时效，
            // 气泡点开走 App 端内播放（api/AppMedia/file），任何时候点都还能听
            try
            {
                var fileName = BuildFileName(song, url, br);
                var file = await ctx.File.DownloadAsync(url, fileName, SaveDir, ct);
                // 先写映射再推气泡：旧直链气泡点「保存」时 media_saver 反查必已能命中
                await TrySaveUrlNameAsync(ctx, url, song, ct);
                // 登记缓存：该曲连日在榜很常见，之后再遇免下载直接复用
                await TrySaveCachedAsync(ctx, fileKey, file.RelativePath, fileName, today, ct);
                await NotifyMediaAsync(ctx, file.RelativePath, $"{i + 1}. {SongLabel(song)}", ct);
                pushedCount++;
                pushedSnapshot.Add((i + 1, song, file.RelativePath, fileName));
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

        // 「下载」点选卡片（2026-10-08 新增，App 端一键下载）：逐曲「下载」按钮 + 「下载全部」，
        // 点按回复 热歌下载N 由 DownloadPushedAsync 复制到 music/。卡片与快照失败均只落日志
        // 不影响推送（代价是当天点选下载不可用，气泡播放不受影响）
        if (pushedSnapshot.Count > 0)
        {
            try
            {
                var downloadOptions = new List<QuantumOption>();
                if (pushedSnapshot.Count > 1)
                {
                    downloadOptions.Add(new QuantumOption(
                        Key: "热歌下载",
                        Label: $"下载全部（{pushedSnapshot.Count} 首）",
                        Color: "blue"));
                }
                foreach (var p in pushedSnapshot)
                {
                    downloadOptions.Add(new QuantumOption(
                        Key: $"热歌下载{p.Order}",
                        Label: $"下载 · {SongLabel(p.Song)}",
                        Color: "green"));
                }
                await ctx.Notify.SendOptionsAsync(
                    header + "\r\n点选「下载」保存到 music/ 目录（重复点选自动跳过已下载）：",
                    downloadOptions, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                ctx.Log($"下载点选卡片推送失败（不影响音频气泡，但当天点选下载不可用）：{e.Message}");
            }
            await SaveSnapshotAsync(ctx, today, rankId, pushedSnapshot, ct);
        }

        ctx.Log($"每日热歌推送完成：气泡 {pushedCount}/{songs.Count}（新下载 {pushedCount - reusedCount}、复用缓存 {reusedCount}），榜单 {chartName}（{rankId}）。");
        await MarkPushedAsync(ctx, today, rankId, ct);
    }

    // ------------------------------------------------------------------ 榜单
    /// <summary>拉网易歌单榜单并取前 keep 首（含榜单名次 Rank，轮换选歌用）。返回 (曲目列表, 榜单名)。</summary>
    private static async Task<(List<Song> Songs, string ChartName)> LoadChartAsync(
        QuantumTaskContext ctx, string rankId, int keep, CancellationToken ct)
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
        var rank = 0;
        foreach (var item in tracks)
        {
            rank++;
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
                "wy",
                rank));
            if (songs.Count >= keep)
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

    /// <summary>清理 3 天前的已推快照与已下载台账（下载交互只对近两天有效；music/ 与 musicCache/
    /// 文件不受影响）；失败只落日志不影响推送。</summary>
    private static async Task CleanStaleSnapshotsAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            foreach (var type in new[] { SnapshotType, SavedType })
            {
                var stale = await ctx.CustomData.QueryAsync(type,
                    new QuantumCustomDataFilter { CreateTimeEnd = DateTime.Now.AddDays(-3) }, ct);
                if (stale.Count > 0)
                {
                    await ctx.CustomData.DeleteAsync(stale.Select(n => n.Id).ToList(), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"旧快照/台账清理失败（不影响推送）：{e.Message}");
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

    /// <summary>缓存索引一次性加载（Data1=曲目标识 → 缓存行）：轮换选歌与推送循环共用，免逐曲查询。
    /// 查询异常按空索引处理（全部视为未缓存，宁可重下也不漏推）。</summary>
    private static async Task<Dictionary<string, QuantumCustomDataValue>> LoadCacheIndexAsync(
        QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            var rows = await ctx.CustomData.QueryAsync(CachedType, ct: ct);
            var index = new Dictionary<string, QuantumCustomDataValue>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.Data1))
                {
                    index[row.Data1] = row; // 重复曲目标识以后写入的为准
                }
            }
            return index;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"缓存索引加载失败（按全部未缓存处理，将重新下载）：{e.Message}");
            return new Dictionary<string, QuantumCustomDataValue>(StringComparer.Ordinal);
        }
    }

    /// <summary>轮换选歌：候选池内「从未推过」的新歌优先（按榜单名次），其余按最久未推优先
    /// （LastPushDate），同优先级按名次；选满 count 后按名次排序返回，气泡顺序与榜单一致。</summary>
    private static List<Song> SelectRotated(
        List<Song> candidates, Dictionary<string, QuantumCustomDataValue> cacheIndex, int count)
    {
        return candidates
            .Select(s => (Song: s, Cached: cacheIndex.TryGetValue(BuildFileKey(s), out var row) ? row : null))
            .OrderBy(t => t.Cached != null)
            .ThenBy(t => LastPushDate(t.Cached))
            .ThenBy(t => t.Song.Rank)
            .Take(count)
            .OrderBy(t => t.Song.Rank)
            .Select(t => t.Song)
            .ToList();
    }

    /// <summary>缓存行「最近推送日期」：Data5 优先，老数据空回退 Data4（首次缓存日期）；
    /// 均无效返回 MaxValue（视作最近刚推过，排最后）。</summary>
    private static DateTime LastPushDate(QuantumCustomDataValue row)
    {
        if (row != null)
        {
            if (DateTime.TryParse(row.Data5, out var d5))
            {
                return d5;
            }
            if (DateTime.TryParse(row.Data4, out var d4))
            {
                return d4;
            }
        }
        return DateTime.MaxValue;
    }

    /// <summary>缓存命中推送成功后回写 Data5=今日（轮换选歌的「最近推送日期」依据）；
    /// 同一轮内同一曲只写一次（touchedToday 去重）；失败只落日志不影响推送
    /// （代价是该曲明天可能重复推一次，不丢功能）。</summary>
    private static async Task TryTouchCachedAsync(
        QuantumTaskContext ctx, QuantumCustomDataValue row, string fileKey,
        HashSet<string> touchedToday, string today, CancellationToken ct)
    {
        if (string.Equals(row.Data5, today, StringComparison.Ordinal) || touchedToday.Contains(fileKey))
        {
            return; // 已是今日或本轮已写过，免写
        }
        try
        {
            await ctx.CustomData.UpdateAsync(new List<QuantumCustomDataValue> { row with { Data5 = today } }, ct);
            touchedToday.Add(fileKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"轮换戳记（最近推送日期）回写失败（不影响推送）：{e.Message}");
        }
    }

    /// <summary>缓存登记（Data1=曲目标识 Data2=相对路径 Data3=文件名 Data4=缓存日期 Data5=最近推送日期）：
    /// 失败只落日志不影响本次推送（代价是该曲下次会再下一份，不丢功能）。</summary>
    private static async Task TrySaveCachedAsync(
        QuantumTaskContext ctx, string fileKey, string relativePath, string fileName, string date, CancellationToken ct)
    {
        try
        {
            await ctx.CustomData.AddAsync(new List<QuantumCustomDataValue>
            {
                new()
                {
                    Type = CachedType, Data1 = fileKey, Data2 = relativePath, Data3 = fileName,
                    Data4 = date, Data5 = date
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"缓存记录登记失败（不影响本次推送）：{e.Message}");
        }
    }

    // ------------------------------------------------------------------ 下载（热歌下载N：musicCache/ → music/）
    /// <summary>下载已推热歌：按「日期（今日无则回退昨日）+推送序号」取快照，把 musicCache/ 文件
    /// 经服务端 AppMedia 地址复制下载到 music/（与 music_search「保存N」、media_saver 直存同目录
    /// 同命名，气泡文件已永存服务端故无直链时效问题）。tokensRaw="all" 或序号串（如 "2,5"）；
    /// 已下载曲目以 music_daily_saved 台账去重，防 music/ 里攒 (N) 副本。</summary>
    private static async Task DownloadPushedAsync(QuantumTaskContext ctx, string tokensRaw, CancellationToken ct)
    {
        try
        {
            await ctx.CustomData.SaveTitleAsync(SavedType, "每日热歌已下载",
                new[] { "musicCache相对路径", "下载日期", "music文件名" }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"已下载台账表头登记失败（不影响下载）：{e.Message}");
        }

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var snapshot = await LoadSnapshotAsync(ctx, today, ct);
        if (snapshot.Count == 0)
        {
            var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
            snapshot = await LoadSnapshotAsync(ctx, yesterday, ct);
            if (snapshot.Count > 0)
            {
                ctx.Log($"今日无已推快照，回退昨日（{yesterday}）{snapshot.Count} 首。");
            }
        }
        if (snapshot.Count == 0)
        {
            await NotifyAsync(ctx, "热歌下载", $"今日（{today}）尚无已推送的热歌记录，请等每日推送后再点下载。", ct);
            return;
        }

        // 序号 → 快照行（同日多轮推送同序号时，以后落库的为准=与聊天里最新气泡对应）
        var byOrder = new Dictionary<int, (int Order, string RelativePath, string FileName, string Label)>();
        foreach (var row in snapshot)
        {
            if (int.TryParse(row.Data3, out var order) && order > 0 && !string.IsNullOrWhiteSpace(row.Data4))
            {
                var label = string.IsNullOrWhiteSpace(row.Data6)
                    ? (row.Data7 ?? "")
                    : $"{row.Data7} - {row.Data6}";
                byOrder[order] = (order, row.Data4, row.Data5, label);
            }
        }

        var picks = new List<(int Order, string RelativePath, string FileName, string Label)>();
        var missed = new List<string>();
        if (tokensRaw == "all")
        {
            picks.AddRange(byOrder.Values.OrderBy(p => p.Order));
        }
        else
        {
            foreach (var token in tokensRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(token, out var order) && byOrder.TryGetValue(order, out var pick))
                {
                    picks.Add(pick);
                }
                else
                {
                    missed.Add(token);
                }
            }
        }

        var results = new List<string>();
        foreach (var miss in missed)
        {
            results.Add($"{QuantumText.Color("red", "✗")} [{miss}] 不在已推热歌序号里（1-{byOrder.Count}），已跳过。");
        }

        var ok = 0;
        foreach (var pick in picks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // 台账去重：该曲目已复制进 music/ 过则跳过（文件门面同名会加 (N) 后缀存副本）
                var saved = await ctx.CustomData.QueryAsync(SavedType, pick.RelativePath, ct: ct);
                if (saved.Count > 0)
                {
                    results.Add($"{QuantumText.Color("gray", "⊘")} {pick.Label}：已在 music/（{saved[0].Data3 ?? pick.FileName}），跳过。");
                    continue;
                }
                var fileName = string.IsNullOrWhiteSpace(pick.FileName)
                    ? pick.RelativePath[(pick.RelativePath.LastIndexOf('/') + 1)..]
                    : pick.FileName;
                // musicCache/ 文件已永存服务端，经 AppMedia 服务端地址复制下载到 music/
                var serverUrl = "api/AppMedia/file?path=" + Uri.EscapeDataString(pick.RelativePath);
                var file = await ctx.File.DownloadAsync(serverUrl, fileName, MusicDir, ct);
                ok++;
                await TrySaveLedgerAsync(ctx, pick.RelativePath, today, file.FileName, ct);
                results.Add($"{QuantumText.Color("green", "✓")} {pick.Label}（{file.Length / 1024.0 / 1024.0:F1}MB）→ {file.FullPath}");
                ctx.Log($"热歌下载：{pick.RelativePath} → {file.FullPath}，{file.Length} 字节。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                results.Add($"{QuantumText.Color("red", "✗")} {pick.Label}：下载失败（{e.Message}）");
            }
        }

        var title = ok == picks.Count && missed.Count == 0 ? "热歌下载完成" : $"热歌下载（{ok}/{picks.Count + missed.Count}）";
        var text = string.Join("\r\n", results);
        ctx.Log(text);
        await NotifyAsync(ctx, title, text, ct);
    }

    /// <summary>按推送日期取已推快照行（Data3=序号且 Data4=相对路径 的有效行）；查询失败按空处理，
    /// 由调用方回退昨日或提示未推送。</summary>
    private static async Task<List<QuantumCustomDataValue>> LoadSnapshotAsync(
        QuantumTaskContext ctx, string date, CancellationToken ct)
    {
        try
        {
            var rows = await ctx.CustomData.QueryAsync(SnapshotType, date, ct: ct);
            return rows
                .Where(r => int.TryParse(r.Data3, out var order) && order > 0 && !string.IsNullOrWhiteSpace(r.Data4))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"已推快照查询失败（{e.Message}）。");
            return new List<QuantumCustomDataValue>();
        }
    }

    /// <summary>已推快照落库（music_daily_push_songs：Data1=推送日期 Data2=榜单ID Data3=推送序号
    /// Data4=musicCache 相对路径 Data5=文件名 Data6=歌手 Data7=歌名 Data8=专辑）：下载交互按
    /// 「日期+序号」反查。失败只落日志不影响推送（代价是当天点选下载不可用）。</summary>
    private static async Task SaveSnapshotAsync(
        QuantumTaskContext ctx, string today, string rankId,
        List<(int Order, Song Song, string RelativePath, string FileName)> pushed, CancellationToken ct)
    {
        try
        {
            await ctx.CustomData.SaveTitleAsync(SnapshotType, "每日热歌已推快照",
                new[] { "推送日期", "榜单ID", "推送序号", "musicCache相对路径", "文件名", "歌手", "歌名", "专辑" }, ct);
            await ctx.CustomData.AddAsync(pushed.Select(p => new QuantumCustomDataValue
            {
                Type = SnapshotType,
                Data1 = today,
                Data2 = rankId,
                Data3 = p.Order.ToString(),
                Data4 = p.RelativePath,
                Data5 = p.FileName,
                Data6 = p.Song.Artist,
                Data7 = p.Song.Name,
                Data8 = p.Song.Album
            }).ToList(), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"已推快照落库失败（不影响推送，但当天点选下载不可用）：{e.Message}");
        }
    }

    /// <summary>已下载台账登记（music_daily_saved：Data1=musicCache 相对路径 Data2=日期 Data3=music/
    /// 实际文件名）：失败只落日志（代价是再点一次会重下一份 (N) 副本）。</summary>
    private static async Task TrySaveLedgerAsync(
        QuantumTaskContext ctx, string relativePath, string date, string fileName, CancellationToken ct)
    {
        try
        {
            await ctx.CustomData.AddAsync(new List<QuantumCustomDataValue>
            {
                new() { Type = SavedType, Data1 = relativePath, Data2 = date, Data3 = fileName }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"已下载台账登记失败（不影响本次下载）：{e.Message}");
        }
    }

    /// <summary>「热歌下载N / 热歌下载（=全部）/ all / 纯序号串」判定并规范化：返回序号串（如 "2,5"）
    /// 或 "all"；非数字垃圾返回 false（交回每日推送主流程，指令任务由正则兜底不会误吞）。
    /// 注意不用「下载N/保存N」前缀——那是 music_search 的指令，避免点选跨任务误触发。</summary>
    private static bool TryStripDailyDownload(string text, out string tokensRaw)
    {
        tokensRaw = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }
        var rest = trimmed;
        if (rest.StartsWith("热歌下载", StringComparison.Ordinal))
        {
            rest = rest["热歌下载".Length..].Trim([' ', ':', '：', ',', '，']);
        }
        if (rest.Length == 0 || rest is "all" or "全部")
        {
            tokensRaw = "all";
            return true;
        }
        var tokens = rest.Split([',', '，', ' ', '　', '\n', '\r', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > 0 && tokens.All(t => t.Length <= 15 && t.All(char.IsDigit)))
        {
            tokensRaw = string.Join(",", tokens);
        }
        return tokensRaw != null;
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
    private sealed record Song(string Id, string Name, string Artist, string Album, string Interval, string Source, int Rank = 0);

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
        return $"{BuildFileKey(song)}.{ext}";
    }

    /// <summary>曲目标识（= 文件名去扩展名）：歌手 - 歌名，超 100 字符截断。与直链/扩展名无关，
    /// 缓存命中判定用它——同一曲目即便换源/换扩展名也视为已缓存，不会再下第二份。</summary>
    private static string BuildFileKey(Song song)
    {
        var stem = string.IsNullOrWhiteSpace(song.Artist) ? song.Name : $"{song.Artist} - {song.Name}";
        return stem.Length > 100 ? stem[..100] : stem;
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
