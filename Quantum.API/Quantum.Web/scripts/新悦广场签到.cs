// ============================================================================
// 碧乐+签到（由 新悦广场签到.js 转换，2026-09-17）：对固定账号列表逐个调用商场签到接口并通知结果。
// 迁移映射（计划 2.7）：api → ctx.Http；sendNotify → ctx.Notify；console.log → ctx.Log；
//   JSON.stringify → Newtonsoft.Json。
// 环境变量（2026-09-19 脱敏改造：账号令牌与签名全部改从环境变量读取，脚本不再内置）：
//   XinYueAccount（必填，可配多条同名变量；单条格式：手机号|令牌，令牌含「Bearer 」前缀）；
//   XinYueNonce / XinYueSignature（必填，X-ZHIMA-* 签名头）。
// 推送（2026-09-20 改）：全部账号跑完后合并为一条消息推送（逐账号一行结果），不再每账号各推一条刷屏。
// 与旧脚本的差异（有意为之）：
//   1) 旧脚本的 sign()（新悦广场）与 sign2()（碧乐城）两段已注释停用，转换时只保留实际启用的 sign3()；
//      如要恢复，按其请求体/令牌新增一个方法即可（结构与本文件 SignAsync 相同）。
//   2) Host / Connection 两个头由 HttpClient 依 URI 自行管理，无处覆盖（原脚本手工指定，无实际作用）。
//   3) X-ZHIMA-NONCE / X-ZHIMA-SIGNATURE 会随服务端签名规则或时间失效，失败时改这两个环境变量即可，不必重传脚本。
// ============================================================================
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class XinYuePlazaSignTask : IQuantumTask
{
    private const string SignUrl = "https://a.zhimatech.com/restful/mall/3836/checkInRecord";

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        // 多账号：同名环境变量 XinYueAccount 配多条，平台按「&」合并投递；单条格式：手机号|Bearer令牌
        var rawAccounts = Var(ctx, "XinYueAccount")
            ?? throw new Exception("缺少环境变量 XinYueAccount（格式：手机号|Bearer令牌，可配多条同名变量），请先在环境变量页配置");
        var nonce = Var(ctx, "XinYueNonce") ?? throw new Exception("缺少环境变量 XinYueNonce，请先在环境变量页配置");
        var signature = Var(ctx, "XinYueSignature") ?? throw new Exception("缺少环境变量 XinYueSignature，请先在环境变量页配置");

        // 逐账号签到收集结果行，最后合并一条推送（多账号不再各推一条）
        var lines = new List<string>();
        foreach (var item in rawAccounts.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var parts = item.Split('|', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                ctx.Log($"XinYueAccount 条目格式应为「手机号|Bearer令牌」，已跳过：{item}");
                continue;
            }
            lines.Add(await SignAsync(ctx, parts[0].Trim(), parts[1].Trim(), nonce, signature, ct));
        }
        if (lines.Count > 0)
        {
            await ctx.Notify.SendAsync("碧乐+签到", string.Join("\r\n", lines), ct);
        }
        else
        {
            ctx.Log("无有效账号条目，未推送。");
        }
    }

    /// <summary>单账号签到，返回该账号的结果行（成功/失败均成行，由调用方合并推送，不再逐账号直推）。</summary>
    private static async Task<string> SignAsync(
        QuantumTaskContext ctx, string user, string authorization, string nonce, string signature, CancellationToken ct)
    {
        var body = new JObject
        {
            ["latitude"] = 30.64241981506348,
            ["longitude"] = 104.0431137084961
        }.ToString(Formatting.None);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SignUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Referer",
                "https://servicewechat.com/wxe86a2e5bb03ed8be/20/page-frame.html");
            request.Headers.TryAddWithoutValidation("user-agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36 MicroMessenger/7.0.20.1781(0x6700143B) NetType/WIFI MiniProgramEnv/Windows WindowsWechat/WMPF WindowsWechat(0x63090a13) XWEB/9115");
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh");
            request.Headers.TryAddWithoutValidation("X-ZHIMA-VERSION", "V2.4.11");
            request.Headers.TryAddWithoutValidation("X-ZHIMA-URL", "restful%2Fmall%2F3836%2FcheckInRecord");
            request.Headers.TryAddWithoutValidation("X-ZHIMA-NONCE", nonce);
            request.Headers.TryAddWithoutValidation("X-ZHIMA-SIGNATURE", signature);

            using var response = await ctx.Http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            ctx.Log(user + text);
            if (!response.IsSuccessStatusCode)
            {
                return $"{user}碧乐+签到结果：{QuantumText.Tag("red", "失败")} HTTP {(int)response.StatusCode} {text}";
            }

            var result = JObject.Parse(text);
            var data = result["data"];
            if (result["code"]?.ToString() == "200" && data != null && data.Type != JTokenType.Null)
            {
                // JS 的 data.point_total || data.point：0 也走回退分支
                var points = data["point_total"]?.ToString();
                if (string.IsNullOrEmpty(points) || points == "0")
                {
                    points = data["point"]?.ToString();
                }
                return $"{user}碧乐+签到结果：{QuantumText.Tag("green", "成功")} {points}，剩余积分：{data["total_point"]}";
            }
            return $"{user}碧乐+签到结果：{QuantumText.Tag("red", "失败")} {result["msg"]}";
        }
        catch (Exception e)
        {
            ctx.Log($"{user} 签到异常：{e.Message}");
            return $"{user}碧乐+签到结果：{QuantumText.Tag("red", "失败")} {e.Message}";
        }
    }

    /// <summary>取环境变量值，空白视为未配置。</summary>
    private static string Var(QuantumTaskContext ctx, string name)
        => ctx.Variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

