// ============================================================================
// qBittorrent 添加磁力任务（由 qBittorrentAdd.js + qBittorrentBase.js 转换，2026-09-17）
// 迁移映射（计划 2.7）：got/FormData → ctx.Http + MultipartFormDataContent；sendNotify → ctx.Notify；
//   process.env → ctx.Variables；console.log → ctx.Log；错误对象的 name/code → 异常类型/消息。
// 单文件编译：原 qBittorrentBase.js 的 qblogin / addTorrents 已内联本文件（qbSearch.cs 另内联一份，
//   两任务各自独立，不能互相 require）。
// 环境变量：qBittorrentURL（必填，如 http://192.168.x.x:8080，需带 http:// 或 https:// 前缀）
//   command（磁力链接，多个换行分隔）
//   qbusername / qbpassword（可选；缺失则跳过登录，需在 qB 设置>Web UI 中对量子所在 IP 放行或关闭身份校验）
//   qbSID（自动维护，勿手改：qB 登录会话缓存——12 小时内免登录复用，超时或校验失效自动重登并回写）
// 与旧脚本的差异（有意为之）：本任务按 HTTP 状态码判定成败（旧脚本收到 403 也报「添加成功」）；
//   qB 5.x 对「种子已在下载列表」的重复添加返回 409，按「已在 qB」上报而非失败。
// ============================================================================
using Quantum.Plugins;

public class QBittorrentAddTask : IQuantumTask
{
    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var qBittorrentURL = Var(ctx, "qBittorrentURL");
        if (string.IsNullOrEmpty(qBittorrentURL))
        {
            await ctx.Notify.SendAsync("qBittorrent", "未设置qBittorrent服务地址，请添加量子变量 ：qBittorrentURL。", ct);
            return;
        }

        var cookie = await GetCookieAsync(ctx, qBittorrentURL, ct);
        var msg = await AddTorrentsAsync(ctx, qBittorrentURL, cookie, Var(ctx, "command"), null, ct);
        if (!string.IsNullOrEmpty(msg))
        {
            await ctx.Notify.SendAsync("qBittorrent", msg, ct);
        }
    }

    private static string Var(QuantumTaskContext ctx, string name)
        => ctx.Variables.TryGetValue(name, out var value) ? value : null;

    private const string QbSidEnv = "qbSID";

    /// <summary>
    /// 取 qB 会话 Cookie：优先复用环境变量 qbSID 缓存（变量记录自带更新时间）——
    /// 无缓存或超过 12 小时直接重登；12 小时内先调 app/version 校验，失效再重登；重登成功回写 qbSID。
    /// 未配账号密码时维持旧行为：返回空 cookie，依赖 qB 的 IP 白名单放行。
    /// </summary>
    private static async Task<string> GetCookieAsync(QuantumTaskContext ctx, string baseUrl, CancellationToken ct)
    {
        baseUrl = baseUrl.TrimEnd('/');
        var user = Var(ctx, "qbusername");
        var password = Var(ctx, "qbpassword");
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

    /// <summary>添加磁力任务；失败时发通知并返回空串（与旧脚本一致，避免重复推送）。</summary>
    private static async Task<string> AddTorrentsAsync(QuantumTaskContext ctx, string baseUrl, string cookie,
        string urls, string savePath, CancellationToken ct)
    {
        baseUrl = baseUrl.TrimEnd('/');
        ctx.Log($"磁力信息：{urls}");
        try
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(urls ?? string.Empty), "urls" },
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
            var body = await response.Content.ReadAsStringAsync(ct);
            ctx.Log($"添加磁力响应：HTTP {(int)response.StatusCode} {body.Trim()}");
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // qB 5.x 重复添加返回 409，种子已在下载列表中，无需再提交
                var dup = $"磁力已在 qB 下载列表中（HTTP 409），未重复添加。\n{urls}";
                ctx.Log(dup);
                await ctx.Notify.SendAsync("qBittorrent", $"{QuantumText.Tag("orange", "已在列表")} {dup}", ct);
                return string.Empty;
            }
            if (!response.IsSuccessStatusCode)
            {
                var reason = $"HTTP {(int)response.StatusCode} {body.Trim()}\n"
                    + "（403 多为未登录且量子 IP 不在白名单，请配置 qbusername/qbpassword 或在 qB 中放行）";
                ctx.Log($"磁力任务添加失败：{reason}");
                await ctx.Notify.SendAsync("qBittorrent", $"{QuantumText.Tag("red", "添加失败")} {reason}", ct);
                return string.Empty;
            }
            return $"{QuantumText.Tag("green", "添加成功")} 磁力任务已提交 qB 下载。";
        }
        catch (Exception e)
        {
            ctx.Log(e.ToString());
            await ctx.Notify.SendAsync("qBittorrent",
                $"{QuantumText.Tag("red", "添加失败")} {Kind(e)}：{e.Message}", ct);
            return string.Empty;
        }
    }

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
