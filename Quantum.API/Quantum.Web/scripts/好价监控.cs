// ============================================================================
// 好价监控（2026-09-20 新增）：轮询什么值得买移动站好价榜（m.smzdm.com/top/{分类}/，
// Nuxt SSR 匿名直连无凭据），按值率/评论数/关键词过滤，把「新出现且达标」的好价推送到 App。
// 状态机对齐 yousukenabo/smzdm（基线不推旧货、未达标新品等涨、已推永不再推）。
// 数据源结论（2026-09-20 调研）：faxian json_more 需浏览器生成 WAF cookie（纯 HTTP 不可行）、
// search.smzdm.com 关键词搜索被瑞数反爬（上游要隐身 Chromium），均不采用；榜单页偶发瑞数
// 202 挑战（无 __NUXT__）时该榜本轮跳过不动状态。
// 环境变量：
//   HAOJIA_KEYWORDS    可选。白名单关键词（逗号分隔，标题或副标题含任一才推）；空=只按阈值
//   HAOJIA_EXCLUDE     可选。黑名单关键词；默认「外卖,评论有奖,话费,充值,积分,红包」（过滤活动贴噪音）
//   HAOJIA_MIN_WORTH   可选。值率阈值 %，默认 70
//   HAOJIA_MIN_COMMENT 可选。评论数阈值，默认 50
//   HAOJIA_MIN_VOTES   可选。值票数阈值，默认 0
//   HAOJIA_RANKS       可选。榜单 slug 逗号分隔（all=全站榜），默认全部 10 榜
//   HAOJIA_MAX_PUSH    可选。单次推送条数上限（安全阀防刷屏），默认 10
// 去重状态存 CustomData（haojia_seen：article_id/榜单/状态/最后见到），7 天未见自动清理。
// 建议任务 cron 每 30 分钟；无新达标好价不推送。
// ============================================================================
using System.Text.RegularExpressions;
using Quantum.Plugins;

public class HaoJiaMonitorTask : IQuantumTask
{
    private const string MobileUa =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1";
    private const string SeenType = "haojia_seen";
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    private static readonly (string Slug, string Name)[] AllRanks =
    [
        ("all", "全站好价榜"),
        ("diannaoshuma", "电脑数码榜"),
        ("jiayongdianqi", "家用电器榜"),
        ("yundonghuwai", "运动户外榜"),
        ("gehuhuazhuang", "个护化妆榜"),
        ("muyingyongpin", "母婴用品榜"),
        ("riyongbaihuo", "日用百货榜"),
        ("fushixiebao", "服饰鞋包榜"),
        ("youxi", "游戏榜"),
        ("yiliaojiankang", "医疗健康榜")
    ];

    private sealed record NuxtItem(string Id, string Title, string Subtitle, string Url, string Mall,
        int WorthPercent, int Worthy, int Comment);

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var include = SplitWords(ctx.Variables.TryGetValue("HAOJIA_KEYWORDS", out var inc) ? inc : null);
        var exclude = SplitWords(ctx.Variables.TryGetValue("HAOJIA_EXCLUDE", out var exc) ? exc : null);
        if (exclude.Count == 0) exclude = ["外卖", "评论有奖", "话费", "充值", "积分", "红包"];
        var minWorth = ParseInt(ctx.Variables.TryGetValue("HAOJIA_MIN_WORTH", out var w) ? w : null, 70);
        var minComment = ParseInt(ctx.Variables.TryGetValue("HAOJIA_MIN_COMMENT", out var c) ? c : null, 50);
        var minVotes = ParseInt(ctx.Variables.TryGetValue("HAOJIA_MIN_VOTES", out var v) ? v : null, 0);
        var maxPush = Math.Clamp(ParseInt(ctx.Variables.TryGetValue("HAOJIA_MAX_PUSH", out var mp) ? mp : null, 10), 1, 50);
        var ranks = ParseRanks(ctx.Variables.TryGetValue("HAOJIA_RANKS", out var rk) ? rk : null);
        if (ranks.Count == 0)
        {
            ctx.Log("HAOJIA_RANKS 配置未匹配到任何榜单，任务结束");
            return;
        }

        await ctx.CustomData.SaveTitleAsync(SeenType, "好价监控去重", ["article_id", "榜单", "状态", "最后见到"], ct);

        // 既有状态：article_id → 行（重复 id 后者覆盖）；slug 是否建过基线
        var rows = await ctx.CustomData.QueryAsync(SeenType, ct: ct);
        var known = new Dictionary<string, QuantumCustomDataValue>();
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.Data1)) known[row.Data1] = row;
        }
        var knownSlugs = new HashSet<string>();
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.Data2)) knownSlugs.Add(row.Data2);
        }

        var pushes = new List<string>();
        var keepAlive = new List<QuantumCustomDataValue>();
        var keptIds = new HashSet<string>();
        var adds = new List<QuantumCustomDataValue>();
        var now = NowStamp();
        var baselineAdds = 0;

        foreach (var (slug, name) in ranks)
        {
            ct.ThrowIfCancellationRequested();
            string html;
            try
            {
                html = await FetchAsync(ctx, slug, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                ctx.Log($"[{name}] 抓取失败：{e.Message}");
                continue;
            }
            var items = ParseNuxtItems(html);
            if (items.Count == 0)
            {
                ctx.Log($"[{name}] 未解析到条目（可能被瑞数挑战页拦截），本轮跳过");
                continue;
            }
            var firstSight = !knownSlugs.Contains(slug);

            foreach (var item in items)
            {
                // 基线旧货：压制并保活（刷新「最后见到」，防止过期误推）
                if (known.TryGetValue(item.Id, out var row))
                {
                    if (row.Data3 == "baseline" && keptIds.Add(item.Id))
                        keepAlive.Add(row with { Data4 = now });
                    continue;
                }
                if (adds.Any(a => a.Data1 == item.Id)) continue; // 本轮已处理过（跨榜重复）
                if (firstSight)
                {
                    // 首次监控该榜：存量全是旧货，记基线不推送
                    adds.Add(new QuantumCustomDataValue { Type = SeenType, Data1 = item.Id, Data2 = slug, Data3 = "baseline", Data4 = now });
                    baselineAdds++;
                    continue;
                }
                if (!Qualifies(item, include, exclude, minWorth, minComment, minVotes)) continue; // 未达标新品：先等涨
                if (pushes.Count >= maxPush) continue; // 达到推送上限：留到下轮
                pushes.Add(FormatLine(item, name));
                adds.Add(new QuantumCustomDataValue { Type = SeenType, Data1 = item.Id, Data2 = slug, Data3 = "pushed", Data4 = now });
            }
        }

        // 落状态：先更新既有基线行（仅查回的行，本轮新增行一次写对避免主键冲突），再新增，最后清过期
        if (keepAlive.Count > 0) await ctx.CustomData.UpdateAsync(keepAlive, ct);
        if (adds.Count > 0) await ctx.CustomData.AddAsync(adds, ct);
        var cutoff = DateTime.Now - Ttl;
        var expiredIds = new List<string>();
        foreach (var row in rows)
        {
            if (keptIds.Contains(row.Data1)) continue; // 本轮刚保活的不过期
            var seen = DateTime.TryParse(row.Data4, out var s) ? s : row.CreateTime;
            if (seen < cutoff) expiredIds.Add(row.Id);
        }
        if (expiredIds.Count > 0)
        {
            await ctx.CustomData.DeleteAsync(expiredIds, ct);
            ctx.Log($"清理 {expiredIds.Count} 条 7 天未见记录");
        }

        if (pushes.Count > 0)
        {
            var sb = new System.Text.StringBuilder($"好价监控 {DateTime.Now:MM-dd HH:mm} 新增 {pushes.Count} 条达标好价");
            foreach (var line in pushes) sb.Append("\n\n").Append(line);
            await NotifyAsync(ctx, sb.ToString(), ct);
        }
        ctx.Log($"好价监控执行完毕：本轮推送 {pushes.Count} 条，新增基线 {baselineAdds} 条，保活 {keepAlive.Count} 条。");
    }

    // ================================ 过滤与文案 ================================

    private static bool Qualifies(NuxtItem item, List<string> include, List<string> exclude,
        int minWorth, int minComment, int minVotes)
    {
        if (item.WorthPercent < minWorth || item.Comment < minComment || item.Worthy < minVotes) return false;
        var text = item.Title + item.Subtitle;
        if (include.Count > 0 && !include.Any(k => text.Contains(k, StringComparison.Ordinal))) return false;
        if (exclude.Any(k => text.Contains(k, StringComparison.Ordinal))) return false;
        return true;
    }

    private static string FormatLine(NuxtItem item, string rankName)
    {
        var sb = new System.Text.StringBuilder()
            .Append(QuantumText.Tag("green", "好价"))
            .Append('【').Append(string.IsNullOrEmpty(item.Mall) ? rankName : item.Mall).Append("】")
            .Append(Truncate(item.Title, 40))
            .Append($"\n（{item.WorthPercent}%值 · {item.Comment}评 · {item.Worthy}人觉得值）");
        if (!string.IsNullOrEmpty(item.Subtitle))
            sb.Append('\n').Append(Truncate(item.Subtitle, 40));
        sb.Append('\n').Append(item.Url);
        return sb.ToString();
    }

    // ================================ 抓取与解析 ================================

    private static async Task<string> FetchAsync(QuantumTaskContext ctx, string slug, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(1500, 3500), ct); // 榜间随机间隔，防匀速特征
        var url = slug == "all" ? "https://m.smzdm.com/top/" : $"https://m.smzdm.com/top/{slug}/";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", MobileUa);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Referer", "https://m.smzdm.com/");
        using var response = await ctx.Http.SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static readonly Regex NuxtBlockRegex = new(@"window\.__NUXT__\s*=\s*(.*?)</script>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NuxtAnchorRegex = new(@"article_id:""\d+""", RegexOptions.Compiled);

    /// <summary>
    /// 解析移动站好价榜的 window.__NUXT__ 内嵌数据（对齐 yousukenabo/smzdm_monitor.py：以
    /// article_id 为锚切条目，字段均为字面量字符串，正则稳定提取；\u002F 反转义 + HTML 实体解码）。
    /// </summary>
    private static List<NuxtItem> ParseNuxtItems(string html)
    {
        var result = new List<NuxtItem>();
        var block = NuxtBlockRegex.Match(html);
        if (!block.Success) return result;
        var raw = block.Groups[1].Value;
        var anchors = NuxtAnchorRegex.Matches(raw).Cast<Match>().ToList();
        var seen = new HashSet<string>();
        for (var i = 0; i < anchors.Count; i++)
        {
            var start = anchors[i].Index;
            var end = i + 1 < anchors.Count ? anchors[i + 1].Index : Math.Min(start + 1500, raw.Length);
            var seg = raw[start..end];
            var id = GrabStr(seg, "article_id");
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            var pct = GrabStr(seg, "article_worthy_per_cent").Replace("%", "").Trim();
            result.Add(new NuxtItem(
                id,
                GrabStr(seg, "article_title"),
                GrabStr(seg, "article_subtitle"),
                GrabStr(seg, "article_url"),
                GrabStr(seg, "article_mall"),
                int.TryParse(pct, out var wp) ? wp : 0,
                GrabNum(seg, "article_worthy"),
                GrabNum(seg, "article_comment")));
        }
        return result;
    }

    private static string GrabStr(string seg, string field)
    {
        var m = Regex.Match(seg, $"{field}:\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Replace("\\u002F", "/").Replace("\\/", "/")) : "";
    }

    private static int GrabNum(string seg, string field)
    {
        // 计数字段源码里是带引号的字符串（如 "7546"），timesort 是裸数字，两者都兼容
        var m = Regex.Match(seg, $"{field}:\"?(\\d+)\"?");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    // ================================ 工具 ================================

    private static List<(string Slug, string Name)> ParseRanks(string? conf)
    {
        if (string.IsNullOrWhiteSpace(conf)) return [.. AllRanks];
        var wanted = conf.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s == "all" ? "" : s)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. AllRanks.Where(r => wanted.Contains(r.Slug))];
    }

    private static List<string> SplitWords(string? conf) =>
        string.IsNullOrWhiteSpace(conf)
            ? []
            : [.. conf.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string NowStamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse((value ?? "").Trim(), out var v) ? v : fallback;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static async Task NotifyAsync(QuantumTaskContext ctx, string content, CancellationToken ct)
    {
        if (ctx.EnablePush) await ctx.Notify.SendAsync("好价监控", content, ct);
        else ctx.Log($"（推送关闭）好价监控：{content}");
    }
}
