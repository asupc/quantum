// ============================================================================
// 热点速览（2026-09-22 改版为「板块新闻」）：定时/指令把指定板块的新增新闻推送到 App 会话流，
// 去重窗口期内已推送过的条目不再重复打扰。
// 后端要求：用到 2026-09-17 的 ctx.CustomData 契约（全列过滤 QueryAsync / AddAsync / DeleteAsync /
//   SaveTitleAsync）做「已推送」去重，旧构建会拒绝保存，请先部署含该契约的后端。
// 数据源（2026-09-22 全部实测可用，免 key 免 Cookie；原 zhihu 热榜按 IP 风控、新浪分类滚动
//   roll/get 已全量回「列表和页面没有经过注册」故弃用；60s 的 baidu/hotlist、bilibili、finance
//   端点已 404 勿再试）：
//   财经 —— 新浪 7×24 zhibo.sina.com.cn（zhibo_id=152），与「财经快讯」任务同源已生产验证，
//     条目为整句快讯，条目键用源内唯一 id；无独立「全球/科技」频道源，「要闻」（60s 当日国内外
//     要闻，指令别名 全球/国际/早报/60s）与「知乎热榜」（常含科技/国际话题）作近似覆盖；
//   要闻 —— 60s API /v2/60s（当日要闻清单，尾部附当天微信原文链接，沿用封面图推送）；
//   热榜系 —— 60s API /v2/zhihu、/weibo、/toutiao、/baidu/realtime、/douyin（title/link/热度，
//     百度带 desc 摘要、知乎带 detail 摘要与热度文案，格式统一解析）。
// 板块表：财经 finance / 要闻 news（别名 全球、国际、早报、60s）/ 知乎 zhihu / 微博 weibo /
//   头条 toutiao / 百度 baidu / 抖音 douyin；默认推送 财经、要闻、知乎。
// 触发与配置：
//   定时 Cron 维持每 4 小时一轮；指令触发：发「热点」走配置板块，发「热点 财经 知乎」临时只推
//   指定板块（中文名/英文键均可）；指令带了文字但没匹配到任何板块时回复可用板块清单；
//   Web 手动调试：配变量 command=热点 或 热点 财经 即可（兼容 hotnews_msg 变量名）。
// 环境变量：
//   scripts_hotnews_boards（可选，逗号分隔板块，默认 财经,要闻,知乎）；
//   scripts_hotnews_max（可选，每板块条数上限，默认 5，限 3~10）；
//   scripts_hotnews_seen_hours（可选，去重窗口小时数，默认 36）；
//   scripts_60s_url（可选，60s 要闻接口地址整体替换用）。
// 去重设计（custom data 类型 hotnews_seen，表头：板块/标题键/推送时间）：
//   每板块只推窗口期内未推送过的条目（同轮已推过的标题跨板块也不重复出现）；本次推送的条目键
//   写入记录（Data1 存板块键；财经用源内 id、其余用去空白标题键），窗口外自动清理（>72h 删除）；
//   全部板块都无新增时只发一条「无新增」提示，不发正文；个别板块拉取失败在正文尾部标注，
//   全部失败才报错。注意：SaveTitleAsync 每轮只调一次——同轮重复调会触发后端实体跟踪冲突
//   （2026-09-22 试运行实测），故表头保存独立于逐板块写入。
// 注意事项：
//   1) 新浪 7×24 偶发按出口 IP 风控，被拦的轮次该板块自动标注失败，不影响其他板块；
//   2) 日期/时间取服务器本地时区，Docker 部署请确认容器时区为 Asia/Shanghai。
// ============================================================================
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class HotNewsDigestTask : IQuantumTask
{
    private const string Sina724Url = "https://zhibo.sina.com.cn/api/zhibo/feed?page=1&page_size=50&zhibo_id=152&tag_id=0";
    private const string SixtyBase = "https://60s.viki.moe/v2";
    private const string SeenType = "hotnews_seen";
    private const int DefaultMaxItems = 5;
    private const int DefaultSeenHours = 36;
    private const int CleanupHours = 72;

    /// <summary>一个新闻板块：英文键 / 展示名 / 端点（"sina724" 为新浪 7×24，其余为 60s API 路径）/ 触发别名。</summary>
    private sealed record NewsBoard(string Key, string Name, string Endpoint, string[] Aliases);

    private static readonly NewsBoard[] BoardTable =
    {
        new("finance", "财经", "sina724", new[] { "财经", "finance" }),
        new("news", "要闻", "/60s", new[] { "要闻", "全球", "国际", "早报", "60s", "news" }),
        new("zhihu", "知乎", "/zhihu", new[] { "知乎", "zhihu" }),
        new("weibo", "微博", "/weibo", new[] { "微博", "weibo" }),
        new("toutiao", "头条", "/toutiao", new[] { "头条", "toutiao" }),
        new("baidu", "百度", "/baidu/realtime", new[] { "百度", "baidu" }),
        new("douyin", "抖音", "/douyin", new[] { "抖音", "douyin" }),
    };

    /// <summary>一条新闻：条目键 / 标题（或快讯全文）/ 摘要 / 热度文案 / 发稿时刻 / 原文链接。</summary>
    private sealed record NewsItem(string Key, string Title, string Summary, string Heat, string Clock, string Link);

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var now = DateTime.Now;
        var boards = ResolveBoards(ctx, out var from, out var commandMiss);
        if (commandMiss)
        {
            await ctx.Notify.SendAsync("热点速览",
                "未识别到板块。可用板块：财经、要闻（全球/国际/早报/60s）、知乎、微博、头条、百度、抖音。\n例如发送「热点 财经 知乎」；也可配置环境变量 scripts_hotnews_boards 固定板块。", ct);
            return;
        }
        int max = ResolveMax(ctx);
        int seenHours = ResolveSeenHours(ctx);
        ctx.Log($"板块（{from}）：{string.Join("、", boards.Select(b => b.Name))}，每板块上限：{max}，去重窗口：{seenHours}h");

        // ── 逐板块拉取（个别失败记名标注，全部失败才报错） ──
        var fetched = new List<(NewsBoard Board, List<NewsItem> Items, string ArticleLink)>();
        var failed = new List<string>();
        foreach (var board in boards)
        {
            ct.ThrowIfCancellationRequested();
            var (items, articleLink) = await FetchBoardAsync(ctx, board, ct);
            if (items.Count == 0)
            {
                failed.Add(board.Name);
                ctx.Log($"板块「{board.Name}」拉取失败。");
            }
            else
            {
                fetched.Add((board, items, articleLink));
            }
        }
        if (fetched.Count == 0)
        {
            await ctx.Notify.SendAsync("热点速览", "所有板块都没取到数据，请稍后重试或查看任务日志。", ct);
            return;
        }

        // ── 去重：窗口期内未推送过的才算新增；同轮跨板块同题只推一次 ──
        var pushedThisRun = new HashSet<string>();
        var freshByBoard = new List<(NewsBoard Board, List<NewsItem> Items, string ArticleLink)>();
        foreach (var (board, items, articleLink) in fetched)
        {
            ct.ThrowIfCancellationRequested();
            var seenKeys = await LoadSeenKeysAsync(ctx, board.Key, now, seenHours, ct);
            var fresh = new List<NewsItem>();
            foreach (var item in items)
            {
                if (fresh.Count >= max)
                {
                    break;
                }
                if (!seenKeys.Contains(item.Key) && pushedThisRun.Add(item.Key))
                {
                    fresh.Add(item);
                }
            }
            if (fresh.Count == 0)
            {
                ctx.Log($"板块「{board.Name}」本轮无新增（上榜 {items.Count} 条均为窗口期内已推送）。");
                continue;
            }
            freshByBoard.Add((board, fresh, articleLink));
        }
        if (freshByBoard.Count == 0)
        {
            ctx.Log("各板块上榜条目均为窗口期内已推送过的老话题，不重复推送正文。");
            await ctx.Notify.SendAsync("热点速览 · 板块新闻",
                "本轮无新增新闻：各板块上榜条目与近期推送重复，暂不重复推送。", ct);
            return;
        }
        ctx.Log($"各板块合计新增 {freshByBoard.Sum(x => x.Items.Count)} 条，开始组装推送。");

        // ── 组装并推送 ──
        var body = BuildMessage(freshByBoard, failed, now);
        ctx.Log(body);
        await ctx.Notify.SendAsync("热点速览 · 板块新闻", body, ct);

        // ── 记录与清理（失败只记日志，不影响已完成的推送） ──
        await EnsureSeenTitleAsync(ctx, ct);
        foreach (var (board, fresh, _) in freshByBoard)
        {
            await RecordSeenAsync(ctx, board.Key, fresh.Select(item => item.Key).ToList(), now, ct);
        }
        await CleanupAsync(ctx, now, ct);
    }

    // ────────────────────────── 板块解析 ──────────────────────────

    /// <summary>解析板块清单：指令后缀（「热点 财经 知乎」）优先于 scripts_hotnews_boards 变量，默认 财经,要闻,知乎。</summary>
    private static List<NewsBoard> ResolveBoards(QuantumTaskContext ctx, out string from, out bool commandMiss)
    {
        from = "默认";
        commandMiss = false;
        var command = CommandOf(ctx);
        if (!string.IsNullOrEmpty(command))
        {
            var rest = command.Replace("热点", string.Empty);
            if (HasContent(rest))
            {
                var matched = ParseBoards(rest);
                if (matched.Count > 0)
                {
                    from = "指令指定";
                    return matched;
                }
                commandMiss = true;
                return new List<NewsBoard>();
            }
        }
        if (ctx.Variables.TryGetValue("scripts_hotnews_boards", out var configured)
                && !string.IsNullOrEmpty(configured))
        {
            var matched = ParseBoards(configured);
            if (matched.Count > 0)
            {
                from = "变量配置";
                return matched;
            }
        }
        return ParseBoards("财经,要闻,知乎");
    }

    /// <summary>指令文本：兼容 command / hotnews_msg 两个变量名。</summary>
    private static string CommandOf(QuantumTaskContext ctx)
    {
        foreach (var name in new[] { "command", "hotnews_msg" })
        {
            if (ctx.Variables.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>按分隔符切词并匹配板块（中文名/英文键，去重保序）。</summary>
    private static List<NewsBoard> ParseBoards(string text)
    {
        var result = new List<NewsBoard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in text.Split(new[] { ',', '，', '、', ' ', '/', '|', ';', '；' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var board = MatchBoard(token);
            if (board != null && seen.Add(board.Key))
            {
                result.Add(board);
            }
        }
        return result;
    }

    private static NewsBoard MatchBoard(string token)
    {
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }
        foreach (var board in BoardTable)
        {
            if (string.Equals(board.Key, t, StringComparison.OrdinalIgnoreCase))
            {
                return board;
            }
            foreach (var alias in board.Aliases)
            {
                if (t.Contains(alias, StringComparison.Ordinal))
                {
                    return board;
                }
            }
        }
        return null;
    }

    /// <summary>除分隔符与空白外是否还有内容（判断指令是否带了后缀文字）。</summary>
    private static bool HasContent(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch) && ",，、/|;；".IndexOf(ch) < 0)
            {
                return true;
            }
        }
        return false;
    }

    private static int ResolveMax(QuantumTaskContext ctx)
    {
        if (ctx.Variables.TryGetValue("scripts_hotnews_max", out var configured)
            && int.TryParse(configured, out var value))
        {
            return Math.Clamp(value, 3, 10);
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

    /// <summary>按板块端点拉取：sina724 走新浪 7×24 专析，其余走 60s API（热榜系统一解析）。ArticleLink 仅 60s 要闻有值。</summary>
    private static async Task<(List<NewsItem> Items, string ArticleLink)> FetchBoardAsync(
        QuantumTaskContext ctx, NewsBoard board, CancellationToken ct)
    {
        if (board.Endpoint == "sina724")
        {
            return (await FetchSina724Async(ctx, ct), string.Empty);
        }
        var url = board.Key == "news"
                  && ctx.Variables.TryGetValue("scripts_60s_url", out var overrideUrl)
                  && !string.IsNullOrEmpty(overrideUrl)
            ? overrideUrl
            : SixtyBase + board.Endpoint;
        return await FetchSixtyAsync(ctx, board, url, ct);
    }

    /// <summary>新浪 7×24：rich_text 快讯全文 + create_time；条目键用源内 id；链接取 docurl（缺失兜底 ext 内桌面版）。</summary>
    private static async Task<List<NewsItem>> FetchSina724Async(QuantumTaskContext ctx, CancellationToken ct)
    {
        var root = await GetJsonAsync(ctx, Sina724Url, ct);
        var list = new List<NewsItem>();
        if (root?["result"]?["data"]?["feed"]?["list"] is not JArray feed)
        {
            ctx.Log($"板块 finance：响应结构异常 → {Snippet(root?.ToString(Newtonsoft.Json.Formatting.None))}");
            return list;
        }
        foreach (var item in feed)
        {
            ct.ThrowIfCancellationRequested();
            var key = item["id"]?.ToString();
            var title = OneLine(item["rich_text"]?.ToString());
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(title))
            {
                continue;
            }
            var link = OneLine(item["docurl"]?.ToString());
            if (string.IsNullOrEmpty(link))
            {
                link = ExtDocUrl(item["ext"]?.ToString());
            }
            list.Add(new NewsItem(key, title, string.Empty, string.Empty, ClockOf(item["create_time"]?.ToString()), link));
        }
        return list;
    }

    /// <summary>新浪 ext（JSON 字符串）内桌面版 docurl 提取，解析失败给空串。</summary>
    private static string ExtDocUrl(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return string.Empty;
        }
        try
        {
            return OneLine(JObject.Parse(ext)["docurl"]?.ToString());
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>60s API 要闻：news 为一句话条目数组，无逐条链接；link 为当天微信原文（随板块段尾部附出）。</summary>
    private static async Task<(List<NewsItem> Items, string ArticleLink)> FetchSixtyAsync(
        QuantumTaskContext ctx, NewsBoard board, string url, CancellationToken ct)
    {
        var root = await GetJsonAsync(ctx, url, ct);
        var list = new List<NewsItem>();
        if (root is null)
        {
            return (list, string.Empty);
        }
        if (board.Key == "news")
        {
            if (root["data"]?["news"] is not JArray news)
            {
                ctx.Log($"板块 news：响应结构异常 → {Snippet(root.ToString(Newtonsoft.Json.Formatting.None))}");
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
                    list.Add(new NewsItem(NormalizeKey(text), text, string.Empty, string.Empty, string.Empty, string.Empty));
                }
            }
            return (list, articleLink);
        }

        // 热榜系：title/link(/url) + 热度（hot_value→万 / hot_value_desc / score_desc）+ 摘要（desc/detail）
        if (root["data"] is not JArray hot)
        {
            ctx.Log($"板块 {board.Key}：响应结构异常 → {Snippet(root.ToString(Newtonsoft.Json.Formatting.None))}");
            return (list, string.Empty);
        }
        foreach (var item in hot)
        {
            ct.ThrowIfCancellationRequested();
            var title = OneLine(item["title"]?.ToString());
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }
            var link = OneLine(item["link"]?.ToString());
            if (string.IsNullOrEmpty(link))
            {
                link = OneLine(item["url"]?.ToString());
            }
            var heat = OneLine(item["hot_value_desc"]?.ToString());
            if (string.IsNullOrEmpty(heat))
            {
                heat = OneLine(item["score_desc"]?.ToString());
            }
            if (string.IsNullOrEmpty(heat) && long.TryParse(item["hot_value"]?.ToString(), out var hotValue))
            {
                heat = hotValue >= 10000 ? $"{hotValue / 10000}万热度" : $"{hotValue}热度";
            }
            var summarySrc = OneLine(item["desc"]?.ToString());
            if (string.IsNullOrEmpty(summarySrc))
            {
                summarySrc = OneLine(item["detail"]?.ToString());
            }
            var summary = string.IsNullOrEmpty(summarySrc) || summarySrc == title ? string.Empty : TrimText(summarySrc, 60);
            list.Add(new NewsItem(NormalizeKey(title), title, summary, heat, string.Empty, link));
        }
        return (list, string.Empty);
    }

    /// <summary>新浪 create_time（yyyy-MM-dd HH:mm:ss）取 HH:mm，格式不符给空串。</summary>
    private static string ClockOf(string rawTime)
    {
        if (!string.IsNullOrEmpty(rawTime))
        {
            int space = rawTime.IndexOf(' ');
            if (space >= 0 && rawTime.Length >= space + 6)
            {
                return rawTime.Substring(space + 1, 5);
            }
        }
        return string.Empty;
    }

    // ────────────────────────── 去重记录 ──────────────────────────

    /// <summary>表头保存：每轮只调一次（同轮重复调 SaveTitleAsync 会触发后端实体跟踪冲突，见头注释）。</summary>
    private static async Task EnsureSeenTitleAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.CustomData.SaveTitleAsync(SeenType, "热点去重记录", ["板块", "标题键", "推送时间"], ct);
        }
        catch (Exception e)
        {
            ctx.Log($"去重表头保存失败（忽略）：{e.Message}");
        }
    }

    private static async Task<HashSet<string>> LoadSeenKeysAsync(
        QuantumTaskContext ctx, string boardKey, DateTime now, int seenHours, CancellationToken ct)
    {
        var keys = new HashSet<string>();
        try
        {
            var rows = await ctx.CustomData.QueryAsync(SeenType, new QuantumCustomDataFilter
            {
                Data1 = boardKey,
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
        QuantumTaskContext ctx, string boardKey, List<string> freshKeys, DateTime now, CancellationToken ct)
    {
        if (freshKeys.Count == 0)
        {
            return;
        }
        try
        {
            var rows = freshKeys.Select(key => new QuantumCustomDataValue
            {
                Type = SeenType,
                Data1 = boardKey,
                Data2 = key,
                Data3 = now.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            }).ToList();
            await ctx.CustomData.AddAsync(rows, ct);
            ctx.Log($"板块 {boardKey} 写入去重记录 {rows.Count} 条。");
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
        List<(NewsBoard Board, List<NewsItem> Items, string ArticleLink)> freshByBoard,
        List<string> failedBoards,
        DateTime now)
    {
        var body = new StringBuilder();
        body.Append($"{now.Month}月{now.Day}日 {now:HH:mm} 板块新闻新增 {freshByBoard.Sum(x => x.Items.Count)} 条\n");
        foreach (var (board, items, articleLink) in freshByBoard)
        {
            body.Append($"\n【{board.Name}】\n");
            for (int i = 0; i < items.Count; i++)
            {
                // 详情为命名链接：只显示「详情」二字，点击直达（不占链接地址版面）
                var detail = string.IsNullOrEmpty(items[i].Link) ? string.Empty : $" 🔗{Link("详情", items[i].Link)}";
                // 摘要行为空时把发稿时刻前缀到标题行（财经快讯场景）
                var lead = !string.IsNullOrEmpty(items[i].Clock) && string.IsNullOrEmpty(items[i].Summary)
                    ? $"{items[i].Clock} "
                    : string.Empty;
                body.Append($"{i + 1}. {lead}{items[i].Title}{detail}\n");
                if (!string.IsNullOrEmpty(items[i].Summary))
                {
                    var heat = string.IsNullOrEmpty(items[i].Heat) ? string.Empty : $"（{items[i].Heat}）";
                    body.Append($"　　{items[i].Summary}{heat}\n");
                }
                else if (!string.IsNullOrEmpty(items[i].Heat))
                {
                    body.Append($"　　{items[i].Heat}\n");
                }
            }
            if (board.Key == "news" && !string.IsNullOrEmpty(articleLink))
            {
                body.Append($"　　📄 {Link("查看当天全文", articleLink)}\n");
            }
        }
        if (failedBoards.Count > 0)
        {
            body.Append($"\n⚠️ 以下板块本轮拉取失败：{string.Join("、", failedBoards)}\n");
        }
        body.Append("\n以上点「详情」直达原文；仅推送近期未推过的新增条目。");
        return body.ToString().TrimEnd();
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

    /// <summary>压掉换行/连续空白为单空格（接口字段常带换行，气泡里会破版式）。</summary>
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

    /// <summary>响应片段：压成单行取前 300 字，供结构异常时排障。</summary>
    private static string Snippet(string text)
        => string.IsNullOrEmpty(text) ? "(空)" : TrimText(OneLine(text), 300);

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
