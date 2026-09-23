// ============================================================================
// 提交 Kyber Network 项目到量子（由 kyber-project-post.js 转换，2026-09-17）：
//   自动登录 Kyber（Odoo）取 session_id → JSON-RPC 拉取 project.kyber 项目 → 按项目Id 增量写入
//   kyber-project 自定义数据（存在则更新 Data2-Data12，否则新增）。
//   2026-09-18 会话缓存优化：session_id 存环境变量 KyberSessionId（与 kyber-leave-query 共享），
//   每次执行先用缓存会话直接拉项目——拿到 records 即未过期免登录，失效才重登并回写缓存。
// 后端要求：需含 2026-09-17 的 ctx.CustomData 契约扩展（Data6-Data15 / 过滤器 / UpdateAsync / SaveTitleAsync）；
//   旧构建会以「'QuantumCustomDataValue' does not contain a definition for 'Data6'」拒绝保存，请先重新部署后端。
// 迁移映射（计划 2.7）：axios → HttpClient；getCustomData → ctx.CustomData.QueryAsync（Data1 匹配）；
//   updateCustomData → ctx.CustomData.UpdateAsync；addCustomData → ctx.CustomData.AddAsync；
//   URLSearchParams → Uri.EscapeDataString 拼表单；console.log → ctx.Log。
// 环境变量（均必填；2026-09-19 脱敏改造：脚本不再内置服务地址与账号，全部从环境变量读取）：
//   KyberBaseUrl / KyberLogin / KyberPassword；
//   KyberSessionId 为脚本自动维护的会话缓存（勿手改，失效会自动重登刷新）。
// 与旧脚本的差异（有意为之）：
//   1) 本任务自建 HttpClient 并关闭自动重定向（ctx.Http 会跟随 303，拿不到登录响应的 Set-Cookie）；
//      代价是它不套用任务的代理开关——Kyber 为公网直连地址，如需代理请自行在客户端上配置。
//   2) 更新走批量 UpdateAsync（原脚本逐条 PUT），语义相同、往返更少。
//   3) Odoo 的 many2one 空值为 false（原脚本 element.pro_dep[1] 在此会抛异常），统一按空列处理。
//   4) 原脚本的 addCus() 定义了表头却从未调用，故这里也不写表头；如需要，在任务末尾调用
//      ctx.CustomData.SaveTitleAsync("kyber-project", "Kyber项目", new[] { "项目Id", "项目编号", "项目部门",
//      "项目BU", "项目PM", "项目类型", "项目名称", "客户名称", "项目状态", "公司名称", "项目金额", "立项日期" }, ct)
//   5) 检测到新增项目时逐条推送通知（标题「Kyber新项目通知」，正文为项目名称/项目类型/客户名称/状态/金额/立项日期），
//      落库成功后才推送；存量更新不推送。首次全量拉取会逐条推送，如嫌刷屏可改为汇总一条。
// ============================================================================
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class KyberProjectPostTask : IQuantumTask
{
    private const string CustomDataType = "kyber-project";
    private const string SessionEnv = "KyberSessionId";

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var baseUrl = Var(ctx, "KyberBaseUrl") ?? throw new Exception("缺少环境变量 KyberBaseUrl，请先在环境变量页配置");
        var login = Var(ctx, "KyberLogin") ?? throw new Exception("缺少环境变量 KyberLogin，请先在环境变量页配置");
        var password = Var(ctx, "KyberPassword") ?? throw new Exception("缺少环境变量 KyberPassword，请先在环境变量页配置");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };

        // 先验证缓存会话是否过期：直接用它拉项目，拿到 records 即未过期（KyberSessionId 与 kyber-leave-query 共享）
        var sessionId = await FindCachedSessionAsync(ctx, ct);
        var projects = string.IsNullOrEmpty(sessionId) ? null : await GetKyberProjectAsync(ctx, http, baseUrl, sessionId, ct);
        if (projects != null)
        {
            ctx.Log($"Kyber 会话缓存有效，免登录复用（session_id: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}...）");
        }
        else
        {
            ctx.Log(string.IsNullOrEmpty(sessionId) ? "无缓存会话，登录 Kyber。" : "缓存会话已失效，重新登录 Kyber。");
            sessionId = await LoginAsync(ctx, http, baseUrl, login, password, ct);
            if (string.IsNullOrEmpty(sessionId))
            {
                await ctx.Notify.SendAsync("Kyber项目同步", "Kyber 登录失败，未获取到 session_id，任务终止。", ct);
                return;
            }
            projects = await GetKyberProjectAsync(ctx, http, baseUrl, sessionId, ct);
        }
        if (projects == null || projects.Count == 0)
        {
            ctx.Log("未获取到项目数据。");
            return;
        }
        await SubmitAsync(ctx, projects, ct);
    }

    private static string Var(QuantumTaskContext ctx, string name)
        => ctx.Variables.TryGetValue(name, out var value) ? value : null;

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

    /// <summary>登录 Kyber 取 session_id 并回写 KyberSessionId 环境变量（与 kyber-leave-query 共享）；失败返回 null。</summary>
    private static async Task<string> LoginAsync(QuantumTaskContext ctx, HttpClient http, string baseUrl,
        string login, string password, CancellationToken ct)
    {
        baseUrl = baseUrl.TrimEnd('/');
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

    /// <summary>拉取 project.kyber 项目清单；会话失效/接口异常返回 null，正常（含空清单）返回列表。</summary>
    private static async Task<List<QuantumCustomDataValue>> GetKyberProjectAsync(QuantumTaskContext ctx,
        HttpClient http, string baseUrl, string sessionId, CancellationToken ct)
    {
        var results = new List<QuantumCustomDataValue>();
        var payload = new JObject
        {
            ["id"] = 29,
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["params"] = new JObject
            {
                ["model"] = "project.kyber",
                ["method"] = "web_search_read",
                ["args"] = new JArray(),
                ["kwargs"] = new JObject
                {
                    ["limit"] = 800,
                    ["offset"] = 0,
                    ["order"] = "",
                    ["context"] = new JObject
                    {
                        ["lang"] = "zh_CN",
                        ["tz"] = "Asia/Shanghai",
                        ["uid"] = 1252,
                        ["allowed_company_ids"] = new JArray { 1, 132 },
                        ["bin_size"] = true
                    },
                    ["count_limit"] = 10001,
                    ["domain"] = new JArray
                    {
                        "&",
                        new JArray { "so_type", "!=", "pj_rd" },
                        new JArray { "company_id", "in", new JArray { 1, false } }
                    },
                    ["fields"] = new JArray
                    {
                        "pro_num", "pro_template_id", "pro_dep", "pro_bu", "pro_pm", "pro_category",
                        "pro_name", "pro_customer", "state", "company_id", "rent_count",
                        "pro_comp_taxed_amount", "pro_confirm_date"
                    }
                }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{baseUrl.TrimEnd('/')}/web/dataset/call_kw/project.kyber/web_search_read")
            {
                Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Cookie", $"cids=1; session_id={sessionId}");
            request.Headers.TryAddWithoutValidation("User-Agent", "Apifox/1.0.0 (https://apifox.com)");
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                ctx.Log($"获取 Kyber 项目失败：HTTP {(int)response.StatusCode} {body}");
                return null;
            }

            var records = JObject.Parse(body)["result"]?["records"] as JArray;
            if (records == null)
            {
                // 会话过期时 Odoo 回 error 节点（或跳登录页的 HTML），统一按失效处理走重登
                ctx.Log($"Kyber 返回体无 records 节点：{body}");
                return null;
            }
            ctx.Log($"获取到的项目数量：{records.Count}");
            foreach (var element in records)
            {
                results.Add(new QuantumCustomDataValue
                {
                    Type = CustomDataType,
                    Data1 = Str(element["id"]),             // 项目Id
                    Data2 = Str(element["pro_num"]),        // 项目编号
                    Data3 = Str(element["pro_dep"]),        // 项目部门
                    Data4 = Str(element["pro_bu"]),         // 项目BU
                    Data5 = Str(element["pro_pm"]),         // 项目PM
                    Data6 = Str(element["pro_category"]),   // 项目类型
                    Data7 = Str(element["pro_name"]),       // 项目名称
                    Data8 = Str(element["pro_customer"]),   // 公司名称
                    Data9 = Str(element["state"]),          // 项目状态
                    Data10 = Str(element["company_id"]),    // 公司名称
                    Data11 = Str(element["pro_comp_taxed_amount"]), // 项目金额
                    Data12 = Str(element["pro_confirm_date"])       // 立项日期
                });
            }
        }
        catch (Exception e)
        {
            ctx.Log($"获取 Kyber 项目异常：{e.Message}");
            return null;
        }
        return results;
    }

    /// <summary>按项目Id（Data1）增量写入：已存在则更新 Data2-Data12，否则新增。</summary>
    private static async Task SubmitAsync(QuantumTaskContext ctx, List<QuantumCustomDataValue> projects, CancellationToken ct)
    {
        var existing = await ctx.CustomData.QueryAsync(CustomDataType, ct: ct);
        var byKey = new Dictionary<string, QuantumCustomDataValue>();
        foreach (var row in existing)
        {
            if (!string.IsNullOrEmpty(row.Data1))
            {
                byKey[row.Data1] = row;
            }
        }

        var adds = new List<QuantumCustomDataValue>();
        var updates = new List<QuantumCustomDataValue>();
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (byKey.TryGetValue(project.Data1, out var current))
            {
                ctx.Log($"更新项目：{project.Data1}");
                updates.Add(current with
                {
                    Data2 = project.Data2,
                    Data3 = project.Data3,
                    Data4 = project.Data4,
                    Data5 = project.Data5,
                    Data6 = project.Data6,
                    Data7 = project.Data7,
                    Data8 = project.Data8,
                    Data9 = project.Data9,
                    Data10 = project.Data10,
                    Data11 = project.Data11,
                    Data12 = project.Data12
                });
            }
            else
            {
                ctx.Log($"新增项目：{project.Data1}");
                adds.Add(project);
            }
        }

        if (adds.Count > 0)
        {
            await ctx.CustomData.AddAsync(adds, ct);
            // 落库成功后才推送，避免写入失败时误报；存量更新不推送
            await NotifyNewProjectsAsync(ctx, adds, ct);
        }
        if (updates.Count > 0)
        {
            await ctx.CustomData.UpdateAsync(updates, ct);
        }
        ctx.Log($"同步完成：新增 {adds.Count} 条，更新 {updates.Count} 条。");
    }

    /// <summary>新增项目逐条推送通知，标题固定「Kyber新项目通知」，正文为项目名称/类型/客户/状态/金额。</summary>
    private static async Task NotifyNewProjectsAsync(QuantumTaskContext ctx, List<QuantumCustomDataValue> adds,
        CancellationToken ct)
    {
        foreach (var project in adds)
        {
            ct.ThrowIfCancellationRequested();
            var content = string.Join("\n",
                Display(project.Data7),                  // 项目名称
                Display(project.Data6),                  // 项目类型
                Display(project.Data8),                  // 客户名称
                $"状态：{Display(project.Data9)}",        // 项目状态
                $"项目金额：{Display(project.Data11)}",  // 项目金额
                $"立项日期：{Display(project.Data12)}"); // 立项日期
            await ctx.Notify.SendAsync("Kyber新项目通知", content, ct);
        }
        ctx.Log($"已推送 {adds.Count} 条新项目通知。");
    }

    /// <summary>空值占位，避免通知里出现空白行。</summary>
    private static string Display(string value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

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
