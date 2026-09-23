// ============================================================================
// 财经快讯（2026-09-17 新写）：定时/指令把财经 7×24 快讯按「只推没推过的」增量推送到 App 会话流。
// 后端要求：用到 2026-09-17 的 ctx.CustomData 契约（全列过滤 QueryAsync / AddAsync / DeleteAsync /
//   SaveTitleAsync）做「已推送」去重，旧构建会拒绝保存，请先部署含该契约的后端。
// 数据源（2026-09-17 实测选型，全部免 key；财联社 nodeapi 已改版回 HTML、华尔街见闻接口存活但
//   软广混杂，均弃用）：
//   新浪 7×24 —— zhibo.sina.com.cn/api/zhibo/feed（zhibo_id=152），免 key 免请求头，ext 内带关联
//     股票/指数，正文纯文本，默认主源；
//   金十数据 —— flash-api.jin10.com/get_flash_list，需带网页端公开请求头
//     x-app-id: SO1EJGmNgCtmpcPF / x-version: 1.0.0（头是网页端公开值，失效则换源），
//     important 字段可标 ⭐ 重要快讯，正文混的 HTML 标签/直播广告条目已清洗；
//   东方财富 7×24 —— np-listapi.eastmoney.com getFastNewsList（fastColumn=102 重要栏），
//     响应实为 UTF-8（curl 直看显示的「乱码」是 Windows 终端管道假象，勿按 GBK 转码）。
// 详情链接（2026-09-19 增；2026-09-20 改命名链接提信息密度）：新浪条目自带 docurl（缺则取 ext
//   内桌面版 docurl）；金十/东财接口无链接字段，按 id 拼 flash.jin10.com/detail/{id} 与
//   finance.eastmoney.com/a/{code}.html（两格式 2026-09-19 实测 200 且标题相符）；
//   推送以 {{link:详情|URL}} 命名链接附在每条快讯末（App/Web 2026-09-20 起支持：只显示「详情」、
//   点击跳转，旧版客户端按字面显示标记），存档新增 Data7「链接」列（仍存完整 URL）。
// 触发与配置：
//   定时 Cron 推荐 0 0/30 8-23 * * ?（8:00~23:30 每 30 分钟收一次增量）；
//   指令触发：触发指令填 ^财经.* 勾正则，指令环境变量名填 command——
//     App 端发「财经」走默认源，发「财经 金十」「财经 东财」「财经 新浪」临时切换源；
//   Web 手动调试：配变量 command=财经 或 财经 金十 即可。
// 环境变量：
//   scripts_finflash_source（可选，默认 sina，可 jin10/em）；scripts_finflash_max（可选，默认 10，限 3~20）；
//   scripts_finflash_seen_hours（可选，去重窗口小时数，默认 24）；
//   scripts_finflash_keep_days（可选，存档保留天数，默认 30，0=永久保留）。
// 存档设计（custom data 类型 finflash_news，表头：来源/条目键/时间/正文/关联/重要/链接）：
//   推送成功后把本次抓到的全部新增条目（含未列出的）全文落库，供 Web 端自定义数据页查询/CSV 导出；
//   Data 列在双库均为长文本（MySql longtext / Sqlite TEXT，2026-09-17 核对迁移），正文存清洗后全文不截断；
//   按保留窗口自动清理（默认 30 天，设 0 永久保留）。
// 去重设计（custom data 类型 finflash_seen，表头：来源/条目键/推送时间）：
//   「增量」语义——每次把抓到的 50 条里「窗口期内没推过的」按时间倒序推最新 max 条，全部记为已读
//   （含未列出的更早条目，避免下次重复）；没有新增时推一条「回顾最新 3 条」，不写记录；
//   窗口外自动清理（>72h 的记录删除）。主源拉取失败自动落备源（sina→jin10→em 依序）。
// 注意事项：
//   1) 条目键用源内唯一 id（新浪数字 id / 金十 id / 东财 code），比标题键精确，但跨源不通用
//      （去重记录按源隔离，切源后当批全部视为新条目属预期）；
//   2) 金十接口按出口 IP 风控的可能较低，但请求头若被站点废弃会 403，届时换 scripts_finflash_source；
//   3) 日期/时间取服务器本地时区，Docker 部署请确认容器时区为 Asia/Shanghai。
// ============================================================================
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class FinanceFlashTask : IQuantumTask
{
    private const string SinaFeedUrl = "https://zhibo.sina.com.cn/api/zhibo/feed?page=1&page_size=50&zhibo_id=152&tag_id=0";
    private const string Jin10FlashUrl = "https://flash-api.jin10.com/get_flash_list?channel=-8200&vip=1&max_time=.5";
    private const string EastmoneyUrl = "https://np-listapi.eastmoney.com/comm/web/getFastNewsList?client=web&biz=web_724&fastColumn=102&sortEnd=&pageSize=50&req_trace=1";
    private const string SeenType = "finflash_seen";
    private const string NewsType = "finflash_news";
    private const int DefaultKeepDays = 30;
    private const int DefaultMaxItems = 10;
    private const int DefaultSeenHours = 24;
    private const int CleanupHours = 72;

    /// <summary>一条快讯：键（源内 id）/ 原始时间 / 正文（清洗后全文）/ 重要标 / 尾注（关联标的等）/ 详情链接。</summary>
    private sealed record FlashItem(string Key, string RawTime, string Text, string Mark, string Tail, string Link)
    {
        /// <summary>展示用 HH:mm（解析失败给空串，条目照常列出）。</summary>
        public string Clock
        {
            get
            {
                if (string.IsNullOrEmpty(RawTime))
                {
                    return string.Empty;
                }
                int space = RawTime.IndexOf(' ');
                return space >= 0 && RawTime.Length >= space + 6 ? RawTime.Substring(space + 1, 5) : string.Empty;
            }
        }
    }

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var now = DateTime.Now;
        string source = ResolveSource(ctx);
        int max = ResolveMax(ctx);
        int seenHours = ResolveSeenHours(ctx);
        ctx.Log($"源：{source}，条数上限：{max}，去重窗口：{seenHours}h");

        // ── 拉取（主源 0 条自动落备源，sina→jin10→em） ──
        var fetched = new List<FlashItem>();
        string actual = source;
        foreach (var candidate in OrderSources(source))
        {
            ct.ThrowIfCancellationRequested();
            fetched = candidate switch
            {
                "sina" => await FetchSinaAsync(ctx, ct),
                "jin10" => await FetchJin10Async(ctx, ct),
                _ => await FetchEastmoneyAsync(ctx, ct),
            };
            if (fetched.Count > 0)
            {
                actual = candidate;
                if (candidate != source)
                {
                    ctx.Log($"源 {source} 拉取失败，已切换 {candidate}。");
                }
                break;
            }
            ctx.Log($"源 {candidate} 拉取失败。");
        }
        if (fetched.Count == 0)
        {
            await ctx.Notify.SendAsync("财经快讯", "三个源都没取到数据，请稍后重试或查看任务日志。", ct);
            return;
        }

        // ── 增量过滤：窗口期内推过的键剔除 ──
        var seenKeys = await LoadSeenKeysAsync(ctx, actual, now, seenHours, ct);
        var freshItems = fetched.Where(item => !seenKeys.Contains(item.Key)).ToList();

        // ── 组装并推送 ──
        if (freshItems.Count == 0)
        {
            var review = BuildReviewMessage(actual, fetched.Take(3).ToList(), seenHours, now);
            ctx.Log(review);
            await ctx.Notify.SendAsync($"财经快讯 · {SourceName(actual)}", review, ct);
            return;
        }
        var picked = freshItems.Take(max).ToList();
        int omitted = freshItems.Count - picked.Count;
        var body = BuildMessage(actual, picked, omitted, now);
        ctx.Log(body);
        await ctx.Notify.SendAsync($"财经快讯 · {SourceName(actual)}", body, ct);

        // ── 记录、存档与清理（本次抓到的全部新条目记为已读并落档，含未列出的；失败只记日志） ──
        await RecordSeenAsync(ctx, actual, freshItems.Select(item => item.Key).ToList(), now, ct);
        await SaveNewsAsync(ctx, actual, freshItems, ct);
        await CleanupAsync(ctx, now, ct);
        await CleanupNewsAsync(ctx, now, ResolveKeepDays(ctx), ct);
    }

    /// <summary>解析源：指令后缀（「财经 金十」）优先于 scripts_finflash_source 变量，默认 sina。</summary>
    private static string ResolveSource(QuantumTaskContext ctx)
    {
        if (ctx.Variables.TryGetValue("command", out var command) && !string.IsNullOrEmpty(command))
        {
            var rest = command.Replace("财经", string.Empty).Trim();
            if (rest.Length > 0)
            {
                if (rest.Contains("金十", StringComparison.Ordinal))
                {
                    return "jin10";
                }
                if (rest.Contains("东财", StringComparison.Ordinal) || rest.Contains("东方", StringComparison.Ordinal))
                {
                    return "em";
                }
                if (rest.Contains("新浪", StringComparison.Ordinal))
                {
                    return "sina";
                }
            }
        }
        if (ctx.Variables.TryGetValue("scripts_finflash_source", out var configured) && !string.IsNullOrEmpty(configured))
        {
            var trimmed = configured.Trim();
            if (trimmed is "jin10" or "em")
            {
                return trimmed;
            }
        }
        return "sina";
    }

    /// <summary>兜底顺序：首选源排最前，其余按 sina→jin10→em 的固定序补位。</summary>
    private static IEnumerable<string> OrderSources(string primary)
        => new[] { primary, "sina", "jin10", "em" }.Distinct();

    private static string SourceName(string source)
        => source switch
        {
            "jin10" => "金十数据",
            "em" => "东方财富",
            _ => "新浪7×24",
        };

    private static int ResolveMax(QuantumTaskContext ctx)
    {
        if (ctx.Variables.TryGetValue("scripts_finflash_max", out var configured)
            && int.TryParse(configured, out var value))
        {
            return Math.Clamp(value, 3, 20);
        }
        return DefaultMaxItems;
    }

    private static int ResolveSeenHours(QuantumTaskContext ctx)
    {
        if (ctx.Variables.TryGetValue("scripts_finflash_seen_hours", out var configured)
            && int.TryParse(configured, out var value) && value > 0)
        {
            return value;
        }
        return DefaultSeenHours;
    }

    // ────────────────────────── 数据源 ──────────────────────────

    /// <summary>新浪 7×24：rich_text 正文 + create_time；详情链接取条目顶层 docurl（移动版，缺失兜底 ext 桌面版）；
    /// ext 内 stocks 取前两个拼关联标的尾注。</summary>
    private static async Task<List<FlashItem>> FetchSinaAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var root = await GetJsonAsync(ctx, SinaFeedUrl, ct);
        var list = new List<FlashItem>();
        if (root?["result"]?["data"]?["feed"]?["list"] is not JArray feed)
        {
            return list;
        }
        foreach (var item in feed)
        {
            ct.ThrowIfCancellationRequested();
            var key = item["id"]?.ToString();
            var text = OneLine(item["rich_text"]?.ToString());
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text))
            {
                continue;
            }
            var (tail, extLink) = ParseSinaExt(item["ext"]?.ToString());
            var link = OneLine(item["docurl"]?.ToString());
            if (string.IsNullOrEmpty(link))
            {
                link = extLink;
            }
            list.Add(new FlashItem(key, OneLine(item["create_time"]?.ToString()), text, string.Empty, tail, link));
        }
        return list;
    }

    /// <summary>新浪 ext（JSON 字符串）解析：关联股票/指数名取前两个拼尾注（如「（法拉第未来、纳斯达克）」），
    /// 并带出桌面版 docurl（顶层 docurl 缺失时作链接兜底）。</summary>
    private static (string Tail, string Link) ParseSinaExt(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return (string.Empty, string.Empty);
        }
        try
        {
            var obj = JObject.Parse(ext);
            var names = (obj["stocks"] as JArray)
                ?.Select(stock => OneLine(stock?["key"]?.ToString()))
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct()
                .Take(2)
                .ToList();
            var tail = names is { Count: > 0 } ? $"（关联：{string.Join("、", names)}）" : string.Empty;
            return (tail, OneLine(obj["docurl"]?.ToString()));
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>金十：data.content 正文 + time + important（1 为重要快讯，标 ⭐）；需带网页端公开请求头；
    /// 详情链接按 id 拼 flash.jin10.com/detail/{id}。</summary>
    private static async Task<List<FlashItem>> FetchJin10Async(QuantumTaskContext ctx, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            ["x-app-id"] = "SO1EJGmNgCtmpcPF",
            ["x-version"] = "1.0.0",
        };
        var root = await GetJsonAsync(ctx, Jin10FlashUrl, ct, headers);
        var list = new List<FlashItem>();
        if (root?["data"] is not JArray flashes)
        {
            return list;
        }
        foreach (var item in flashes)
        {
            ct.ThrowIfCancellationRequested();
            var key = item["id"]?.ToString();
            var text = StripHtml(item["data"]?["content"]?.ToString());
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text))
            {
                continue;
            }
            if (text.Contains("正在直播", StringComparison.Ordinal) && text.Contains("立即观看", StringComparison.Ordinal))
            {
                continue;
            }
            var mark = item["important"]?.ToString() == "1" ? "⭐" : string.Empty;
            list.Add(new FlashItem(key, OneLine(item["time"]?.ToString()), text, mark, string.Empty,
                $"https://flash.jin10.com/detail/{key}"));
        }
        return list;
    }

    /// <summary>东方财富 7×24：title 即快讯正文 + showTime（2026-09-17 实测响应为 UTF-8，
    /// curl 直看显示的「乱码」是 Windows 终端管道的假象，勿再按 GBK 转码）；
    /// 详情链接按 code 拼 finance.eastmoney.com/a/{code}.html。</summary>
    private static async Task<List<FlashItem>> FetchEastmoneyAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var url = EastmoneyUrl + "&_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var root = await GetJsonAsync(ctx, url, ct);
        var list = new List<FlashItem>();
        if (root?["data"]?["fastNewsList"] is not JArray news)
        {
            return list;
        }
        foreach (var item in news)
        {
            ct.ThrowIfCancellationRequested();
            var key = item["code"]?.ToString();
            var text = OneLine(item["title"]?.ToString());
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text))
            {
                continue;
            }
            list.Add(new FlashItem(key, OneLine(item["showTime"]?.ToString()), text, string.Empty, string.Empty,
                $"https://finance.eastmoney.com/a/{key}.html"));
        }
        return list;
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
            ctx.Log($"去重记录读取失败（按全部新条目处理）：{e.Message}");
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
            await ctx.CustomData.SaveTitleAsync(SeenType, "财经快讯去重记录", ["来源", "条目键", "推送时间"], ct);
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
            ctx.Log($"去重记录写入失败（下次推送可能重复）：{e.Message}");
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

    // ────────────────────────── 快讯存档 ──────────────────────────

    /// <summary>存档：本次全部新增条目（含未列出的）全文写入 finflash_news，供 Web 端查询/导出。</summary>
    private static async Task SaveNewsAsync(
        QuantumTaskContext ctx, string source, List<FlashItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }
        try
        {
            await ctx.CustomData.SaveTitleAsync(NewsType, "财经快讯存档",
                ["来源", "条目键", "时间", "正文", "关联", "重要", "链接"], ct);
            var rows = items.Select(item => new QuantumCustomDataValue
            {
                Type = NewsType,
                Data1 = source,
                Data2 = item.Key,
                Data3 = item.RawTime,
                Data4 = item.Text,
                Data5 = item.Tail,
                Data6 = item.Mark,
                Data7 = item.Link,
            }).ToList();
            await ctx.CustomData.AddAsync(rows, ct);
            ctx.Log($"写入快讯存档 {rows.Count} 条。");
        }
        catch (Exception e)
        {
            ctx.Log($"快讯存档写入失败（不影响推送，下次同条不会重推）：{e.Message}");
        }
    }

    /// <summary>存档按保留窗口清理（keepDays=0 永久保留）。</summary>
    private static async Task CleanupNewsAsync(
        QuantumTaskContext ctx, DateTime now, int keepDays, CancellationToken ct)
    {
        if (keepDays <= 0)
        {
            return;
        }
        try
        {
            var stale = await ctx.CustomData.QueryAsync(NewsType, new QuantumCustomDataFilter
            {
                CreateTimeEnd = now.AddDays(-keepDays),
            }, ct);
            var ids = stale.Select(row => row.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (ids.Count > 0)
            {
                await ctx.CustomData.DeleteAsync(ids, ct);
                ctx.Log($"清理过期快讯存档 {ids.Count} 条。");
            }
        }
        catch (Exception e)
        {
            ctx.Log($"快讯存档清理失败（忽略）：{e.Message}");
        }
    }

    private static int ResolveKeepDays(QuantumTaskContext ctx)
    {
        if (ctx.Variables.TryGetValue("scripts_finflash_keep_days", out var configured)
            && int.TryParse(configured, out var value) && value >= 0)
        {
            return value;
        }
        return DefaultKeepDays;
    }

    // ────────────────────────── 组装 ──────────────────────────

    /// <summary>正文里的重要标渲染为橙色胶囊：存档列仍存 ⭐ 原值，展示与数据互不影响。</summary>
    private static string MarkTag(FlashItem item)
        => string.IsNullOrEmpty(item.Mark) ? string.Empty : QuantumText.Tag("orange", "重要") + " ";

    private static string BuildMessage(string source, List<FlashItem> items, int omitted, DateTime now)
    {
        var body = new StringBuilder();
        body.Append($"{now.Month}月{now.Day}日 {now:HH:mm} · {SourceName(source)}新增 {items.Count} 条");
        if (omitted > 0)
        {
            body.Append($"（更早还有 {omitted} 条未列出）");
        }
        foreach (var (item, index) in items.Select((item, i) => (item, i)))
        {
            var clock = string.IsNullOrEmpty(item.Clock) ? string.Empty : $"[{item.Clock}] ";
            body.Append($"\n\n{index + 1}. {MarkTag(item)}{clock}{TrimText(item.Text, 120)}");
            if (!string.IsNullOrEmpty(item.Tail))
            {
                body.Append($"\n　　{item.Tail}");
            }
            // 详情为命名链接：只显示「详情」二字，点击直达（不占链接地址版面）
            if (!string.IsNullOrEmpty(item.Link))
            {
                body.Append($"　🔗{Link("详情", item.Link)}");
            }
        }
        return body.ToString();
    }

    /// <summary>无新增时的回顾文案：列最新 3 条（不写去重记录，下次抓到真增量照常推）。</summary>
    private static string BuildReviewMessage(string source, List<FlashItem> latest, int seenHours, DateTime now)
    {
        var body = new StringBuilder();
        body.Append($"{now.Month}月{now.Day}日 {now:HH:mm} · 近 {seenHours}h 无新快讯，回顾最新 {latest.Count} 条（{SourceName(source)}）");
        foreach (var (item, index) in latest.Select((item, i) => (item, i)))
        {
            var clock = string.IsNullOrEmpty(item.Clock) ? string.Empty : $"[{item.Clock}] ";
            body.Append($"\n\n{index + 1}. {MarkTag(item)}{clock}{TrimText(item.Text, 120)}");
            if (!string.IsNullOrEmpty(item.Link))
            {
                body.Append($"　🔗{Link("详情", item.Link)}");
            }
        }
        return body.ToString();
    }

    /// <summary>命名链接标记 {{link:文字|URL}}：App/Web 气泡内只显示文字、点击跳 URL。
    /// （脚本内联生成纯字符串，不依赖后端 QuantumText 版本——两端口径见各自富文本解析器。）</summary>
    private static string Link(string text, string url) => $"{{{{link:{text}|{url}}}}}";

    /// <summary>金十正文常混 HTML 标签/实体（&lt;b&gt;、&amp;nbsp; 等），剥掉后再压平。</summary>
    private static string StripHtml(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var plain = System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", " "));
        return OneLine(plain);
    }

    /// <summary>压掉换行/连续空白为单空格（快讯正文里常带换行，气泡里会破版式）。</summary>
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

    /// <summary>GET JSON：显式带 UA/Accept（部分站点对空头的请求回 403），可附额外头（金十）；
    /// 失败/非 2xx 返回 null 交由兜底链换源。</summary>
    private static async Task<JObject> GetJsonAsync(
        QuantumTaskContext ctx, string url, CancellationToken ct,
        IReadOnlyDictionary<string, string> extraHeaders = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; QuantumTask/1.0)");
            if (extraHeaders != null)
            {
                foreach (var (name, value) in extraHeaders)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
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
