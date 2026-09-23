// ============================================================================
// Kyber 离职申请查询（2026-09-18 新写）：
//   定时拉取 Kyber（Odoo）hr.leave.kyber 离职申请全量清单（name_search，limit 3200），
//   按记录 Id 增量写入 kyber-leave 自定义数据；发现新增离职人员时把名单汇总进「一条」通知
//   （区别于 kyber-project-post 的逐条推送）。
// 授权与 kyber-project-post.cs 一致（登录页取 CSRF → 表单登录 → Set-Cookie 取 session_id），
//   并做会话缓存优化：登录成功把 session_id 写入环境变量 KyberSessionId；每次执行先用缓存会话
//   直接调业务接口——返回体带 result 即视为未过期、免登录复用，失败（过期/未缓存/接口异常）才
//   重新登录并回写缓存（参照 dygangs 的 qbSID 缓存套路，Odoo 会话寿命由服务端定，以实测为准）。
// 后端要求：需含 2026-09-17 的 ctx.CustomData / ctx.Env 契约（与 kyber-project-post 相同构建即可）。
// 环境变量（均必填，与 kyber-project-post 同一套变量；2026-09-19 脱敏改造：脚本不再内置服务地址与账号）：
//   KyberBaseUrl / KyberLogin / KyberPassword；
//   KyberSessionId 为脚本自动维护的会话缓存（与 kyber-project-post 共享，勿手改，失效会自动重登刷新）。
// 与 kyber-project-post.cs 的差异（有意为之）：
//   1) 会话免登复用（见上），登录频次从每次执行降为会话过期才登。
//   2) 首次执行只建基线：全部记录入库 + 一条计数汇总通知（离职清单可能上千条，不逐个报名单）；
//      之后每次新增汇总进一条通知，超过 30 名截断为计数提示。
//   3) 自定义数据仅两列：Data1=记录Id（hr.leave.kyber 主键）、Data2=显示名称——name_search 只回
//      [id, display_name] 无更多字段；存量显示名称变化时更新 Data2。
// ============================================================================
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class KyberLeaveQueryTask : IQuantumTask
{
    private const string CustomDataType = "kyber-leave";
    private const string SessionEnv = "KyberSessionId";

    /// <summary>一条离职申请记录（name_search 返回的 [id, display_name] 对）。</summary>
    private sealed record LeaveEntry(long Id, string Name);

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var baseUrl = (Var(ctx, "KyberBaseUrl") ?? throw new Exception("缺少环境变量 KyberBaseUrl，请先在环境变量页配置")).TrimEnd('/');
        var login = Var(ctx, "KyberLogin") ?? throw new Exception("缺少环境变量 KyberLogin，请先在环境变量页配置");
        var password = Var(ctx, "KyberPassword") ?? throw new Exception("缺少环境变量 KyberPassword，请先在环境变量页配置");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };

        // 先验证缓存会话是否过期：直接用它在业务接口上拉数据，拿到 result 即未过期
        var sessionId = await FindCachedSessionAsync(ctx, ct);
        var leaves = string.IsNullOrEmpty(sessionId) ? null : await FetchAsync(ctx, http, baseUrl, sessionId, ct);
        if (leaves != null)
        {
            ctx.Log($"Kyber 会话缓存有效，免登录复用（session_id: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}...）");
        }
        else
        {
            ctx.Log(string.IsNullOrEmpty(sessionId) ? "无缓存会话，登录 Kyber。" : "缓存会话已失效，重新登录 Kyber。");
            sessionId = await LoginAsync(ctx, http, baseUrl, login, password, ct);
            if (string.IsNullOrEmpty(sessionId))
            {
                await ctx.Notify.SendAsync("Kyber离职查询", "Kyber 登录失败，未获取到 session_id，任务终止。", ct);
                return;
            }
            leaves = await FetchAsync(ctx, http, baseUrl, sessionId, ct);
            if (leaves == null)
            {
                await ctx.Notify.SendAsync("Kyber离职查询", "Kyber 登录成功但仍未取到离职申请数据，任务终止（详见任务日志）。", ct);
                return;
            }
        }
        ctx.Log($"获取到的离职申请数量：{leaves.Count}");
        await SyncAsync(ctx, leaves, ct);
    }

    private static string Var(QuantumTaskContext ctx, string name)
        => ctx.Variables.TryGetValue(name, out var value) ? value : null;

    /// <summary>调 hr.leave.kyber/name_search 拉全量清单；会话失效/接口异常返回 null，正常（含空清单）返回列表。</summary>
    private static async Task<List<LeaveEntry>> FetchAsync(QuantumTaskContext ctx, HttpClient http,
        string baseUrl, string sessionId, CancellationToken ct)
    {
        var payload = new JObject
        {
            ["id"] = 24,
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["params"] = new JObject
            {
                ["model"] = "hr.leave.kyber",
                ["method"] = "name_search",
                ["args"] = new JArray(),
                ["kwargs"] = new JObject
                {
                    ["limit"] = 3200,
                    ["args"] = new JArray(),
                    ["operator"] = "ilike",
                    ["context"] = new JObject
                    {
                        ["lang"] = "zh_CN",
                        ["tz"] = "Asia/Shanghai",
                        ["allowed_company_ids"] = new JArray { 1, 2, 3, 4, 5, 6, 7, 8, 132 }
                    }
                }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{baseUrl}/web/dataset/call_kw/hr.leave.kyber/name_search")
            {
                Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Cookie", $"cids=1; session_id={sessionId}");
            request.Headers.TryAddWithoutValidation("User-Agent", "Apifox/1.0.0 (https://apifox.com)");
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            // 会话过期时 Odoo 回 error 节点（或跳登录页的 HTML），统一按失效处理走重登
            if (JObject.Parse(body)["result"] is not JArray result)
            {
                ctx.Log($"离职申请接口未返回 result（HTTP {(int)response.StatusCode}）：{Snippet(body)}");
                return null;
            }
            var leaves = new List<LeaveEntry>();
            foreach (var item in result)
            {
                // name_search 标准回包是 [id, display_name] 数组；防御性兼容 {id, name} 对象形态
                if (item is JArray pair && pair.Count >= 2)
                {
                    var idText = Str(pair[0]);
                    leaves.Add(new LeaveEntry(long.TryParse(idText, out var id) ? id : 0, Str(pair[1])));
                }
                else if (item is JObject obj)
                {
                    var idText = Str(obj["id"]);
                    leaves.Add(new LeaveEntry(long.TryParse(idText, out var id) ? id : 0, Str(obj["name"])));
                }
            }
            return leaves;
        }
        catch (Exception e)
        {
            ctx.Log($"获取离职申请异常：{e.Message}");
            return null;
        }
    }

    /// <summary>按记录 Id（Data1）增量写入；首次执行建基线只发计数汇总，之后新增汇总进一条通知。</summary>
    private static async Task SyncAsync(QuantumTaskContext ctx, List<LeaveEntry> leaves, CancellationToken ct)
    {
        var existing = await ctx.CustomData.QueryAsync(CustomDataType, ct: ct);
        var byId = new Dictionary<string, QuantumCustomDataValue>();
        foreach (var row in existing)
        {
            if (!string.IsNullOrEmpty(row.Data1))
            {
                byId[row.Data1] = row;
            }
        }

        var adds = new List<QuantumCustomDataValue>();
        var updates = new List<QuantumCustomDataValue>();
        foreach (var leave in leaves.OrderBy(l => l.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (byId.TryGetValue(leave.Id.ToString(), out var current))
            {
                if (current.Data2 != leave.Name)
                {
                    ctx.Log($"更新离职申请：{leave.Id}");
                    updates.Add(current with { Data2 = leave.Name });
                }
            }
            else
            {
                ctx.Log($"新增离职申请：{leave.Id} {leave.Name}");
                adds.Add(new QuantumCustomDataValue
                {
                    Type = CustomDataType,
                    Data1 = leave.Id.ToString(),  // 记录Id
                    Data2 = leave.Name            // 显示名称
                });
            }
        }

        if (adds.Count > 0)
        {
            await ctx.CustomData.SaveTitleAsync(CustomDataType, "Kyber离职申请", new[] { "记录Id", "显示名称" }, ct);
            await ctx.CustomData.AddAsync(adds, ct);
        }
        if (updates.Count > 0)
        {
            await ctx.CustomData.UpdateAsync(updates, ct);
        }

        if (existing.Count == 0)
        {
            // 首次执行只建基线，不逐个报名单（清单可能上千条）
            if (adds.Count > 0)
            {
                await ctx.Notify.SendAsync("Kyber离职通知",
                    $"首次同步完成：共 {adds.Count} 条离职申请记录已入库（数据管理-Kyber离职申请），此后新增离职人员将汇总通知。",
                    ct);
            }
        }
        else if (adds.Count > 0)
        {
            await NotifyNewLeavesAsync(ctx, adds, ct);
        }
        ctx.Log($"同步完成：新增 {adds.Count} 条，更新 {updates.Count} 条（存量 {existing.Count} 条）。");
    }

    /// <summary>新增离职人员汇总进一条通知；超过 30 名截断为计数提示，避免超长消息。</summary>
    private static async Task NotifyNewLeavesAsync(QuantumTaskContext ctx, List<QuantumCustomDataValue> adds,
        CancellationToken ct)
    {
        var lines = new List<string> { $"新增离职人员 {adds.Count} 名：" };
        var shown = Math.Min(adds.Count, 30);
        for (var i = 0; i < shown; i++)
        {
            lines.Add($"{i + 1}. {Display(adds[i].Data2)}");
        }
        if (adds.Count > shown)
        {
            lines.Add($"……等共 {adds.Count} 名，完整名单见数据管理-Kyber离职申请。");
        }
        await ctx.Notify.SendAsync("Kyber离职通知", string.Join("\n", lines), ct);
    }

    /// <summary>登录 Kyber 取 session_id 并回写 KyberSessionId 环境变量；失败返回 null。</summary>
    private static async Task<string> LoginAsync(QuantumTaskContext ctx, HttpClient http, string baseUrl,
        string login, string password, CancellationToken ct)
    {
        try
        {
            // 第一步：访问登录页取 CSRF token 与初始会话 cookie（CSRF 校验需要同一个 session）
            string loginPage;
            string initialCookie;
            using (var pageRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/web/login"))
            using (var pageResponse = await http.SendAsync(pageRequest, ct))
            {
                loginPage = await pageResponse.Content.ReadAsStringAsync(ct);
                initialCookie = ExtractCookie(HeaderText(pageResponse, "Set-Cookie"), "session_id");
            }
            var csrfMatch = Regex.Match(loginPage, "name=\"csrf_token\"[^>]*value=\"([^\"]+)\"");
            if (!csrfMatch.Success)
            {
                ctx.Log("无法从登录页面获取 CSRF token");
                return null;
            }

            // 第二步：提交登录表单（服务端 303 跳转，从响应头取新 session_id）
            var form = string.Join("&",
                $"csrf_token={Uri.EscapeDataString(csrfMatch.Groups[1].Value)}",
                $"login={Uri.EscapeDataString(login)}",
                $"password={Uri.EscapeDataString(password)}",
                "redirect=");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/web/login")
            {
                Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            if (!string.IsNullOrEmpty(initialCookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", $"session_id={initialCookie}");
            }

            using var response = await http.SendAsync(request, ct);
            var sessionId = ExtractCookie(HeaderText(response, "Set-Cookie"), "session_id");
            if (string.IsNullOrEmpty(sessionId))
            {
                ctx.Log($"登录响应中未找到 session_id（HTTP {(int)response.StatusCode}）");
                return null;
            }
            ctx.Log($"登录成功，session_id: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}...");

            try
            {
                await ctx.Env.SaveAsync(SessionEnv, sessionId,
                    "Kyber(Odoo) 会话缓存（脚本登录后自动维护，勿手改）", true, ct);
                ctx.Log($"session_id 已写入环境变量 {SessionEnv}，下次执行免登录复用。");
            }
            catch (Exception e)
            {
                ctx.Log($"session_id 写入环境变量异常（不影响本次执行）：{e.Message}");
            }
            return sessionId;
        }
        catch (Exception e)
        {
            ctx.Log($"Kyber 登录异常：{e.Message}");
            return null;
        }
    }

    /// <summary>读 KyberSessionId 环境变量缓存；异常不阻断流程（按无缓存处理）。</summary>
    private static async Task<string> FindCachedSessionAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            return (await ctx.Env.QueryAsync(SessionEnv, null, ct)).FirstOrDefault()?.Value;
        }
        catch (Exception e)
        {
            ctx.Log($"读取会话缓存异常（按无缓存处理）：{e.Message}");
            return null;
        }
    }

    /// <summary>空值占位，避免通知里出现空白行。</summary>
    private static string Display(string value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

    /// <summary>日志用截断：响应体过长只留前 300 字符。</summary>
    private static string Snippet(string body)
        => string.IsNullOrEmpty(body) ? "(空)" : (body.Length <= 300 ? body : body.Substring(0, 300) + "…");

    /// <summary>从 Set-Cookie 头提取指定 cookie 值。</summary>
    private static string ExtractCookie(string setCookieHeaders, string name)
    {
        if (string.IsNullOrEmpty(setCookieHeaders))
        {
            return null;
        }
        var match = Regex.Match(setCookieHeaders, $@"(?:^|\n){Regex.Escape(name)}=([^;\n]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>把响应的同名多值头拼成一个串（对应 axios 的 set-cookie 数组）。</summary>
    private static string HeaderText(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join("\n", values) : null;

    /// <summary>Odoo 字段取值：many2one 为 [id, name] 时取名称，false/null 记空，其余原样。</summary>
    private static string Str(JToken token)
    {
        if (token == null || token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return null;
        }
        if (token.Type == JTokenType.Boolean)
        {
            return token.Value<bool>() ? "true" : null;
        }
        if (token.Type == JTokenType.Array)
        {
            return token.Count() > 1 ? Str(token[1]) : null;
        }
        return token.ToString();
    }
}
