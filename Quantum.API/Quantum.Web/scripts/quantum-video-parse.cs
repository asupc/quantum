// ============================================================================
// 视频解析（由 quantum-video-parse.js 转换，2026-09-17）：调用内网视频解析服务，
// 把分享链接解析出的标题/作者/视频/封面推送通知，并将结果转存 NAS。
// 迁移映射（计划 2.7）：require('./quantum') sendNotify → ctx.Notify；api → ctx.Http（预配代理/超时 100s）；
//   set-cookie → response.Headers.TryGetValues("Set-Cookie")；JSON.stringify/parse → Newtonsoft JObject；
//   console.log → ctx.Log；process.env.videoParseUrl → ctx.Variables["videoParseUrl"]。
// 环境变量：videoParseUrl（要解析的视频分享链接；为空只提示不解析，与旧脚本一致）；
//   videoParseServiceUrl（服务地址，必填；2026-09-19 脱敏改造：脚本不再内置地址）；
//   videoParseUsername / videoParsePassword（登录凭据，均必填）。
// 与旧脚本的差异（有意为之）：
//   1) 旧通道发三条消息（文本摘要 / 裸视频链接 / MessageType=2 封面图），现按作品类型拆条富媒体推送：
//      视频作品 → 单条视频气泡（封面作播放预览，配文=标题/作者）；
//      图文作品（无视频直链）→ 逐张图片气泡（首张带配文；live_photo_url 非空的实况照片按视频气泡推、图片作预览）；
//      背景音乐不再单独推音频气泡（2026-09-18 定：一起推的是视频/图片内容不是音频），音乐直链只留日志与 NAS 存档；
//      是否实际推送由平台推送开关决定。
//   2) NAS 保存失败（HTTP 非 2xx 或响应 code!=200）补推一条失败通知——旧脚本只 console.log，保存失败用户无感知。
//   3) 登录/解析/转存的网络异常走顶部 catch 推「执行异常」，与旧脚本 catch 行为一致；
//      ctx.Http 默认跟随重定向（与旧 api() 相同），无需自建客户端。
// ============================================================================
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class VideoParseTask : IQuantumTask
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36 Edg/146.0.0.0";

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            var serviceUrl = (Var(ctx, "videoParseServiceUrl") ?? throw new Exception("缺少环境变量 videoParseServiceUrl，请先在环境变量页配置")).TrimEnd('/');
            var username = Var(ctx, "videoParseUsername") ?? throw new Exception("缺少环境变量 videoParseUsername，请先在环境变量页配置");
            var password = Var(ctx, "videoParsePassword") ?? throw new Exception("缺少环境变量 videoParsePassword，请先在环境变量页配置");
            var videoUrl = Var(ctx, "videoParseUrl");

            // 与旧脚本一致：先登录拿 cookie，再判断有没有链接
            var cookie = BuildCookie(await LoginAsync(ctx, serviceUrl, username, password, ct));

            if (string.IsNullOrWhiteSpace(videoUrl))
            {
                ctx.Log("未提供视频链接（变量 videoParseUrl 为空）。");
                await NotifyAsync(ctx, "视频解析", "未提供视频链接，请提供要解析的视频URL", ct);
                return;
            }

            var parseResult = await ParseVideoAsync(ctx, serviceUrl, videoUrl, cookie, ct);
            if (parseResult == null || parseResult.Value<int?>("code") != 200 || parseResult["data"] == null)
            {
                var reason = parseResult?["msg"]?.ToString();
                await NotifyAsync(ctx, "视频解析失败", string.IsNullOrEmpty(reason) ? "未知错误" : reason, ct);
                return;
            }

            var data = parseResult["data"];
            var title = Text(data, "title") is { Length: > 0 } t ? t : "未知标题";
            var coverUrl = Text(data, "cover_url");
            var videoLink = Text(data, "video_url");
            var musicUrl = Text(data, "music_url");
            var authorName = (string?)data["author"]?["name"] is { Length: > 0 } name ? name : "未知作者";
            var images = data["images"] as JArray ?? new JArray();

            // 拆条推送（2026-09-18 二次调整）：一起推的是视频/图片内容，不再推背景音乐音频气泡。
            // ① 有视频直链 → 单条视频气泡（封面作播放预览，配文=标题/作者）；
            // ② 无视频直链（图文作品）→ 逐张图片气泡，首张带配文；live_photo_url 非空的实况照片按视频气泡推、图片作预览；
            // 两者皆无才退回「未解析到视频直链」文本；音乐直链只留日志与 NAS 存档。
            var message = $"标题：{title}\r\n作者：{authorName}\r\n视频：{videoLink}\r\n封面：{coverUrl}" +
                          (musicUrl.Length > 0 ? $"\r\n音乐：{musicUrl}" : "") +
                          $"\r\n图片：{images.Count} 张";
            ctx.Log(message);

            var caption = $"标题：{title}\r\n作者：{authorName}";
            if (videoLink.Length > 0)
            {
                await NotifyVideoAsync(ctx, videoLink, caption, coverUrl.Length > 0 ? coverUrl : null, ct);
            }
            else if (images.Count > 0)
            {
                var first = true;
                foreach (var item in images)
                {
                    if (item is not JObject image) continue;
                    var imageUrl = Text(image, "url");
                    if (imageUrl.Length == 0) continue;
                    var livePhotoUrl = Text(image, "live_photo_url");
                    if (livePhotoUrl.Length > 0)
                    {
                        await NotifyVideoAsync(ctx, livePhotoUrl, first ? caption : null, imageUrl, ct);
                    }
                    else
                    {
                        await NotifyImageAsync(ctx, imageUrl, first ? caption : null, ct);
                    }
                    first = false;
                }
            }
            else if (coverUrl.Length > 0)
            {
                await NotifyImageAsync(ctx, coverUrl, caption, ct);
            }
            else
            {
                await NotifyAsync(ctx, "视频解析成功", "未解析到视频直链", ct);
            }

            var nasData = new JObject
            {
                ["video_url"] = Text(data, "video_url"),
                ["cover_url"] = coverUrl,
                ["title"] = Text(data, "title"),
                ["music_url"] = musicUrl,
                ["images"] = images,
                ["author"] = data["author"] as JObject ?? new JObject(),
                ["proxy_url"] = Text(data, "proxy_url")
            };
            await SaveToNasAsync(ctx, serviceUrl, cookie, nasData, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // ForceEndTime 到期的协作取消：正常退出，不当异常推通知
        }
        catch (Exception e)
        {
            ctx.Log("脚本异常：" + e);
            await NotifyAsync(ctx, "视频解析异常", "视频解析脚本执行异常", ct);
        }
    }

    /// <summary>登录取 cookies：只读 Set-Cookie 响应头（响应体不消费，与旧脚本一致）。</summary>
    private static async Task<string[]> LoginAsync(QuantumTaskContext ctx, string serviceUrl, string username, string password, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Post, serviceUrl + "/api/login", serviceUrl, serviceUrl + "/login", null,
            JsonConvert.SerializeObject(new { username, password, remember = true }));
        using var response = await ctx.Http.SendAsync(request, ct);
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        ctx.Log($"登录完成：HTTP {(int)response.StatusCode}，收到 {setCookies.Length} 条 Cookie。");
        return setCookies;
    }

    /// <summary>解析分享链接：GET /video/share/url/parse?url=编码后的链接，响应体 JSON。</summary>
    private static async Task<JObject> ParseVideoAsync(QuantumTaskContext ctx, string serviceUrl, string videoUrl, string cookie, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Get,
            serviceUrl + "/video/share/url/parse?url=" + Uri.EscapeDataString(videoUrl), serviceUrl, serviceUrl + "/", cookie, null);
        using var response = await ctx.Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        ctx.Log("解析响应: " + body);
        return string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body);
    }

    /// <summary>转存 NAS：POST /api/save-to-nas（JSON + Cookie）。</summary>
    private static async Task SaveToNasAsync(QuantumTaskContext ctx, string serviceUrl, string cookie, JObject nasData, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Post, serviceUrl + "/api/save-to-nas", serviceUrl, serviceUrl + "/", cookie,
            nasData.ToString(Formatting.None));
        using var response = await ctx.Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        ctx.Log("保存NAS响应: " + body);
        if (!response.IsSuccessStatusCode)
        {
            await NotifyAsync(ctx, "NAS转存失败", $"HTTP {(int)response.StatusCode}", ct);
            return;
        }
        try
        {
            var result = string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body);
            if (result?["code"] != null && result.Value<int?>("code") != 200)
            {
                var msg = result["msg"]?.ToString();
                await NotifyAsync(ctx, "NAS转存失败", string.IsNullOrEmpty(msg) ? $"code={result.Value<int?>("code")}" : msg, ct);
            }
        }
        catch (Exception e)
        {
            // 响应体不是 JSON（如纯文本网关页）：只留日志，不算失败
            ctx.Log("保存NAS响应非 JSON，按成功处理：" + e.Message);
        }
    }

    /// <summary>公共请求头：Accept/Accept-Language/Origin/Referer/UA（+ 可选 Cookie / JSON 体）。</summary>
    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string serviceUrl, string referer, string cookie, string jsonBody)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        request.Headers.Add("Origin", serviceUrl);
        request.Headers.Referrer = new Uri(referer);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.Add("Cookie", cookie);
        }
        if (jsonBody != null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        return request;
    }

    /// <summary>每条 Set-Cookie 取第一段（不含属性），分号连接，与旧 buildCookie 一致。</summary>
    private static string BuildCookie(IEnumerable<string> setCookies)
    {
        var cookies = new List<string>();
        foreach (var cookie in setCookies)
        {
            var first = cookie.Split(';')[0];
            if (!string.IsNullOrEmpty(first))
            {
                cookies.Add(first);
            }
        }
        return string.Join("; ", cookies);
    }

    /// <summary>取字符串字段，缺省空串（旧脚本 x || '' 语义）。</summary>
    private static string Text(JToken data, string key) => (string?)data[key] ?? string.Empty;

    /// <summary>多变量取值：命中第一个非空变量。</summary>
    private static string Var(QuantumTaskContext ctx, string name)
        => ctx.Variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>推送开关关闭时只落日志。</summary>
    private static async Task NotifyAsync(QuantumTaskContext ctx, string title, string content, CancellationToken ct)
    {
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendAsync(title, content, ct);
        }
    }

    /// <summary>封面图片气泡（图+配文同屏）；推送开关关闭时只落日志。</summary>
    private static async Task NotifyImageAsync(QuantumTaskContext ctx, string imageUrl, string caption, CancellationToken ct)
    {
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendImageAsync(imageUrl, caption, ct: ct);
        }
    }

    /// <summary>视频气泡（端内播放，posterUrl 作封面预览）；推送开关关闭时只落日志。</summary>
    private static async Task NotifyVideoAsync(QuantumTaskContext ctx, string videoUrl, string caption,
        string posterUrl, CancellationToken ct)
    {
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendVideoAsync(videoUrl, caption, posterUrl, ct);
        }
    }
}
