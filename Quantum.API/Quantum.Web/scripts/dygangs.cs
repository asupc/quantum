// ============================================================================
// 电影港续集监听（由 dygangs.js 转换，2026-09-17）：抓取配置的剧集页，把新出现的磁力链接写入
// video_update_monitoring 自定义数据（Data1 来源页 / Data2 标题 / Data3 磁力），并维护表头；
// 新磁力按 2160P/4K 筛选提交 qBittorrent 下载（保存子目录=片名）并推送 App 汇总通知。
// （2026-09-17 补漏：.cs 转换时曾遗漏旧脚本的 addTorrents/sendNotify 两段，导致只入库、不下载、不通知。）
// 后端要求：需含 2026-09-17 的 ctx.CustomData 契约扩展（Data6-Data15 / 过滤器 / UpdateAsync / SaveTitleAsync）；
//   旧构建会以「'QuantumCustomDataValue' does not contain a definition for 'Data6'」拒绝保存，请先重新部署后端。
// 迁移映射（计划 2.7）：cheerio → HtmlAgilityPack；iconv-lite(gb2312) → CodePagesEncodingProvider + GB2312；
//   api → ctx.Http；getCustomData(type,{Data1}) → ctx.CustomData.QueryAsync(过滤器)；
//   addCustomData → AddAsync；addOrUpdateCustomDataTitle → SaveTitleAsync；console.log → ctx.Log；
//   qBittorrentBase.js 的 qblogin/addTorrents → 本文件内联（脚本单文件编译，不能跨文件引用）。
// 环境变量：scripts_dygangs_urls（多页用 & 分隔，仅作监听清单为空时的回退）；
//   qBittorrentURL（必填，带 http:// 或 https:// 前缀，如 http://192.168.x.x:8080）；
//   qbusername / qbpassword（可选；缺失则跳过登录，需在 qB 设置>Web UI 中对量子所在 IP 放行或关闭身份校验）；
//   qbSID（自动维护，勿手改：qB 登录会话缓存——12 小时内免登录复用，超时或校验失效自动重登并回写）。
// 与旧脚本的差异（有意为之）：
//   1) 页面是 gb2312 编码，必须按字节取回再解码——ctx.Http.GetStringAsync 会按 UTF-8 解，中文标题会乱码/抛错；
//      GB2312 在本平台映射到代码页 936（GBK），若某页出现 GB18030 独有字，改用 "GB18030" 即可。
//   2) 标题取 InnerText 并 Trim（旧脚本取首个子文本节点，不 Trim）。
//   3) 顺带补了抓取失败与无结果的日志/跳过，旧脚本请求异常会让整批 URL 中断。
//   4) 兼容旧版清单（dygangs.js 时代手动维护，Data1=影片名 / Data2=来源页 / Data3=自动下载 / Data4=是否启用）：
//      来源页按 Data1/Data2 中第一个 http 地址解析（新清单 Data1 是 URL，旧清单 Data2 是 URL），
//      旧「是否启用=否」的行跳过；历史去重同时比对 Data3（新行磁力）与 Data4（旧行磁力），
//      否则升级后首跑会把旧「影视监听记录」里的磁力整批重复采集。
//   5) 下载与推送不再看旧「自动下载/消息推送」开关：推送每页合并一条 App 通知（旧脚本逐条推送且汇总文案从未发出）；
//      推送正文只列剧集标题不带磁力串（2026-09-20 改——磁力动辄百余字节刷屏且手机上无从复制使用，
//      磁力仍完整存 video_update_monitoring 的 Data3 与任务日志，需要时去自定义数据页/日志取）；
//      下载保留旧 2160P/4K 筛选——仅标题含 2160P/4K（忽略大小写，旧脚本先把标题转大写再匹配）的磁力提交 QB，
//      非高清条目只入库+推送不下载；EnablePush 由平台通知门面自行把关，脚本侧不再判断。
//   6) 提交成败按 HTTP 状态码 + qB 响应体判定（旧脚本收到 403 / 响应体 Fails. 也报「添加成功」）。
//   去重仍与旧脚本一致：按来源页查询（Data1/Data2 包含匹配）后，用磁力精确相等判断是否已采集。
// ============================================================================
using System.Text;
using HtmlAgilityPack;
using Quantum.Plugins;

public class DygangsMonitorTask : IQuantumTask
{
    private const string CustomDataType = "video_update_monitoring";
    private const string ItemType = "video_update_monitoring_item";
    private const string DefaultUrls = "https://www.dygangs.net/dmq/20211031/48089.htm";

    private static readonly string[] Titles = ["来源", "标题", "磁力"];

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        // .NET 默认只带 UTF-8/ASCII 等少数编码，gb2312 需先注册代码页提供程序（进程内幂等）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var pageEncoding = Encoding.GetEncoding("GB2312");

        await ctx.CustomData.SaveTitleAsync(CustomDataType, "视频更新监听", Titles, ct);

        // 来源页清单：优先取监听清单 video_update_monitoring_item（由搜索订阅脚本 dygangs_search.cs 维护，
        // 兼容 dygangs.js 时代的旧行，见文件头差异 4），清单为空时回退 scripts_dygangs_urls 变量，再兜底内置默认页
        var pages = new List<(string Url, string Name)>();
        foreach (var n in await ctx.CustomData.QueryAsync(ItemType, ct: ct))
        {
            var urlNew = HttpUrl(n.Data1);
            var url = urlNew ?? HttpUrl(n.Data2);
            if (url == null)
            {
                ctx.Log($"[{(n.Data1 ?? n.Data2)?.Trim()}]监听清单行没有来源页 URL，请修正该行，已跳过。");
                continue;
            }
            if (string.Equals(n.Data4?.Trim(), "否", StringComparison.Ordinal))
            {
                ctx.Log($"[{(urlNew == null ? n.Data1 : n.Data2)?.Trim()}]旧清单「是否启用」为否，跳过。");
                continue;
            }
            // 名称：新行 Data1=URL（片名在 Data2），旧行 Data2=URL（影片名在 Data1）
            var name = (urlNew != null ? n.Data2 : n.Data1)?.Trim();
            pages.Add((url, string.IsNullOrEmpty(name) ? url : name));
        }
        if (pages.Count == 0)
        {
            var configured = ctx.Variables.TryGetValue("scripts_dygangs_urls", out var value) ? value : null;
            pages = (string.IsNullOrEmpty(configured) ? DefaultUrls : configured)
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(u => (Url: u.Trim(), Name: u.Trim()))
                .ToList();
        }
        ctx.Log($"本次监听 {pages.Select(p => p.Url).Distinct().Count()} 个来源页。");

        // qB 提交：地址整轮读一次；未配置只提示一次；登录一次取 SID 整轮复用
        var qbUrl = Var(ctx, "qBittorrentURL");
        var qbUrlMissingLogged = false;
        var qbCookie = null as string;

        var total = 0;
        foreach (var page in pages.DistinctBy(p => p.Url))
        {
            ct.ThrowIfCancellationRequested();
            // 去重集合：新历史行 Data1=来源页 / Data3=磁力；旧「影视监听记录」行 Data1=影片名 / Data2=来源页 / Data4=磁力。
            // 两路都查，否则升级首跑查不到旧行，旧磁力会被整批当成新采集
            var datas = (await ctx.CustomData.QueryAsync(CustomDataType, new QuantumCustomDataFilter { Data1 = page.Url }, ct))
                .Concat(await ctx.CustomData.QueryAsync(CustomDataType, new QuantumCustomDataFilter { Data2 = page.Url }, ct))
                .ToList();

            byte[] bytes;
            try
            {
                bytes = await ctx.Http.GetByteArrayAsync(page.Url, ct);
            }
            catch (Exception e)
            {
                ctx.Log($"[{page.Name}]页面抓取失败，跳过：{e.Message}");
                continue;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(pageEncoding.GetString(bytes));
            var anchors = doc.DocumentNode.SelectNodes("//a[starts-with(@href,'magnet:?xt=urn:btih:')]");
            if (anchors == null || anchors.Count == 0)
            {
                ctx.Log($"[{page.Name}]未找到磁力链接。");
                continue;
            }

            var newData = new List<QuantumCustomDataValue>();
            foreach (var anchor in anchors)
            {
                ct.ThrowIfCancellationRequested();
                var link = anchor.GetAttributeValue("href", null);
                if (string.IsNullOrEmpty(link))
                {
                    continue;
                }
                var name = anchor.InnerText?.Trim();
                // Data3=新行磁力；Data4=旧「影视监听记录」磁力
                if (datas.Any(n => n.Data3 == link || n.Data4 == link))
                {
                    ctx.Log($"[{name}]已采集.");
                    continue;
                }
                ctx.Log($"[{name}]新采集");
                newData.Add(new QuantumCustomDataValue
                {
                    Type = CustomDataType,
                    Data1 = page.Url,
                    Data2 = name,
                    Data3 = link
                });
            }

            if (newData.Count > 0)
            {
                await ctx.CustomData.AddAsync(newData, ct);

                // 提交 qBittorrent：按 2160P/4K 筛选逐条提交（保存子目录=片名），成败落日志并计数
                var submitted = 0;
                var eligible = 0;
                if (string.IsNullOrEmpty(qbUrl))
                {
                    if (!qbUrlMissingLogged)
                    {
                        qbUrlMissingLogged = true;
                        ctx.Log("未设置qBittorrent服务地址，请添加量子变量：\r\n" +
                                "qBittorrentURL qb链接地址 如：http://192.168.x.x:8080\r\n" +
                                "qbusername qb登录账号（可选）\r\nqbpassword qb登录密码（可选）");
                    }
                    ctx.Log($"[{page.Name}]未配置 qBittorrentURL，{newData.Count} 条新磁力未提交下载。");
                }
                else
                {
                    qbCookie ??= await GetCookieAsync(ctx, qbUrl, ct);
                    foreach (var item in newData)
                    {
                        ct.ThrowIfCancellationRequested();
                        // 与旧脚本一致：仅 2160P/4K 提交下载，其余跳过（标题忽略大小写匹配）
                        if (!IsHighRes(item.Data2))
                        {
                            ctx.Log($"[{page.Name}]跳过下载不包含指定分辨率的项目：{item.Data2}");
                            continue;
                        }
                        eligible++;
                        if (await AddTorrentAsync(ctx, qbUrl, qbCookie, item.Data3, page.Name, ct))
                        {
                            submitted++;
                        }
                    }
                }

                // App 通知（每页一条汇总，只列标题不带磁力；磁力在入库 Data3 与任务日志里；是否实际送达由平台推送开关决定）
                var detail = string.Join("\r\n", newData.Select(n => n.Data2));
                string tail;
                if (string.IsNullOrEmpty(qbUrl))
                {
                    tail = $"{QuantumText.Tag("orange", "未配置")} qBittorrentURL，未自动下载。";
                }
                else
                {
                    tail = $"已自动提交 QB 下载 {QuantumText.Color("green", submitted.ToString())} 条";
                    if (submitted < eligible)
                    {
                        tail += $"（{QuantumText.Color("red", (eligible - submitted).ToString())} 条失败，见任务日志）";
                    }
                    if (eligible < newData.Count)
                    {
                        tail += $"，另有 {newData.Count - eligible} 条非 2160P/4K 未提交";
                    }
                    tail += "。";
                }
                await ctx.Notify.SendAsync("电影港更新",
                    $"【{page.Name}】采集到 {newData.Count} 条新剧集：\r\n{detail}\r\n\r\n{tail}", ct);
            }
            ctx.Log($"[{page.Name}]本页磁力 {anchors.Count} 条，新采集 {newData.Count} 条。");
            total += newData.Count;
        }

        ctx.Log($"监听结束：共新采集 {total} 条。");
    }

    /// <summary>值本身是 http(s) 地址则返回去空白后的值，否则 null（兼容新旧清单来源页所在列不同）。</summary>
    private static string HttpUrl(string value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || !text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    private static string Var(QuantumTaskContext ctx, string name)
        => ctx.Variables.TryGetValue(name, out var value) ? value : null;

    /// <summary>标题含 2160P 或 4K（忽略大小写）才提交下载；等价旧脚本的 Includes 判定（旧脚本先把标题转大写）。</summary>
    private static bool IsHighRes(string title)
        => !string.IsNullOrEmpty(title) && (title.Contains("2160P", StringComparison.OrdinalIgnoreCase)
            || title.Contains("4K", StringComparison.OrdinalIgnoreCase));

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
