// ============================================================================
// btsow 磁力搜索（由 btsow.js 转换，2026-09-17）：按指令搜索 btsow，结果以可点选卡片推送——
//   点按即回复该条磁力链接（与手打完全等价），由「添加qb磁力任务」任务（触发词 magnet:|磁力）
//   接管提交 qBittorrent 下载，添加成败看 qB 任务回执。
// 迁移映射（计划 2.7）：api → ctx.Http；JSON.parse → Newtonsoft.Json；sendNotify → ctx.Notify（标题自定）；
//   process.env.command → ctx.Variables["command"]；console.log → ctx.Log；
//   Date 取本地年月日 → DateTimeOffset.FromUnixTimeSeconds(...).ToLocalTime()。
// 环境变量：command（如「搜磁力 西游记」，默认「西游记」）、
//   scripts_btsow_url（必填，搜索接口完整地址；2026-09-19 脱敏改造：脚本不再内置地址，
//   域名轮换时直接改环境变量即可，不必重传脚本）。
// 与旧脚本的差异（有意为之）：
//   1) 接口主机历经域名轮换（旧域名现 302 到新主机根路径、返回 400，已实测），
//      而接口路径与返回结构（hash/name/size/lastUpdateTime）未变，故只换主机；该类域名常轮换，
//      再变时改 scripts_btsow_url 环境变量即可，不必重传脚本。
//   2) 标题清洗由「<em>、</em> 各去一次」改为去掉全部（同一标题可能有多组高亮标签，旧写法会残留标签）。
//   3) 请求体与 Content-Type 明确按 UTF-8 + application/json 发送（中文关键词必须 UTF-8，否则服务端判缺参）。
//   4) 无结果时只记日志——旧脚本在此静默，排查困难。
//   5) 请求显式带 Accept/User-Agent：该站对两者都没有的请求直接回 403（已实测），而 ctx.Http 默认不带，
//      旧脚本能通是因为 got 自带了 User-Agent。
//   6) 2026-09-18 富交互改造：逐条文本磁力推送改为单条 SendOptionsAsync 点选卡片（参照 music_search），
//      磁力链接从消息正文挪进选项回复文本；点选提交下载，旧版 App 看不到选项块只能看到标题行。
//      需含 2026-09-18 的 ctx.Notify.SendOptionsAsync 契约（富交互批次），旧后端会拒绝保存。
// ============================================================================
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class BtsowSearchTask : IQuantumTask
{
    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var command = ctx.Variables.TryGetValue("command", out var value) ? value : null;
        if (string.IsNullOrEmpty(command))
        {
            command = "西游记";
        }
        ctx.Log($"执行任务：【{command}】");
        var key = command.Replace("搜磁力", "").Trim();

        var url = ctx.Variables.TryGetValue("scripts_btsow_url", out var configured) && !string.IsNullOrEmpty(configured)
            ? configured
            : throw new Exception("缺少环境变量 scripts_btsow_url（搜索接口地址），请先在环境变量页配置");
        ctx.Log($"url {url}");

        // 接口约定：请求体为 [{"search":关键词}, 每页条数, 页码]
        var payload = new JArray { new JObject { ["search"] = key }, 10, 1 }.ToString(Formatting.None);

        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "QuantumTask/1.0");
            using var response = await ctx.Http.SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                ctx.Log($"搜索请求失败：HTTP {(int)response.StatusCode} {body}");
                await ctx.Notify.SendAsync("磁力搜索", $"搜索【{key}】失败：HTTP {(int)response.StatusCode}", ct);
                return;
            }
        }
        catch (Exception e)
        {
            ctx.Log($"搜索请求异常：{e.Message}");
            await ctx.Notify.SendAsync("磁力搜索", $"搜索【{key}】异常：{e.Message}", ct);
            return;
        }

        var result = JObject.Parse(body);
        var list = result["data"] as JArray;
        if (list == null || list.Count == 0)
        {
            ctx.Log($"未检索到【{key}】相关信息（code={result["code"]}，message={result["message"]}）。");
            return;
        }

        // 富交互：结果收敛为单条点选卡片，点按即回复磁力链接（等价手打，由「添加qb磁力任务」接续提交下载）
        var options = new List<QuantumOption>();
        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            if (options.Count >= 20)
            {
                ctx.Log($"结果超出选项上限 20 条，仅推送前 {options.Count} 条（接口单页固定 10 条，正常不会触发）。");
                break;
            }
            var magnetLink = $"magnet:?xt=urn:btih:{item["hash"]}";
            var title = CleanName(item["name"]?.ToString());
            var desc = $"{SizeText(Bytes(item["size"]))} · {FormatTimestamp(item["lastUpdateTime"])}";
            ctx.Log($"【{title}】 {desc} {magnetLink}");
            options.Add(new QuantumOption(
                Key: magnetLink,
                Label: $"{options.Count + 1}. {Ellipsis(title, 80)}",
                Desc: desc,
                Color: "blue"));
        }
        var header = $"🧲 磁力搜索「{key}」命中 {list.Count} 条";
        ctx.Log(header);
        ctx.Log($"共推送 {options.Count} 条点选结果。");
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendOptionsAsync(
                header + "\r\n点选磁力即提交 qBittorrent 下载，添加结果见 qB 任务回执。",
                options, ct);
        }
    }

    /// <summary>超长标题截断（种子名常带大量规格后缀，卡片 Label 限 80 字符）。</summary>
    private static string Ellipsis(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "…";

    /// <summary>字节数 → 可读大小：≥1GB 用 GB，其余 MB，解析失败记「大小未知」。</summary>
    private static string SizeText(double bytes)
    {
        if (bytes <= 0)
        {
            return "大小未知";
        }
        var mb = bytes / 1024d / 1024d;
        return mb >= 1024
            ? $"{(mb / 1024d).ToString("F2", CultureInfo.InvariantCulture)} GB"
            : $"{mb.ToString("F2", CultureInfo.InvariantCulture)} MB";
    }

    /// <summary>去高亮标签（btsow 会把命中的关键词包成 &lt;em&gt;），并 Trim。</summary>
    private static string CleanName(string name)
        => name?.Replace("<em>", string.Empty).Replace("</em>", string.Empty).Trim();

    /// <summary>size 字段为字节数（数字或字符串），解析失败记 0。</summary>
    private static double Bytes(JToken token)
    {
        if (token == null || token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return 0;
        }
        return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    /// <summary>Unix 秒 → 本地日期 yyyy-MM-dd（对应旧脚本 new Date(ts*1000) 取年月日）。</summary>
    private static string FormatTimestamp(JToken token)
    {
        if (token == null || !long.TryParse(token.ToString(), out var seconds) || seconds <= 0)
        {
            return token?.ToString();
        }
        return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd");
    }
}
