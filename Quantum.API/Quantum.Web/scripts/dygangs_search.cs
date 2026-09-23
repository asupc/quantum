// ============================================================================
// 电影港搜索订阅（2026-09-17 新写）：在 https://www.dygangs.net/ 搜索资源清单，
// 把结果（封面图 / 片名 / 年代 / 类型 / 产地 / 资源ID）经 ctx.Notify 推送；
// 用户回复资源ID 后，自动把该资源写入 video_update_monitoring_item 自定义数据
// （监听清单，Data1 来源页 / Data2 片名 / Data3 资源ID / Data4 年代 / Data5 类型 / Data6 产地 / Data7 封面），
// 交由消费脚本 dygangs.cs 盯详情页新磁力并写入 video_update_monitoring。
// 后端要求：需含 2026-09-17 的 ctx.CustomData 契约扩展（Data6-Data15 / 过滤器 / UpdateAsync / SaveTitleAsync）。
//
// 推荐任务配置（单任务单指令；子任务已随 Web 端下线，等价方案见 docs/子任务与任务循环替代方案.md 2.3，
// 在 Web 端重新保存一次任务即可自动清空旧配置残留的子任务步骤）：
//   1) 触发指令填 ^电影港搜索.*|^[0-9,，\s]+$ 并勾选正则；指令环境变量名填 dygangs_msg。
//   2) App 端发「电影港搜索 凡人」即搜索（脚本剥离指令词取关键字）；对结果点按「订阅本片」或直接
//      回复「45148」「45148,57617」即订阅（两者等价）——脚本按消息形态自动分流：纯数字/逗号/空白视为订阅回复，
//      其余视为搜索（数字片名请用「电影港搜索 2046」形式）。
//   3) Web 端手动调试：配变量 scripts_dygangs_search=关键字（搜索）或 scripts_dygangs_add=ID（订阅）。
//   4) 可选变量：scripts_dygangs_max=通知条数上限（缺省 10；站点单页约 10-12 条，只解析第一页）。
//   5) 旧两步流仍兼容：子任务回复步骤变量 dygangs_id 优先于 dygangs_msg，老配置未清理时不受影响。
//
// 迁移映射（计划 2.7）：表单 POST → ctx.Http.PostAsync（关键字按 GB2312 逐字节百分号编码）；
//   302 → /e/search/result/?searchid=NNN（HttpClient 自动跟随，已留手动跟随兜底）；
//   cheerio → HtmlAgilityPack；iconv-lite(gb2312) → CodePagesEncodingProvider + GB2312；
//   console.log → ctx.Log；sendNotify → ctx.Notify；持久化 → ctx.CustomData（搜索缓存类型 video_search_cache
//   每次搜索清空重建，键为 Data1=资源ID）。
// 推送拆条（2026-09-17）：摘要一条（命中数+回复说明），每个结果一条图片气泡（封面+配文同屏，
//   配文含 [已监听] 标记/ID/片名/年代/产地/类型/详情页），无封面退回纯文本；不再合并成一条长文本。
// 有意差异：
//   1) 搜索关键字必须按站点表单编码（GB2312）提交，UTF-8 直接提交会搜不到；
//   2) 简介字段（◎年代/◎产地/◎类别）先剔除全部空白再按「◎标签」截取，兼容全角空格排版；
//   3) 资源ID 取详情页 URL 文件名（/dmq/20200727/45148.htm → 45148），回复前先在搜索缓存里精确比对；
//   4) 订阅去重以 Data3（资源ID）精确相等为准，另按来源页比对兼容 dygangs.js 时代旧清单，命中即跳过并留日志；
//   5) 单指令分流：纯数字/逗号/空白消息视为订阅回复（原由子任务步骤承接），数字片名须带指令词搜索。
// ============================================================================
using System.Text;
using HtmlAgilityPack;
using Quantum.Plugins;

public class DygangsSearchTask : IQuantumTask
{
    private const string BaseUrl = "https://www.dygangs.net";
    private const string SearchUrl = BaseUrl + "/e/search/index.php";
    private const string CacheType = "video_search_cache";
    private const string ItemType = "video_update_monitoring_item";
    private const int DefaultMax = 10;

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        // .NET 默认只带 UTF-8/ASCII 等少数编码，站点是 gb2312，需先注册代码页提供程序（进程内幂等）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb2312 = Encoding.GetEncoding("GB2312");

        // 手动调试变量：Web 配 scripts_dygangs_add=ID 强制订阅
        var manualAdd = Var(ctx, "scripts_dygangs_add");
        if (!string.IsNullOrWhiteSpace(manualAdd))
        {
            await SubscribeAsync(ctx, manualAdd, ct);
            return;
        }

        // 旧两步流的回复步骤变量（子任务已下线，未清理的老配置仍兼容，优先于单指令消息变量）
        var legacyReply = Var(ctx, "dygangs_id");
        if (!string.IsNullOrWhiteSpace(legacyReply))
        {
            await SubscribeAsync(ctx, legacyReply, ct);
            return;
        }

        // 单指令模式：整条消息经 dygangs_msg 注入，按消息形态分流（纯数字/逗号/空白 = 订阅回复）
        var input = Var(ctx, "dygangs_msg", "dygangs_keyword", "scripts_dygangs_search");
        if (string.IsNullOrWhiteSpace(input))
        {
            ctx.Log("未发现触发变量：发「电影港搜索 关键字」搜索；回复资源ID订阅；" +
                    "手动运行请配变量 scripts_dygangs_search（关键字）或 scripts_dygangs_add（资源ID）。");
            return;
        }

        if (IsReplyIds(input))
        {
            await SubscribeAsync(ctx, input, ct);
            return;
        }

        var keyword = NormalizeKeyword(input);
        if (string.IsNullOrWhiteSpace(keyword))
        {
            ctx.Log($"输入[{input}]未解析出搜索关键字。发「电影港搜索 关键字」搜索；回复资源ID订阅；" +
                    "手动运行请配变量 scripts_dygangs_search（关键字）或 scripts_dygangs_add（资源ID）。");
            return;
        }

        await SearchAsync(ctx, gb2312, keyword, ct);
    }

    // ------------------------------------------------------------------ 搜索
    private static async Task SearchAsync(QuantumTaskContext ctx, Encoding gb2312, string keyword, CancellationToken ct)
    {
        var max = DefaultMax;
        if (ctx.Variables.TryGetValue("scripts_dygangs_max", out var maxText))
        {
            int.TryParse(maxText.Trim(), out max);
        }
        if (max <= 0)
        {
            max = DefaultMax;
        }

        // 站点表单为 gb2312 编码，关键字必须按 GB2312 逐字节百分号编码（tempid/tbname 固定，show=title,smalltext 为默认范围）
        var body = "tempid=1&tbname=article&keyboard=" + UrlEncodeGb2312(gb2312, keyword) + "&show=title%2Csmalltext";
        string html;
        try
        {
            using var content = new StringContent(body, gb2312, "application/x-www-form-urlencoded");
            using var response = await ctx.Http.PostAsync(SearchUrl, content, ct);
            html = gb2312.GetString(await response.Content.ReadAsByteArrayAsync(ct));
            // HttpClient 默认自动跟随 302；若实例关闭了自动跟随则手动取 Location 再取一次结果页
            if (!html.Contains("classlinkclass") && response.Headers.Location != null)
            {
                var location = new Uri(new Uri(BaseUrl), response.Headers.Location).ToString();
                html = gb2312.GetString(await ctx.Http.GetByteArrayAsync(location, ct));
            }
        }
        catch (Exception e)
        {
            ctx.Log($"搜索请求失败：{e.Message}");
            await NotifyAsync(ctx, $"电影港搜索失败：{keyword}", $"请求异常：{e.Message}", ct);
            return;
        }

        var found = ParseResults(html);
        if (found.Count == 0)
        {
            ctx.Log($"[{keyword}]未搜索到结果（或结果页结构变化）。");
            await NotifyAsync(ctx, $"电影港搜索：{keyword}", "未搜索到结果。", ct);
            return;
        }

        // 搜索缓存清空重建（回复步骤只认最近一次搜索的结果；Delete+Add 避开同执行内 UpdateAsync 主键冲突）
        var old = await ctx.CustomData.QueryAsync(CacheType, ct: ct);
        if (old.Count > 0)
        {
            await ctx.CustomData.DeleteAsync(old.Select(n => n.Id).ToArray(), ct);
        }
        await ctx.CustomData.SaveTitleAsync(CacheType, "电影港搜索缓存",
            ["资源ID", "片名", "详情页", "关键字", "年代", "产地", "类型", "封面"], ct);
        await ctx.CustomData.AddAsync(found.Select(n => new QuantumCustomDataValue
        {
            Type = CacheType,
            Data1 = n.Id,
            Data2 = n.Title,
            Data3 = n.Detail,
            Data4 = keyword,
            Data5 = n.Year,
            Data6 = n.Origin,
            Data7 = n.Genre,
            Data8 = n.Cover
        }).ToList(), ct);

        // 已在监听清单的条目打胶囊标，回复前即可看出
        var monitored = await ctx.CustomData.QueryAsync(ItemType, ct: ct);
        // 摘要一条 + 每个结果一条封面卡片（图+配文同屏+可点「订阅本片」选项；已监听条目打标不给选项），
        // 无封面退回带选项文本。富交互（2026-09-18）：点按选项即回复资源ID（与手打等价）。
        var shown = found.Take(max).ToList();
        var summary = $"关键字「{keyword}」命中 {found.Count} 条（展示前 {shown.Count} 条）。\r\n" +
                      "点按封面卡片上的「订阅本片」加入监听；也可回复资源ID（多个逗号分隔，如 45148,57617）。";
        var logText = summary;
        await NotifyAsync(ctx, $"电影港搜索：{keyword}", summary, ct);
        foreach (var item in shown)
        {
            ct.ThrowIfCancellationRequested();
            var isMonitored = monitored.Any(n => n.Data3 == item.Id);
            var flag = isMonitored ? QuantumText.Tag("green", "已监听") : "";
            var caption = $"{flag}{QuantumText.Color("blue", $"[{item.Id}]")}{item.Title}（{item.Year} / {item.Origin} / {item.Genre}）\r\n" +
                          $"详情：{item.Detail}";
            logText += $"\r\n\r\n[{(isMonitored ? "已监听" : "")}{item.Id}]{item.Title}（{item.Year} / {item.Origin} / {item.Genre}）\r\n详情：{item.Detail}\r\n封面：{item.Cover}";
            var options = isMonitored
                ? null
                : new[] { new QuantumOption(Key: item.Id, Label: $"订阅本片（{item.Title}）", Color: "green") };
            if (string.IsNullOrWhiteSpace(item.Cover))
            {
                if (options == null)
                {
                    await NotifyAsync(ctx, null, caption, ct);
                }
                else
                {
                    await NotifyOptionsAsync(ctx, caption, options, ct);
                }
            }
            else
            {
                await NotifyImageAsync(ctx, item.Cover, caption, options, ct);
            }
        }

        ctx.Log(logText);
        ctx.Log($"搜索完成：命中 {found.Count} 条，缓存已重建（{CacheType}）。");
    }

    // ------------------------------------------------------------------ 订阅（回复资源ID）
    private static async Task SubscribeAsync(QuantumTaskContext ctx, string reply, CancellationToken ct)
    {
        var ids = SplitIds(reply);
        if (ids.Length == 0)
        {
            ctx.Log($"回复内容[{reply}]未解析出资源ID，忽略。");
            return;
        }

        var cache = await ctx.CustomData.QueryAsync(CacheType, ct: ct);
        var monitored = await ctx.CustomData.QueryAsync(ItemType, ct: ct);
        var toAdd = new List<QuantumCustomDataValue>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            // 缓存行 Data1=资源ID，精确比对（QueryAsync 是包含匹配，避免前缀串误命中）
            var hit = cache.FirstOrDefault(n => n.Data1 == id);
            if (hit == null)
            {
                ctx.Log($"[{id}]不在最近一次搜索结果里（请先发搜索指令），已跳过。");
                continue;
            }
            // 去重按资源ID（新行 Data3），并顺带按详情页 URL 比对（新行 Data1 / 旧行 Data2 均存来源页），
            // 兼容 dygangs.js 时代的旧清单，避免同一部片因列语义不同而重复订阅
            if (monitored.Any(n => n.Data3 == id || n.Data1 == hit.Data3 || n.Data2 == hit.Data3))
            {
                ctx.Log($"[{hit.Data2}]({id})已在监听清单，跳过。");
                continue;
            }
            toAdd.Add(new QuantumCustomDataValue
            {
                Type = ItemType,
                Data1 = hit.Data3,  // 来源页（与消费脚本 dygangs.cs 的 Data1 语义一致）
                Data2 = hit.Data2,  // 片名
                Data3 = hit.Data1,  // 资源ID
                Data4 = hit.Data5,  // 年代
                Data5 = hit.Data7,  // 类型
                Data6 = hit.Data6,  // 产地
                Data7 = hit.Data8   // 封面
            });
        }

        if (toAdd.Count == 0)
        {
            ctx.Log("没有可新增的监听条目。");
            return;
        }

        await ctx.CustomData.SaveTitleAsync(ItemType, "电影港监听清单",
            ["来源页", "片名", "资源ID", "年代", "类型", "产地", "封面"], ct);
        await ctx.CustomData.AddAsync(toAdd, ct);

        var summary = $"新增监听 {toAdd.Count} 条（清单共 {monitored.Count + toAdd.Count} 条）：\r\n" +
                      string.Join("\r\n", toAdd.Select(n => $"[{n.Data3}]{n.Data2}（{n.Data4} / {n.Data6} / {n.Data5}）")) +
                      "\r\ndygangs 监听任务下次运行即开始盯更新。";
        ctx.Log(summary);
        await NotifyAsync(ctx, "电影港订阅成功", summary, ct);
    }

    // ------------------------------------------------------------------ 解析与工具
    private sealed record SearchItem(string Id, string Title, string Detail, string Year, string Origin, string Genre, string Cover);

    /// <summary>解析搜索结果页：条目标题锚为 a.classlinkclass，封面在其行内 img，简介在下一行的 td valign=top。</summary>
    private static List<SearchItem> ParseResults(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html ?? string.Empty);
        var anchors = doc.DocumentNode.SelectNodes("//a[contains(@class,'classlinkclass')]");
        var result = new List<SearchItem>();
        if (anchors == null)
        {
            return result;
        }
        var seen = new HashSet<string>();
        foreach (var anchor in anchors)
        {
            var href = anchor.GetAttributeValue("href", null)?.Trim();
            var id = IdFrom(href);
            if (id == null || !seen.Add(id))
            {
                continue;
            }
            var title = anchor.InnerText?.Trim();
            var cover = anchor.SelectNodes("./ancestor::tr[1]//img")?.FirstOrDefault()?.GetAttributeValue("src", null)?.Trim();
            var intro = anchor.SelectNodes("./ancestor::tr[1]/following-sibling::tr[1]/td[@valign='top']")?.FirstOrDefault()?.InnerText;
            var flat = Flatten(intro);
            result.Add(new SearchItem(
                id,
                title,
                AbsoluteUrl(href),
                FieldAfter(flat, "年代"),
                FieldAfter(flat, "产地"),
                FieldAfter(flat, "类别"),
                cover == null ? null : AbsoluteUrl(cover)));
        }
        return result;
    }

    /// <summary>详情页 URL 文件名即资源ID（…/20200727/45148.htm → 45148），非纯数字的链接跳过。</summary>
    private static string IdFrom(string href)
    {
        if (string.IsNullOrEmpty(href))
        {
            return null;
        }
        var seg = href.Substring(href.LastIndexOf('/') + 1);
        if (seg.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            seg = seg[..^4];
        }
        return seg.Length > 0 && seg.All(char.IsDigit) ? seg : null;
    }

    private static string AbsoluteUrl(string href)
    {
        if (string.IsNullOrEmpty(href))
        {
            return href;
        }
        return href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : BaseUrl + (href.StartsWith("/") ? href : "/" + href);
    }

    /// <summary>剔除全部空白（含全角空格），便于按「◎标签」稳定截取字段。</summary>
    private static string Flatten(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>取「◎标签」后直到下一个「◎」的值；标签先经 Flatten 去空白（◎年　代 → ◎年代）。</summary>
    private static string FieldAfter(string flat, string label)
    {
        var start = flat.IndexOf("◎" + label, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }
        start += label.Length + 1;
        var end = flat.IndexOf('◎', start);
        return end < 0 ? flat[start..] : flat[start..end];
    }

    /// <summary>按逗号/空白拆分资源ID（兼容半角/全角逗号与制表空白）。</summary>
    private static string[] SplitIds(string reply) => reply.Split([',', '，', ' ', '　', '\n', '\r', '\t'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>纯数字/逗号/空白（如 45148 或 45148,57617）判定为订阅回复；数字片名请用「电影港搜索 2046」形式。</summary>
    private static bool IsReplyIds(string text)
    {
        var ids = SplitIds(text);
        return ids.Length > 0 && ids.All(id => id.All(char.IsDigit));
    }

    /// <summary>指令整条消息触发时剥离命令词：「电影港搜索 凡人」→「凡人」（兼容半角/全角/制表空白）。</summary>
    private static string NormalizeKeyword(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var keyword = raw.Trim();
        foreach (var sep in new[] { ' ', '　', '\t' })
        {
            var idx = keyword.IndexOf(sep);
            if (idx > 0)
            {
                keyword = keyword[(idx + 1)..].Trim();
                break;
            }
        }
        return keyword.Length == 0 ? null : keyword;
    }

    private static string UrlEncodeGb2312(Encoding gb2312, string text)
    {
        var sb = new StringBuilder();
        foreach (var b in gb2312.GetBytes(text))
        {
            sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>多变量取值：命中第一个非空变量（回复步骤优先于手动变量）。</summary>
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

    /// <summary>推送开关关闭时只落日志（搜索结果/订阅回执都以文字承载）。</summary>
    private static async Task NotifyAsync(QuantumTaskContext ctx, string title, string content, CancellationToken ct)
    {
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendAsync(title, content, ct);
        }
    }

    /// <summary>图片气泡推送（封面+配文同屏展示，可带可点选项）；推送开关关闭时只落日志。</summary>
    private static async Task NotifyImageAsync(QuantumTaskContext ctx, string imageUrl, string caption,
        IReadOnlyList<QuantumOption> options = null, CancellationToken ct = default)
    {
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendImageAsync(imageUrl, caption, options, ct);
        }
    }

    /// <summary>带可点选项的文本推送；推送开关关闭时只落日志。</summary>
    private static async Task NotifyOptionsAsync(QuantumTaskContext ctx, string content,
        IReadOnlyList<QuantumOption> options, CancellationToken ct)
    {
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendOptionsAsync(content, options, ct);
        }
    }
}
