// ============================================================================
// 热点速览（2026-09-17 新写）：定时/指令把热点新闻摘要推送到 App 会话流，重复出现的话题不打扰。
// 后端要求：用到 2026-09-17 的 ctx.CustomData 契约（全列过滤 QueryAsync / AddAsync / DeleteAsync /
//   SaveTitleAsync）做「已推送」去重，旧构建会拒绝保存，请先部署含该契约的后端。
// 数据源（2026-09-17 实测选型，全部免 key；微博官方接口要 Cookie、vvhan/tenapi 聚合站均已失效故不用）：
//   知乎热榜 —— api.zhihu.com/topstory/hot-list（题目自带 excerpt 摘要与热度，即「总结」形态）；
//   60s API —— github.com/vikiboss/60s「每天60秒读懂世界」（当日要闻清单+封面图），作备选源与兜底。
// 详情链接（2026-09-19 增；2026-09-20 改命名链接提信息密度）：知乎条目 target.url 形如
//   api.zhihu.com/questions/{id}，改写为网页版 www.zhihu.com/question/{id}；推送以 {{link:详情|URL}}
//   命名链接附在标题行尾（App/Web 2026-09-20 起支持：气泡内只显示「详情」、点击跳转，不占链接地址版面；
//   旧版客户端按字面显示标记）；60s 源接口无逐条链接（news 为纯文本数组），
//   消息尾部「查看当天全文」同样为命名链接（跳当天微信原文 link）。
// 触发与配置：
//   定时 Cron 推荐 0 0 9,21 * * ?（每天 9 点/21 点各推一次）；
//   指令触发：触发指令填 ^热点.* 勾正则，指令环境变量名填 hotnews_msg——
//     App 端发「热点」走默认源，发「热点 60s」临时切换 60s 源（也可 Cron+指令配在同一任务上）；
//   Web 手动调试：配变量 command=热点 或 热点 60s 即可。
// 环境变量：
//   scripts_hotnews_source（可选，默认 zhihu，可 60s）；scripts_hotnews_max（可选，默认 10，限 3~20）；
//   scripts_hotnews_seen_hours（可选，去重窗口小时数，默认 36）；scripts_60s_url（可选，60s 接口地址轮换用）。
// 去重设计（custom data 类型 hotnews_seen，表头：来源/标题键/推送时间）：
//   每次推送把本次出现的话题标题键写入；窗口期内再次出现的话题不再带「新」标（照常列出，仅少个标），
//   窗口外自动清理（>72h 的记录删除）——所以「新」的含义是「窗口期内首次上榜」，不是绝对首见。
// 注意事项：
//   1) 知乎接口按出口 IP 风控，若 NAS 出口被拦（HTTP 403），把 scripts_hotnews_source 设为 60s 即可，
//      zhihu 拉取失败时脚本也会自动落 60s 兜底；
//   2) 日期/时间取服务器本地时区，Docker 部署请确认容器时区为 Asia/Shanghai；
//   3) 标题键取「去空白后前 50 字」做匹配，知乎同题改尾缀不算新话题。
// ============================================================================
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class HotNewsDigestTask : IQuantumTask
{
    private const string ZhihuHotListUrl = "https://api.zhihu.com/topstory/hot-list?limit=20";
    private const string SixtySecondsUrl = "https://60s.viki.moe/v2/60s";
    private const string SeenType = "hotnews_seen";
    private const int DefaultMaxItems = 10;
    private const int DefaultSeenHours = 36;
    private const int CleanupHours = 72;

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var now = DateTime.Now;
        string source = ResolveSource(ctx);
        int max = ResolveMax(ctx);
        int seenHours = ResolveSeenHours(ctx);
        ctx.Log($"源：{source}，条数上限：{max}，去重窗口：{seenHours}h");

        // ── 拉取（zhihu 失败自动落 60s 兜底） ──
        List<(string Title, string Summary, string Heat, string Link)> items;
        string articleLink = string.Empty;
        if (source == "zhihu")
        {
            items = await FetchZhihuAsync(ctx, ct);
            if (items.Count == 0)
            {
                ctx.Log("知乎热榜拉取失败，自动切换 60s 兜底。");
                source = "60s";
            }
        }
        else
        {
            items = null;
        }
        if (source == "60s")
        {
            (items, articleLink) = await FetchSixtySecondsAsync(ctx, ct);
        }
        if (items.Count == 0)
        {
            await ctx.Notify.SendAsync("热点速览", $"两个源都没取到数据，请稍后重试或查看任务日志。", ct);
            return;
        }
        var picked = items.Take(max).ToList();

        // ── 去重：窗口期内已推送过的话题不再带 🔥 ──
        var seenKeys = await LoadSeenKeysAsync(ctx, source, now, seenHours, ct);
        var fresh = new List<string>();
        foreach (var item in picked)
        {
            ct.ThrowIfCancellationRequested();
            var key = NormalizeKey(item.Title);
            if (!seenKeys.Contains(key))
            {
                fresh.Add(key);
            }
        }

        // ── 组装并推送 ──
        var body = BuildMessage(source, picked, fresh, now, articleLink);
        ctx.Log(body);
        await ctx.Notify.SendAsync(source == "zhihu" ? "热点速览 · 知乎热榜" : "热点速览 · 每天60秒", body, ct);

        // ── 记录与清理（失败只记日志，不影响已完成的推送） ──
        await RecordSeenAsync(ctx, source, fresh, now, ct);
        await CleanupAsync(ctx, now, ct);
    }

    /// <summary>解析源：指令后缀（「热点 60s」）优先于 scripts_hotnews_source 变量，默认 zhihu。</summary>
    private static string ResolveSource(QuantumTaskContext ctx)
    {
        var command = ctx.Variables.TryGetValue("command", out var cmd) ? cmd : null;
        if (!string.IsNullOrEmpty(command))
        {
            var rest = command.Replace("热点", string.Empty).Trim();
            if (rest.Length > 0 && (rest.Contains("60") || rest.Contains("早报") || rest.Contains("秒")))
            {
                return "60s";
            }
            if (rest.Length > 0 && rest.Contains("知乎", StringComparison.Ordinal))
            {
                return "zhihu";
            }
        }
        return ctx.Variables.TryGetValue("scripts_hotnews_source", out var configured)
                && !string.IsNullOrEmpty(configured)
                && configured.Trim() == "60s"
            ? "60s"
            : "zhihu";
    }

    private static int ResolveMax(QuantumTaskContext ctx)
    {
        if (ctx.Variables.TryGetValue("scripts_hotnews_max", out var configured)
            && int.TryParse(configured, out var value))
        {
            return Math.Clamp(value, 3, 20);
        }
        return DefaultMaxItems;
    }

    private static int ResolveSeenHours(QuantumTaskContext ctx)
    {
        if (ctx.Variables.TryGetValue("scripts_hotnews_seen_hours", out var configured)
            && int.TryParse(configured, out var value) && value > 0)
        {
            return value;
        }
        return DefaultSeenHours;
    }

    // ────────────────────────── 数据源 ──────────────────────────

    /// <summary>知乎热榜：标题 + excerpt 摘要（截 70 字）+ 热度文案 + 问题页链接。</summary>
    private static async Task<List<(string Title, string Summary, string Heat, string Link)>> FetchZhihuAsync(
        QuantumTaskContext ctx, CancellationToken ct)
    {
        var root = await GetJsonAsync(ctx, ZhihuHotListUrl, ct);
        var list = new List<(string, string, string, string)>();
        if (root?["data"] is not JArray data)
        {
            return list;
        }
        foreach (var item in data)
        {
            ct.ThrowIfCancellationRequested();
            var target = item["target"];
            var title = target?["title"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }
            var excerpt = OneLine(target?["excerpt"]?.ToString());
            var summary = string.IsNullOrEmpty(excerpt) ? string.Empty : TrimText(excerpt, 70);
            var heat = OneLine(item["detail_text"]?.ToString());
            if (string.IsNullOrEmpty(heat))
            {
                heat = OneLine(target?["detail_text"]?.ToString());
            }
            var link = ZhihuWebUrl(target?["url"]?.ToString(), target?["id"]?.ToString());
            list.Add((title, summary, heat, link));
        }
        return list;
    }

    /// <summary>知乎条目链接：target.url 形如 api.zhihu.com/questions/{id}，改写为网页版
    /// www.zhihu.com/question/{id}；非 question 形态的 http(s) 链接原样返回；无 url 时按 id 拼。</summary>
    private static string ZhihuWebUrl(string rawUrl, string id)
    {
        const string apiPrefix = "api.zhihu.com/questions/";
        if (!string.IsNullOrEmpty(rawUrl))
        {
            var url = rawUrl.Trim();
            int pos = url.IndexOf(apiPrefix, StringComparison.Ordinal);
            if (pos >= 0)
            {
                var rest = url[(pos + apiPrefix.Length)..].TrimEnd('/');
                return string.IsNullOrEmpty(rest) ? string.Empty : $"https://www.zhihu.com/question/{rest}";
            }
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }
        return string.IsNullOrEmpty(id) ? string.Empty : $"https://www.zhihu.com/question/{id}";
    }

    /// <summary>60s API：当日要闻清单（条目即一句话摘要，无逐条链接）；
    /// ArticleLink 为当天微信原文（data.link），供消息尾部「查看当天全文」。</summary>
    private static async Task<(List<(string Title, string Summary, string Heat, string Link)> Items, string ArticleLink)>
        FetchSixtySecondsAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var url = ctx.Variables.TryGetValue("scripts_60s_url", out var configured) && !string.IsNullOrEmpty(configured)
            ? configured
            : SixtySecondsUrl;
        var root = await GetJsonAsync(ctx, url, ct);
        var list = new List<(string, string, string, string)>();
        if (root?["data"]?["news"] is not JArray news)
        {
            return (list, string.Empty);
        }
        var articleLink = OneLine(root["data"]?["link"]?.ToString());

        // 封面气泡（当天报纸头图）先行，失败不阻断正文
        var cover = root["data"]?["cover"]?.ToString();
        if (!string.IsNullOrEmpty(cover))
        {
            try
            {
                await ctx.Notify.SendImageAsync(cover, $"每天60秒读懂世界 · {root["data"]?["date"]}", ct: ct);
            }
            catch (Exception e)
            {
                ctx.Log($"封面图推送失败（忽略）：{e.Message}");
            }
        }

        foreach (var item in news)
        {
            ct.ThrowIfCancellationRequested();
            var text = OneLine(item?.ToString());
            if (!string.IsNullOrEmpty(text))
            {
                list.Add((text, string.Empty, string.Empty, string.Empty));
            }
        }
        return (list, articleLink);
    }

    // ────────────────────────── 去重记录 ──────────────────────────

    private static async Task<HashSet<string>> LoadSeenKeysAsync(
        QuantumTaskContext ctx, string source, DateTime now, int seenHours, CancellationToken ct)
    {
        var keys = new HashSet<string>();
        try
        {
            var rows = await ctx.CustomData.QueryAsync(SeenType, new QuantumCustomDataFilter
            {
                Data1 = source,
                CreateTimeStart = now.AddHours(-seenHours),
            }, ct);
            foreach (var row in rows)
            {
                if (!string.IsNullOrEmpty(row.Data2))
                {
                    keys.Add(row.Data2);
                }
            }
        }
        catch (Exception e)
        {
            ctx.Log($"去重记录读取失败（按全部新话题处理）：{e.Message}");
        }
        return keys;
    }

    private static async Task RecordSeenAsync(
        QuantumTaskContext ctx, string source, List<string> freshKeys, DateTime now, CancellationToken ct)
    {
        if (freshKeys.Count == 0)
        {
            return;
        }
        try
        {
            await ctx.CustomData.SaveTitleAsync(SeenType, "热点去重记录", ["来源", "标题键", "推送时间"], ct);
            var rows = freshKeys.Select(key => new QuantumCustomDataValue
            {
                Type = SeenType,
                Data1 = source,
                Data2 = key,
                Data3 = now.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            }).ToList();
            await ctx.CustomData.AddAsync(rows, ct);
            ctx.Log($"写入去重记录 {rows.Count} 条。");
        }
        catch (Exception e)
        {
            ctx.Log($"去重记录写入失败（下次推送可能重复标新）：{e.Message}");
        }
    }

    private static async Task CleanupAsync(QuantumTaskContext ctx, DateTime now, CancellationToken ct)
    {
        try
        {
            var stale = await ctx.CustomData.QueryAsync(SeenType, new QuantumCustomDataFilter
            {
                CreateTimeEnd = now.AddHours(-CleanupHours),
            }, ct);
            var ids = stale.Select(row => row.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (ids.Count > 0)
            {
                await ctx.CustomData.DeleteAsync(ids, ct);
                ctx.Log($"清理过期去重记录 {ids.Count} 条。");
            }
        }
        catch (Exception e)
        {
            ctx.Log($"去重记录清理失败（忽略）：{e.Message}");
        }
    }

    // ────────────────────────── 组装 ──────────────────────────

    private static string BuildMessage(
        string source,
        List<(string Title, string Summary, string Heat, string Link)> items,
        List<string> freshKeys,
        DateTime now,
        string articleLink)
    {
        var fresh = new HashSet<string>(freshKeys);
        var body = new StringBuilder();
        body.Append($"{now.Month}月{now.Day}日 {now:HH:mm} 更新\n");
        for (int i = 0; i < items.Count; i++)
        {
            var marker = fresh.Contains(NormalizeKey(items[i].Title)) ? QuantumText.Tag("orange", "新") : string.Empty;
            // 详情为命名链接：只显示「详情」二字，点击直达（不占链接地址版面）
            var detail = string.IsNullOrEmpty(items[i].Link) ? string.Empty : $" 🔗{Link("详情", items[i].Link)}";
            body.Append($"\n{i + 1}. {marker}{items[i].Title}{detail}\n");
            if (!string.IsNullOrEmpty(items[i].Summary))
            {
                var heat = string.IsNullOrEmpty(items[i].Heat) ? string.Empty : $"（{items[i].Heat}）";
                body.Append($"　　{items[i].Summary}{heat}\n");
            }
        }
        if (source == "60s")
        {
            if (!string.IsNullOrEmpty(articleLink))
            {
                body.Append($"\n📄 {Link("查看当天全文", articleLink)}");
            }
            return body.ToString().TrimEnd();
        }
        body.Append("\n以上点「详情」直达知乎问题页；橙色「新」标为本次窗口期新上榜话题。");
        return body.ToString();
    }

    /// <summary>命名链接标记 {{link:文字|URL}}：App/Web 气泡内只显示文字、点击跳 URL。
    /// （脚本内联生成纯字符串，不依赖后端 QuantumText 版本——两端口径见各自富文本解析器。）</summary>
    private static string Link(string text, string url) => $"{{{{link:{text}|{url}}}}}";

    /// <summary>标题键：压掉所有空白后取前 50 字，供窗口期去重比对。</summary>
    private static string NormalizeKey(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title)
        {
            if (!char.IsWhiteSpace(ch))
            {
                sb.Append(ch);
            }
        }
        return sb.Length <= 50 ? sb.ToString() : sb.ToString(0, 50);
    }

    /// <summary>压掉换行/连续空白为单空格（excerpt 里常带换行，气泡里会破版式）。</summary>
    private static string OneLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        return string.Join(' ', text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TrimText(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    // ────────────────────────── HTTP 公共 ──────────────────────────

    /// <summary>GET JSON：显式带 UA/Accept（部分站点对空头的请求回 403，见 btsow 转换记录），失败/非 2xx 返回 null。</summary>
    private static async Task<JObject> GetJsonAsync(QuantumTaskContext ctx, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; QuantumTask/1.0)");
            using var response = await ctx.Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                ctx.Log($"GET {HostOf(url)} → HTTP {(int)response.StatusCode}");
                return null;
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrEmpty(body) ? null : JObject.Parse(body);
        }
        catch (Exception e)
        {
            ctx.Log($"GET {HostOf(url)} 异常：{e.Message}");
            return null;
        }
    }

    private static string HostOf(string url)
    {
        var trimmed = url ?? string.Empty;
        int start = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return trimmed;
        }
        start += 3;
        int end = trimmed.IndexOf('/', start);
        return end < 0 ? trimmed[start..] : trimmed[start..end];
    }
}
