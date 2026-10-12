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
// 选项卡片时效修复（2026-10-10）：App 同会话只有最新一条带选项的消息保持可点——逐张封面卡各挂一条
//   「订阅本片」选项时，前面的卡片点按会报「已过期」，只有最后一张能订阅。改为：封面卡一律不挂选项，
//   全部未监听结果（含无封面条目）汇总进最后一条 SendOptionsAsync「订阅点选卡」（Label=「序号. 片名」，
//   Desc=年代/产地/类型+ID），点按即回复资源ID（与手打等价）；已监听条目只在封面配文打标、不进点选卡。
// 订阅即首采（2026-10-05 修复）：此前订阅成功只把条目写入监听清单就结束，采集/下载要等 dygangs 监听任务
//   的下一个定时周期；而监听任务对「首次抓取」的页面只把现有磁力入库、不下载不通知（旧集是基线，
//   只有之后新发布的集数才会自动下载）——订阅后长时间毫无动静，这就是「订阅了却没自动监听下载」的根因。
//   现在订阅成功的同一次运行内，立即对新增来源页跑一遍监听采集（与 dygangs.cs 同款解析/去重/入库；
//   脚本单文件编译不能跨文件引用，采集与 qB 提交逻辑在 MonitorSnapshot 内嵌类内联一份）：
//   2160P/4K 直接提交 qB 下载，标清条目挂点选卡片（点按即回磁力消息，由「添加qb磁力任务」接续提交）。
//   首采入库的磁力即成为监听任务的「已采集」基线，之后只追新集，两不冲突、不会重复下载。
// 302 跟随基准修复（2026-10-11）：站点搜索 302 的 Location 已改为相对路径 result/?searchid=NNN，
//   仍按站点根 BaseUrl 解析会得到 /result/?searchid=... → 404「搜索请求失败」；改为以 SearchUrl
//   （/e/search/index.php 所在目录）为基准解析，得到正确的 /e/search/result/?searchid=NNN。
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
            // HttpClient 默认自动跟随 302；若实例关闭了自动跟随则手动取 Location 再取一次结果页。
            // Location 为相对路径 result/?searchid=NNN（2026-10-11 站点改版起），须按 SearchUrl（/e/search/ 目录）
            // 解析；按 BaseUrl（站点根）拼会得到 /result/?searchid=... → 404。
            if (!html.Contains("classlinkclass") && response.Headers.Location != null)
            {
                var location = new Uri(new Uri(SearchUrl), response.Headers.Location).ToString();
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
        // 摘要一条 + 每个结果一条封面卡片（图+配文同屏，已监听条目打标）。
        // 选项卡片时效修复（2026-10-10）：App 同会话只有最新一条带选项的消息保持可点，逐张封面卡
        // 各挂「订阅本片」会让前面的卡点按报「已过期」——封面卡一律不挂选项，未监听结果汇总进
        // 最后一条「订阅点选卡」（点按即回复资源ID，与手打完全等价），那条卡始终是最新、可点不过期。
        var shown = found.Take(max).ToList();
        var summary = $"关键字「{keyword}」命中 {found.Count} 条（展示前 {shown.Count} 条）。\r\n" +
                      "点按最后一张「订阅点选卡」上的片名加入监听；也可回复资源ID（多个逗号分隔，如 45148,57617）。";
        var logText = summary;
        await NotifyAsync(ctx, $"电影港搜索：{keyword}", summary, ct);
        var pickOptions = new List<QuantumOption>();
        var seq = 0;
        foreach (var item in shown)
        {
            ct.ThrowIfCancellationRequested();
            seq++;
            var isMonitored = monitored.Any(n => n.Data3 == item.Id);
            var flag = isMonitored ? QuantumText.Tag("green", "已监听") : "";
            var caption = $"{flag}{QuantumText.Color("blue", $"[{item.Id}]")}{item.Title}（{item.Year} / {item.Origin} / {item.Genre}）\r\n" +
                          $"详情：{item.Detail}";
            logText += $"\r\n\r\n[{(isMonitored ? "已监听" : "")}{item.Id}]{item.Title}（{item.Year} / {item.Origin} / {item.Genre}）\r\n详情：{item.Detail}\r\n封面：{item.Cover}";
            if (!isMonitored)
            {
                pickOptions.Add(new QuantumOption(
                    Key: item.Id,
                    Label: $"{seq}. {Ellipsis(item.Title, 80)}",
                    Desc: $"{item.Year} / {item.Origin} / {item.Genre}（ID {item.Id}）",
                    Color: "green"));
            }
            if (string.IsNullOrWhiteSpace(item.Cover))
            {
                await NotifyAsync(ctx, null, caption, ct);
            }
            else
            {
                await NotifyImageAsync(ctx, item.Cover, caption, null, ct);
            }
        }

        // 汇总订阅点选卡：未监听结果一个气泡内点选（点按即回复资源ID，与手打完全等价）
        if (pickOptions.Count > 0)
        {
            await NotifyOptionsAsync(ctx, $"订阅点选卡（{pickOptions.Count} 项，已监听的不列）：点按片名即订阅本片。",
                pickOptions, ct);
        }

        ctx.Log(logText);
        ctx.Log($"搜索完成：命中 {found.Count} 条，缓存已重建（{CacheType}）；推送封面卡 {shown.Count} 张" +
                (pickOptions.Count > 0 ? " + 订阅点选卡 1 条。" : "（全部已监听，未附点选卡）。"));
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

        // 订阅即首采（2026-10-05）：订阅成功的同一次运行内立即抓取新增来源页，全集磁力入库并给出下载渠道，
        // 不再等 dygangs 监听任务的下一个定时周期（且监听任务对首抓页面只入库不下载，见文件头说明）。
        var snapshot = new MonitorSnapshot(ctx, Encoding.GetEncoding("GB2312"), ct);
        foreach (var n in toAdd)
        {
            ct.ThrowIfCancellationRequested();
            await snapshot.CollectAsync(n.Data1, string.IsNullOrEmpty(n.Data2?.Trim()) ? n.Data1 : n.Data2.Trim());
        }
        var firstNote = snapshot.TotalNew > 0
            ? $"已立即首采 {snapshot.TotalNew} 条全集磁力：2160P/4K 已提交 qB 自动下载，标清见随后的点选卡片。"
            : "详情页暂无磁力可首采，dygangs 监听任务会持续盯新集。";

        var summary = $"新增监听 {toAdd.Count} 条（清单共 {monitored.Count + toAdd.Count} 条）：\r\n" +
                      string.Join("\r\n", toAdd.Select(n => $"[{n.Data3}]{n.Data2}（{n.Data4} / {n.Data6} / {n.Data5}）")) +
                      $"\r\n{firstNote}\r\ndygangs 监听任务持续自动盯新集。";
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

    /// <summary>超长片名截断（订阅点选卡 Label 限 80 字符；与 MonitorSnapshot 内同款，外层独立一份）。</summary>
    private static string Ellipsis(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "…";

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

    // ------------------------------------------------------------------ 订阅首采（内嵌采集器，2026-10-05）
    // 与监听任务 dygangs.cs 同款的页面解析 / 磁力去重 / video_update_monitoring 入库与 qB 提交逻辑——
    // 脚本单文件编译不能跨文件引用，在此内联一份保持同源。差异（有意为之）：
    // 监听任务只对「新出现」的磁力按 2160P/4K 自动下载（订阅前的存量是基线，不算更新）；
    // 订阅首采把订阅时刻页面上的全部磁力都当成要的：入库后 2160P/4K 立即提交 qB，
    // 标清挂点选卡片（点按即回磁力消息，由「添加qb磁力任务」接续提交）。
    private sealed class MonitorSnapshot
    {
        private const string CustomDataType = "video_update_monitoring";
        private const string QbSidEnv = "qbSID";

        private readonly QuantumTaskContext ctx;
        private readonly Encoding gb2312;
        private readonly CancellationToken ct;

        /// <summary>本次订阅首采新入库的磁力总条数（多个来源页累计）。</summary>
        public int TotalNew { get; private set; }

        public MonitorSnapshot(QuantumTaskContext ctx, Encoding gb2312, CancellationToken ct)
        {
            this.ctx = ctx;
            this.gb2312 = gb2312;
            this.ct = ct;
        }

        /// <summary>抓取一个来源页：新磁力写入 video_update_monitoring 并推送下载回执（抓取失败不抛出，留待监听任务兜底）。</summary>
        public async Task CollectAsync(string pageUrl, string pageName)
        {
            // 去重集合与监听任务 dygangs.cs 同款：新行 Data1=来源页 / Data3=磁力；旧「影视监听记录」Data2=来源页 / Data4=磁力
            var datas = (await ctx.CustomData.QueryAsync(CustomDataType, new QuantumCustomDataFilter { Data1 = pageUrl }, ct))
                .Concat(await ctx.CustomData.QueryAsync(CustomDataType, new QuantumCustomDataFilter { Data2 = pageUrl }, ct))
                .ToList();

            byte[] bytes;
            try
            {
                bytes = await ctx.Http.GetByteArrayAsync(pageUrl, ct);
            }
            catch (Exception e)
            {
                ctx.Log($"[{pageName}]订阅首采页面抓取失败，跳过（dygangs 监听任务仍会持续盯）：{Kind(e)} {e.Message}");
                return;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(gb2312.GetString(bytes));
            var anchors = doc.DocumentNode.SelectNodes("//a[starts-with(@href,'magnet:?xt=urn:btih:')]");
            if (anchors == null || anchors.Count == 0)
            {
                ctx.Log($"[{pageName}]订阅首采未找到磁力链接。");
                return;
            }

            var newData = new List<QuantumCustomDataValue>();
            foreach (var anchor in anchors)
            {
                ct.ThrowIfCancellationRequested();
                var link = DecodeHtml(anchor.GetAttributeValue("href", null));
                if (string.IsNullOrEmpty(link))
                {
                    continue;
                }
                var name = anchor.InnerText?.Trim();
                if (datas.Any(n => n.Data3 == link || n.Data4 == link))
                {
                    continue; // 已采集（重复订阅同一部片等情形）
                }
                newData.Add(new QuantumCustomDataValue
                {
                    Type = CustomDataType,
                    Data1 = pageUrl,
                    Data2 = name,
                    Data3 = link
                });
            }

            if (newData.Count == 0)
            {
                ctx.Log($"[{pageName}]订阅首采：本页磁力 {anchors.Count} 条均已采集过，无新条目。");
                return;
            }
            await ctx.CustomData.AddAsync(newData, ct);
            TotalNew += newData.Count;
            ctx.Log($"[{pageName}]订阅首采：本页磁力 {anchors.Count} 条，新入库 {newData.Count} 条。");

            // 提交 qB：2160P/4K 自动下载；标清收集为点选卡片
            var qbUrl = ctx.Variables.TryGetValue("qBittorrentURL", out var qb) ? qb : null;
            var submitted = 0;
            var failed = 0;
            var lowRes = newData.Where(n => !IsHighRes(n.Data2)).ToList();
            if (string.IsNullOrEmpty(qbUrl))
            {
                ctx.Log("未设置qBittorrent服务地址，请添加量子变量：\r\n" +
                        "qBittorrentURL qb链接地址 如：http://192.168.x.x:8080\r\n" +
                        "qbusername qb登录账号（可选）\r\nqbpassword qb登录密码（可选）");
            }
            else
            {
                var cookie = await GetCookieAsync(ctx, qbUrl, ct);
                foreach (var item in newData)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!IsHighRes(item.Data2))
                    {
                        continue;
                    }
                    if (await AddTorrentAsync(ctx, qbUrl, cookie, item.Data3, pageName, ct))
                    {
                        submitted++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            // 回执：正文带 2160P/4K 已提交清单；标清挂点选卡片（点按即回磁力消息，
            // 由「添加qb磁力任务」任务（触发词 magnet:，正则）接续提交 qB，成败看 qB 回执）
            var highResLines = string.Join("\r\n", newData.Where(n => IsHighRes(n.Data2)).Select(n => n.Data2));
            var tail = string.IsNullOrEmpty(qbUrl)
                ? $"{QuantumText.Tag("orange", "未配置")} qBittorrentURL，未自动下载。"
                : $"已提交 2160P/4K 自动下载 {QuantumText.Color("green", submitted.ToString())} 条" +
                  (failed > 0 ? $"（{QuantumText.Color("red", failed.ToString())} 条失败，见任务日志）" : string.Empty);
            var content = $"【{pageName}】订阅首采：本页磁力 {newData.Count} 条已入库。\r\n{tail}" +
                          (highResLines.Length > 0 ? "\r\n\r\n2160P/4K 已提交：\r\n" + highResLines : string.Empty);

            if (lowRes.Count > 0)
            {
                const int MaxOptions = 20;
                var options = lowRes.Take(MaxOptions).Select((n, i) => new QuantumOption(
                    Key: n.Data3,
                    Label: $"{i + 1}. {Ellipsis(n.Data2, 80)}",
                    Desc: "标清资源，点选提交 qB 下载",
                    Color: "blue")).ToList();
                if (lowRes.Count > MaxOptions)
                {
                    ctx.Log($"标清条目 {lowRes.Count} 条，超出点选卡片上限 {MaxOptions}，仅附前 {MaxOptions} 条" +
                            "（全部磁力已入库 video_update_monitoring，可到数据管理页取）。");
                }
                await NotifyOptionsAsync(ctx, content + "\r\n\r\n以下标清资源可点选提交下载：", options, ct);
            }
            else
            {
                await NotifyAsync(ctx, "电影港订阅首采", content, ct);
            }
        }

        // ------------------------------------------------ qB 会话与提交（与 dygangs.cs 同源内联）

        /// <summary>
        /// 取 qB 会话 Cookie：优先复用环境变量 qbSID 缓存——无缓存或超过 12 小时直接重登；
        /// 12 小时内先调 app/version 校验，失效再重登；重登成功回写 qbSID。
        /// 未配账号密码时维持旧行为：返回空 cookie，依赖 qB 的 IP 白名单放行。
        /// </summary>
        private static async Task<string> GetCookieAsync(QuantumTaskContext ctx, string baseUrl, CancellationToken ct)
        {
            baseUrl = baseUrl.TrimEnd('/');
            var user = ctx.Variables.TryGetValue("qbusername", out var u) ? u : null;
            var password = ctx.Variables.TryGetValue("qbpassword", out var p) ? p : null;
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
            {
                ctx.Log("未配置qb 账号密码，跳过登录，可能出现请求拒绝访问（403）");
                return string.Empty;
            }

            var cached = await FindCachedSidAsync(ctx, ct);
            if (cached != null && !string.IsNullOrEmpty(cached.Value) && cached.UpdateTime != default)
            {
                var age = DateTime.Now - cached.UpdateTime;
                if (age <= TimeSpan.FromHours(12))
                {
                    if (await VerifySidAsync(ctx, baseUrl, cached.Value, ct))
                    {
                        ctx.Log($"qbSID 缓存有效（距上次登录 {age.TotalHours:F1} 小时），免登录复用");
                        return "SID=" + cached.Value;
                    }
                    ctx.Log("qbSID 缓存校验未通过（会话已失效），重新登录");
                }
                else
                {
                    ctx.Log($"qbSID 缓存已超过 12 小时（{age.TotalHours:F1} 小时），重新登录");
                }
            }
            return await LoginAsync(ctx, baseUrl, user, password, ct);
        }

        /// <summary>读 qbSID 环境变量缓存；异常不阻断流程（按无缓存处理）。</summary>
        private static async Task<QuantumEnvValue> FindCachedSidAsync(QuantumTaskContext ctx, CancellationToken ct)
        {
            try
            {
                return (await ctx.Env.QueryAsync(QbSidEnv, null, ct)).FirstOrDefault();
            }
            catch (Exception e)
            {
                ctx.Log($"读取 qbSID 缓存异常（按无缓存处理）：{Kind(e)} {e.Message}");
                return null;
            }
        }

        /// <summary>校验 SID 会话是否有效：GET /api/v2/app/version 带 SID，200 有效、403 失效。</summary>
        private static async Task<bool> VerifySidAsync(QuantumTaskContext ctx, string baseUrl, string sid, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v2/app/version");
                request.Headers.TryAddWithoutValidation("Cookie", "SID=" + sid);
                using var response = await ctx.Http.SendAsync(request, ct);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                ctx.Log($"qbSID 校验请求异常（按失效处理）：{Kind(e)} {e.Message}");
                return false;
            }
        }

        /// <summary>登录 qB 取新 SID 并回写 qbSID 环境变量；登录失败或未取到 SID 返回空 cookie。</summary>
        private static async Task<string> LoginAsync(QuantumTaskContext ctx, string baseUrl, string user, string password, CancellationToken ct)
        {
            try
            {
                using var form = new MultipartFormDataContent
                {
                    { new StringContent(user), "username" },
                    { new StringContent(password), "password" }
                };
                using var response = await ctx.Http.PostAsync($"{baseUrl}/api/v2/auth/login", form, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                var sid = ExtractSidValue(response);
                ctx.Log($"qb登录：HTTP {(int)response.StatusCode} {body.Trim()}，SID {(string.IsNullOrEmpty(sid) ? "未取到" : "已取到")}");
                if (string.IsNullOrEmpty(sid))
                {
                    ctx.Log(!response.IsSuccessStatusCode
                        ? "qb登录未成功（多为账号密码错误）"
                        : "qb登录成功但响应未含 SID（qB 可能配置了免认证放行），本次不写缓存");
                    return string.Empty;
                }
                try
                {
                    await ctx.Env.SaveAsync(QbSidEnv, sid, "qB 会话缓存（任务自动维护，勿手改）", true, ct);
                    ctx.Log($"qbSID 已写入环境变量（{QbSidEnv}），12 小时内免登录复用");
                }
                catch (Exception e)
                {
                    ctx.Log($"qbSID 写入环境变量异常（不影响本次执行）：{Kind(e)} {e.Message}");
                }
                return "SID=" + sid;
            }
            catch (Exception e)
            {
                ctx.Log("qb登录认证出现异常");
                ctx.Log(e.ToString());
                return string.Empty;
            }
        }

        /// <summary>
        /// 添加单条磁力任务（保存子目录 /downloads/{savePath}）。
        /// 成败按 HTTP 状态码 + 响应体判定（qB 对无法解析的种子返回 200 + "Fails."；
        /// qB 5.x 对种子已在下载列表返回 409，按已处理计成功），结果只落日志由调用方计数，不在此发通知避免刷屏。
        /// </summary>
        private static async Task<bool> AddTorrentAsync(QuantumTaskContext ctx, string baseUrl, string cookie,
            string url, string savePath, CancellationToken ct)
        {
            baseUrl = baseUrl.TrimEnd('/');
            ctx.Log($"磁力信息：{url}");
            try
            {
                using var form = new MultipartFormDataContent
                {
                    { new StringContent(url ?? string.Empty), "urls" },
                    { new StringContent("false"), "autoTMM" },
                    { new StringContent("false"), "paused" },
                    { new StringContent("Original"), "contentLayout" }
                };
                if (!string.IsNullOrEmpty(savePath))
                {
                    form.Add(new StringContent("/downloads/" + savePath), "savepath");
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v2/torrents/add")
                {
                    Content = form
                };
                if (!string.IsNullOrEmpty(cookie))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);
                }
                using var response = await ctx.Http.SendAsync(request, ct);
                var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
                ctx.Log($"添加磁力响应：HTTP {(int)response.StatusCode} {body}");
                // qB 5.x 重复添加返回 409，种子已在下载列表中，按已处理计成功
                var dup = response.StatusCode == System.Net.HttpStatusCode.Conflict;
                var ok = dup || (response.IsSuccessStatusCode && !body.StartsWith("Fails", StringComparison.OrdinalIgnoreCase));
                if (dup)
                {
                    ctx.Log("磁力下载提交结果----已在 qB 中（HTTP 409），跳过");
                }
                else
                {
                    ctx.Log(ok ? "磁力下载提交结果----成功" : $"磁力下载提交结果----失败"
                        + (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                            ? "（403 多为未登录且量子 IP 不在 qB 白名单，请配置 qbusername/qbpassword 或在 qB 中放行）"
                            : string.Empty));
                }
                return ok;
            }
            catch (Exception e)
            {
                ctx.Log($"磁力下载提交结果----异常：{Kind(e)} {e.Message}");
                return false;
            }
        }

        // ------------------------------------------------ 抓取工具（与 dygangs.cs 同源内联）

        /// <summary>
        /// 还原 HTML 属性值实体（&amp;→&、&lt;→&lt;、&gt;→&gt;、&quot;→"、&#x26;/&#38;→&、&nbsp;→空格）。
        /// 电影港磁力 href 中 & 被转义成 &amp;，原样入库/提交 qB 会让 dn/xl/tr 参数全部失效（只剩 btih、
        /// 没有 tracker，种子停在「正在下载元数据」）；GetAttributeValue 按原始属性值返回、不做解码，须在此还原。
        /// </summary>
        private static string DecodeHtml(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('&') < 0)
            {
                return value;
            }
            return value
                .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
                .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
                .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
                .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
                .Replace("&#x26;", "&", StringComparison.OrdinalIgnoreCase)
                .Replace("&#38;", "&", StringComparison.OrdinalIgnoreCase)
                .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("&#160;", " ", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>标题含 2160P 或 4K（忽略大小写）才自动提交下载；与监听任务 dygangs.cs 的筛选口径一致。</summary>
        private static bool IsHighRes(string title)
            => !string.IsNullOrEmpty(title) && (title.Contains("2160P", StringComparison.OrdinalIgnoreCase)
                || title.Contains("4K", StringComparison.OrdinalIgnoreCase));

        /// <summary>超长标题截断（种子名常带大量规格后缀，点选卡片 Label 限 80 字符；参照 btsow 搜索脚本）。</summary>
        private static string Ellipsis(string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "…";

        /// <summary>异常类别名：等价旧脚本 error.name，且不触碰被门禁拦截的反射成员（e.GetType().Name）。</summary>
        private static string Kind(Exception e) => e switch
        {
            HttpRequestException => nameof(HttpRequestException),
            TaskCanceledException => nameof(TaskCanceledException),
            OperationCanceledException => nameof(OperationCanceledException),
            _ => nameof(Exception)
        };

        /// <summary>从 Set-Cookie 响应头提取 SID 值（不含 SID= 前缀；不会误匹配 SID_30s）。</summary>
        private static string ExtractSidValue(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                return string.Empty;
            }
            foreach (var header in values)
            {
                var match = System.Text.RegularExpressions.Regex.Match(header, @"SID=([^; ]+)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            return string.Empty;
        }
    }
}
