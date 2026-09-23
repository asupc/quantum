// ============================================================================
// B站任务（2026-09-18 新增）：每日任务（观看+分享）、漫画签到、银瓜子兑硬币、直播签到（官方
// 已下线，容错保留）、大会员大积分（非大会员自动跳过）；投币默认关闭（消耗真硬币）。
// 接口序列参考 RayWangQvQ/BiliBiliToolPro（C#，GPL-3.0，个人自用的等价实现）。
// 2026-09-20 扩展：天选时刻抽奖（**默认关**：家庭宽带出口 IP 被 xlive 风控 -352，冷却期先屏蔽；
// 恢复方式=BILI_TASKS 加 tianxuan，或风控冷却后把 tianxuan 加回下行默认集）——扫全站直播间挂件
// 504 的天选房自动参与，对齐 BiliBiliToolPro LiveDomainService.TianXuan（含 Wbi 签名与直播域
// Cookie 预热）；「需关注」奖项由服务端自动关注主播（不做分组整理）；遇 -352 当日中止不重试。
// 环境变量：
//   BILI_COOKIE       必填。浏览器登录 bilibili.com → F12 → Network → 任一请求 → 复制整串 Cookie
//                     （须含 DedeUserID / SESSDATA / bili_jct；buvid3 缺失时脚本自动补）
//   BILI_TASKS        可选。逗号分隔子集，默认 watch,share,manga,silver2coin,live,bigpoint
//                     （tianxuan 需显式加入才执行）
//   BILI_COIN_TARGET  可选。每日投币目标枚数（默认 0 = 不投币；投币消耗真硬币）
//   BILI_COIN_KEEP    可选。投币后保留的硬币数（默认 5）
//   BILI_TIANXUAN_PAGES 可选。天选每分区扫描页数（默认 2，上限 5）
//   BILI_TIANXUAN_MAX   可选。天选单次参与房间上限（默认 20）
//   BILI_TIANXUAN_EXCLUDE 可选。天选奖品名排除词，逗号分隔（默认空）
// 实现说明：
//   1) 任务类接口（heartbeat/share/coin/manga/live/vip_point）均不校验 Wbi 签名，故未实现 Wbi；
//      候选视频取自匿名排行榜接口（无需登录态）。
//   2) 请求间随机延迟 1.5~3s、任务阶段间 5~15s（防匀速特征）；建议 cron 配 08:15 错峰。
//   3) Cookie 失效（nav 未登录）→ 推送红色提醒后终止；SESSDATA 中的逗号发送前转义为 %2C。
//   4) 大会员大积分走 App 端接口（App UA + build/statistics 公共参数）；遇 -352 风控码当日
//      中止并推送提醒，不做重试。
//   5) 天选扫描：直播域 Cookie 预热（live 首页接口种 buvid，缺失会 -352）→ getWebAreaList 分区
//      （真实响应 data.data 双层嵌套）→ second/getList 翻页找 pendant_info["2"].pendent_id==504 →
//      Anchor/Check 过滤（已开奖/需送礼/需勋章跳过）→ Anchor/Join；getList 匿名请求会 -352，
//      扫描必须带登录态。
// ============================================================================
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class BiliDailyTask : IQuantumTask
{
    private const string Api = "https://api.bilibili.com";
    private const string RefererHome = "https://www.bilibili.com/";
    private const string BigPointReferer = "https://big.bilibili.com/mobile/bigPoint/task";
    private const string WebUa =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_3) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/87.0.4280.66 Safari/537.36 Edg/87.0.664.41";
    private const string AppUa =
        "Mozilla/5.0 (Linux; Android 12; SM-S9080 Build/V417IR; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/103.0.5060.129 Mobile Safari/537.36 BiliApp/7760700 mobi_app/android channel/bili";
    private const string Statistics = "{\"appId\":1,\"platform\":3,\"version\":\"8.45.1\",\"abtest\":\"\"}";
    private const string CoinRefererSuffix = "?spm_id_from=333.1007.tianma.1-1-1.click&vd_source=80c1601a7003934e7a90709c18dfcffd";

    // 投币换视频可继续尝试的响应码（对齐 RayPro Constants.DonateCoinCanContinueStatusDic；0 计成功单独判）
    private static readonly HashSet<int> CoinRetryCodes = [-400, 10003, 34002, 34003, 34004, 34005];

    // 大积分里需购买才能完成的任务码，直接跳过不计数
    private static readonly HashSet<string> BigPointBuyCodes = ["vipmallbuy", "tvodbuy", "dressbuyamount"];

    private string _cookie = "";
    private string _csrf = "";
    private string _mid = "";
    private string _buvid = "";
    private readonly List<(string Label, string State, string Detail)> _report = [];

    private sealed record VideoInfo(string Aid, string Bvid, string Cid, long Duration, string Title);

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // ------------------------------------------------------------------ 1. 凭据
        if (!ctx.Variables.TryGetValue("BILI_COOKIE", out var rawCookie) || string.IsNullOrWhiteSpace(rawCookie))
        {
            await NotifyAsync(ctx, $"{QuantumText.Tag("red", "未配置凭据")} 环境变量 BILI_COOKIE 缺失：浏览器登录 bilibili.com 后 F12 复制整串 Cookie 配到环境变量并启用", ct);
            return;
        }
        _cookie = NormalizeCookie(rawCookie);
        _csrf = GetCookieValue(_cookie, "bili_jct");
        _mid = GetCookieValue(_cookie, "DedeUserID");
        if (string.IsNullOrEmpty(_csrf) || string.IsNullOrEmpty(_mid))
        {
            await NotifyAsync(ctx, $"{QuantumText.Tag("red", "凭据不完整")} Cookie 缺少 bili_jct / DedeUserID 字段，请重新复制完整 Cookie", ct);
            return;
        }
        var tasks = ParseTasks(ctx.Variables.TryGetValue("BILI_TASKS", out var taskConf) ? taskConf : null,
            ["watch", "share", "manga", "silver2coin", "live", "bigpoint"]);
        var coinTarget = ParseInt(ctx.Variables.TryGetValue("BILI_COIN_TARGET", out var ct2) ? ct2 : null, 0);
        var coinKeep = ParseInt(ctx.Variables.TryGetValue("BILI_COIN_KEEP", out var ck) ? ck : null, 5);

        // ------------------------------------------------------------------ 2. buvid3 自动补全（分享/投币缺失会概率性 -403）
        await TryFillBuvidAsync(ctx, ct);

        // ------------------------------------------------------------------ 3. 登录校验
        var nav = await GetJsonAsync(ctx, Api + "/x/web-interface/nav", ct, referer: RefererHome);
        if (nav?["code"]?.Value<int>() != 0 || nav["data"]?["isLogin"]?.Value<bool>() != true)
        {
            await NotifyAsync(ctx, $"{QuantumText.Tag("red", "Cookie 已失效")} 登录校验未通过（code={nav?["code"]}），请重新抓取 BILI_COOKIE", ct);
            return;
        }
        var level = nav["data"]?["level_info"]?["current_level"];
        var coinBalance = nav["data"]?["money"]?.ToString();
        var isVip = nav["data"]?["vipStatus"]?.Value<int>() == 1 && nav["data"]?["vipType"]?.Value<int>() != 0;

        // ------------------------------------------------------------------ 4. 每日任务（观看+分享）
        if (tasks.Contains("watch") || tasks.Contains("share"))
            await WatchShareAsync(ctx, ct, tasks);

        // ------------------------------------------------------------------ 5. 漫画签到
        if (tasks.Contains("manga"))
            await MangaCheckInAsync(ctx, ct);

        // ------------------------------------------------------------------ 6. 银瓜子兑硬币
        if (tasks.Contains("silver2coin"))
            await Silver2CoinAsync(ctx, ct);

        // ------------------------------------------------------------------ 7. 直播签到（官方已下线，容错保留）
        if (tasks.Contains("live"))
            await LiveSignAsync(ctx, ct);

        // ------------------------------------------------------------------ 8. 大会员大积分
        if (tasks.Contains("bigpoint"))
        {
            if (isVip) await BigPointAsync(ctx, ct);
            else _report.Add(("大会员大积分", "skip", "非大会员账号"));
        }

        // ------------------------------------------------------------------ 9. 天选时刻抽奖（默认开）
        if (tasks.Contains("tianxuan"))
            await TianXuanAsync(ctx, ct);

        // ------------------------------------------------------------------ 10. 投币（默认关，BILI_COIN_TARGET>0 才执行）
        if (coinTarget > 0)
            await DonateCoinAsync(ctx, ct, coinTarget, coinKeep);

        // ------------------------------------------------------------------ 11. 汇总推送
        var sb = new StringBuilder($"B站每日任务 {DateTime.Now:MM-dd HH:mm}（LV{level} 硬币{coinBalance}）");
        foreach (var (label, state, detail) in _report)
        {
            var tag = state switch
            {
                "ok" => QuantumText.Tag("green", "✓"),
                "skip" => QuantumText.Tag("orange", "跳过"),
                _ => QuantumText.Tag("red", "失败")
            };
            sb.Append('\n').Append(tag).Append(' ').Append(label);
            if (!string.IsNullOrEmpty(detail)) sb.Append(' ').Append(detail);
        }
        await NotifyAsync(ctx, sb.ToString(), ct);
        ctx.Log("B站任务执行完毕。");
    }

    // ================================ 每日任务：观看 + 分享 ================================

    private async Task WatchShareAsync(QuantumTaskContext ctx, CancellationToken ct, HashSet<string> tasks)
    {
        var wantWatch = tasks.Contains("watch");
        var wantShare = tasks.Contains("share");
        try
        {
            var reward = await GetJsonAsync(ctx, Api + "/x/member/web/exp/reward", ct,
                referer: "https://account.bilibili.com/account/home", origin: "https://account.bilibili.com");
            var watched = reward?["data"]?["watch"]?.Value<bool>() == true;
            var shared = reward?["data"]?["share"]?.Value<bool>() == true;

            if (wantWatch && watched && (!wantShare || shared))
            {
                _report.Add(("每日任务", "skip", wantShare ? "观看/分享均已完成" : "观看任务已完成"));
                return;
            }

            var video = await PickVideoAsync(ctx, ct);
            if (video == null)
            {
                _report.Add(("每日任务", "fail", "获取候选视频失败"));
                return;
            }

            var watchOk = watched;
            var shareOk = shared;
            if (!watched || !shared)
            {
                // 分享前也须先上报一次「打开」心跳
                var opened = await HeartbeatAsync(ctx, video, 0, ct);
                if (!watched)
                {
                    var cap = video.Duration > 0 ? (int)Math.Min(video.Duration, 15) : 15;
                    var played = await HeartbeatAsync(ctx, video, Random.Shared.Next(1, cap + 1), ct);
                    watchOk = opened && played;
                }
                if (!shared)
                {
                    var share = await PostFormAsync(ctx, Api + "/x/web-interface/share/add", new Dictionary<string, string>
                    {
                        ["aid"] = video.Aid,
                        ["csrf"] = _csrf,
                        ["eab_x"] = "1",
                        ["ramval"] = Random.Shared.Next(3, 21).ToString(),
                        ["source"] = "web_normal",
                        ["ga"] = "1"
                    }, ct, origin: "https://www.bilibili.com", referer: $"https://www.bilibili.com/video/{video.Bvid}/");
                    var code = share?["code"]?.Value<int>() ?? int.MinValue;
                    // 71000 = 今日已分享
                    shareOk = code == 0 || code == 71000;
                }
            }

            var parts = new List<string>();
            if (wantWatch) parts.Add(watchOk ? "观看+5经验" : "观看未成功");
            if (wantShare) parts.Add(shareOk ? "分享+5经验" : "分享未成功");
            var allOk = (!wantWatch || watchOk) && (!wantShare || shareOk);
            _report.Add(("每日任务", allOk ? "ok" : "fail", $"{string.Join("，", parts)}（{video.Title}）"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"每日任务异常：{e.Message}");
            _report.Add(("每日任务", "fail", e.Message));
        }
    }

    private async Task<VideoInfo?> PickVideoAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var rank = await GetJsonAsync(ctx, Api + "/x/web-interface/ranking/v2?rid=0&type=all", ct,
            referer: RefererHome, origin: "https://www.bilibili.com");
        if (rank?["data"]?["list"] is not JArray list || list.Count == 0) return null;
        var pick = (JObject)list[Random.Shared.Next(list.Count)];
        var aid = pick["aid"]?.ToString();
        if (string.IsNullOrEmpty(aid)) return null;
        var bvid = pick["bvid"]?.ToString() ?? "";

        var view = await GetJsonAsync(ctx, Api + "/x/web-interface/view?aid=" + aid, ct, referer: RefererHome);
        var cid = view?["data"]?["cid"]?.ToString();
        var duration = view?["data"]?["duration"]?.Value<long>() ?? 0;
        var title = Truncate(view?["data"]?["title"]?.ToString() ?? pick["title"]?.ToString() ?? bvid, 18);
        return new VideoInfo(aid, bvid, string.IsNullOrEmpty(cid) ? "0" : cid!, duration, title);
    }

    private async Task<bool> HeartbeatAsync(QuantumTaskContext ctx, VideoInfo video, int playedTime, CancellationToken ct)
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        var resp = await PostFormAsync(ctx,
            Api + $"/x/click-interface/web/heartbeat?aid={video.Aid}&played_time={playedTime}",
            new Dictionary<string, string>
            {
                ["aid"] = video.Aid,
                ["bvid"] = video.Bvid,
                ["cid"] = video.Cid,
                ["mid"] = _mid,
                ["csrf"] = _csrf,
                ["played_time"] = playedTime.ToString(),
                ["realtime"] = playedTime.ToString(),
                ["real_played_time"] = playedTime.ToString(),
                ["start_ts"] = now,
                ["type"] = "3",
                ["dt"] = "2",
                ["play_type"] = "3"
            }, ct, origin: "https://www.bilibili.com", referer: $"https://www.bilibili.com/video/{video.Bvid}/");
        return resp?["code"]?.Value<int>() == 0;
    }

    // ================================ 漫画签到 ================================

    private async Task MangaCheckInAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Random.Shared.Next(1500, 3000), ct);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://manga.bilibili.com/twirp/activity.v1.Activity/ClockIn?platform=android")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            ApplyHeaders(request, origin: "https://manga.bilibili.com");
            using var response = await ctx.Http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var json = TryParse(text);
            var code = json?["code"]?.ToString();
            var msg = json?["msg"]?.ToString() ?? "";
            if (code == "0")
                _report.Add(("漫画签到", "ok", ""));
            else if (msg.Contains("重复", StringComparison.Ordinal) || msg.Contains("已签", StringComparison.Ordinal) ||
                     ((int)response.StatusCode == 400 && code == "invalid_argument" &&
                      msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase)))
                // 已签形态一：code=1「不能重复签到~」；形态二：HTTP400+invalid_argument+duplicate
                _report.Add(("漫画签到", "skip", "今日已签到"));
            else
                _report.Add(("漫画签到", "fail", $"code={code} {msg}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _report.Add(("漫画签到", "fail", e.Message));
        }
    }

    // ================================ 银瓜子兑硬币 ================================

    private async Task Silver2CoinAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            var wallet = await GetJsonAsync(ctx, "https://api.live.bilibili.com/xlive/revenue/v1/wallet/getStatus", ct,
                origin: "https://link.bilibili.com");
            var left = wallet?["data"]?["silver_2_coin_left"]?.Value<int>() ?? 0;
            var silver = wallet?["data"]?["silver"]?.Value<long>() ?? 0;
            if (left <= 0)
            {
                _report.Add(("银瓜子兑换", "skip", $"今日次数已用完（银瓜子余 {silver}）"));
                return;
            }
            if (silver < 100)
            {
                // 兑换 1 硬币需 100 银瓜子，余额不足时接口会报 code=403，提前跳过更直观
                _report.Add(("银瓜子兑换", "skip", $"银瓜子不足 100（余 {silver}）"));
                return;
            }
            var resp = await PostFormAsync(ctx, "https://api.live.bilibili.com/xlive/revenue/v1/wallet/silver2coin",
                new Dictionary<string, string>
                {
                    ["csrf"] = _csrf,
                    ["csrf_token"] = _csrf,
                    ["visit_id"] = BuildVisitId()
                }, ct, origin: "https://link.bilibili.com");
            if (resp?["code"]?.Value<int>() == 0)
                _report.Add(("银瓜子兑换", "ok", $"硬币+1（现余 {resp["data"]?["coin"]}，银瓜子余 {resp["data"]?["silver"]}）"));
            else
                _report.Add(("银瓜子兑换", "fail", $"code={resp?["code"]} {resp?["message"]}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _report.Add(("银瓜子兑换", "fail", e.Message));
        }
    }

    // ================================ 直播签到（已下线，容错） ================================

    private async Task LiveSignAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            var resp = await GetJsonAsync(ctx, "https://api.live.bilibili.com/xlive/web-ucenter/v1/sign/DoSign", ct,
                referer: "https://link.bilibili.com/", origin: "https://link.bilibili.com");
            if (resp?["code"]?.Value<int>() == 0)
                _report.Add(("直播签到", "ok", $"{resp["data"]?["text"]} {resp["data"]?["specialText"]}"));
            else
                // 官方已下线（message: 签到活动已下线，无法使用。），按跳过处理不算失败
                _report.Add(("直播签到", "skip", resp?["message"]?.ToString() ?? "接口不可用"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _report.Add(("直播签到", "skip", e.Message));
        }
    }

    // ================================ 大会员大积分 ================================

    private async Task BigPointAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            // 1. 签到状态（combine 里的 sign_task_item 结果不可信，用独立接口）
            var signStatus = await GetJsonAsync(ctx, Api + "/x/vip/vip_center/sign_in/three_days_sign", ct,
                query: BaseAppParams(new Dictionary<string, string> { ["csrf"] = _csrf }),
                ua: AppUa, referer: BigPointReferer);
            var signed = signStatus?["data"]?["three_day_sign"]?["signed"]?.Value<bool>() == true;
            string signDetail;
            if (signed)
            {
                signDetail = "已签";
            }
            else
            {
                var ms = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var sign = await PostJsonAsync(ctx,
                    Api + $"/pgc/activity/score/task/sign2?mobi_app=android&csrf={Uri.EscapeDataString(_csrf)}&platform=android",
                    new JObject { ["device"] = "phone", ["t"] = ms, ["ts"] = ms / 1000 },
                    ct, ua: AppUa, referer: "https://big.bilibili.com/mobile/index",
                    query: BaseAppParams([]));
                signDetail = sign?["code"]?.Value<int>() == 0
                    ? $"积分+{sign["data"]?["score"] ?? sign["data"]?["vipScore"]}"
                    : $"签到失败 code={sign?["code"]}";
            }

            await Task.Delay(Random.Shared.Next(3000, 6000), ct);

            // 2. 任务列表
            var (targets, _) = await GetCombineAsync(ctx, ct);
            if (targets == null)
            {
                _report.Add(("大会员大积分", "fail", "任务列表获取失败"));
                return;
            }

            // 3. 领取（state==0）→ 完成 → 复查
            var done = 0;
            foreach (var item in targets)
            {
                ct.ThrowIfCancellationRequested();
                var code = item.Code;
                if (BigPointBuyCodes.Contains(code)) continue;
                if (item.State == 0)
                {
                    var received = await PostFormAsync(ctx, Api + "/pgc/activity/score/task/receive/v2",
                        new Dictionary<string, string> { ["taskCode"] = code, ["csrf"] = _csrf },
                        ct, ua: AppUa, referer: BigPointReferer, query: BaseAppParams([]));
                    if (received?["code"]?.Value<int>() != 0)
                    {
                        ctx.Log($"大积分任务 {code} 领取失败 code={received?["code"]}");
                        continue;
                    }
                    await Task.Delay(Random.Shared.Next(1500, 3000), ct);
                }
                if (item.State == 3) { done++; continue; }
                if (await CompleteBigPointTaskAsync(ctx, code, ct)) done++;
            }

            // 4. 复查完成数与积分
            var (after, point) = await GetCombineAsync(ctx, ct);
            var finalDone = after?.Count(x => x.State == 3) ?? done;
            _report.Add(("大会员大积分", finalDone >= (after?.Count ?? 0) ? "ok" : "fail",
                $"{signDetail}，任务 {finalDone}/{after?.Count ?? 0}{(string.IsNullOrEmpty(point) ? "" : $"，积分 {point}")}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"大积分异常：{e.Message}");
            _report.Add(("大会员大积分", "fail", e.Message));
        }
    }

    private async Task<(List<(string Code, int State)>? Targets, string? Point)> GetCombineAsync(
        QuantumTaskContext ctx, CancellationToken ct)
    {
        var combine = await GetJsonAsync(ctx, Api + "/x/vip_point/task/combine", ct,
            query: BaseAppParams(new Dictionary<string, string>
            {
                ["csrf"] = _csrf,
                ["buvid"] = _buvid,
                ["brand"] = "Samsung",
                ["channel"] = "bili",
                ["containerName"] = "AbstractWebActivity",
                ["device"] = "phone"
            }),
            ua: AppUa, referer: BigPointReferer);
        if (combine?["data"]?["task_info"]?["modules"] is not JArray modules)
            return (null, combine?["data"]?["point_info"]?["point"]?.ToString());
        var result = new List<(string, int)>();
        foreach (var module in modules)
        {
            if (module["common_task_item"] is not JArray items) continue;
            foreach (var item in items)
            {
                var code = item["task_code"]?.ToString();
                if (string.IsNullOrEmpty(code) || BigPointBuyCodes.Contains(code)) continue;
                result.Add((code!, item["state"]?.Value<int>() ?? 0));
            }
        }
        return (result, combine["data"]?["point_info"]?["point"]?.ToString());
    }

    private async Task<bool> CompleteBigPointTaskAsync(QuantumTaskContext ctx, string code, CancellationToken ct)
    {
        JObject? resp;
        switch (code)
        {
            case "bonus":
            case "privilege":
                resp = await PostFormAsync(ctx, Api + "/pgc/activity/score/task/complete",
                    new Dictionary<string, string> { ["taskCode"] = code, ["csrf"] = _csrf },
                    ct, ua: AppUa, referer: BigPointReferer, query: BaseAppParams([]));
                break;
            case "dress-view":
            case "ogvwatchnew":
                resp = await PostFormAsync(ctx, Api + "/pgc/activity/score/task/complete/v2",
                    new Dictionary<string, string> { ["taskCode"] = code, ["csrf"] = _csrf },
                    ct, ua: AppUa, referer: BigPointReferer, query: BaseAppParams([]));
                break;
            case "animatetab":
            case "filmtab":
                // 浏览频道任务：完成前须停留 10s
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                resp = await PostFormAsync(ctx, Api + "/pgc/activity/deliver/task/complete",
                    BaseAppParams(new Dictionary<string, string>
                    {
                        ["position"] = code == "animatetab" ? "jp_channel" : "tv_channel",
                        ["c_locale"] = "zh_CN",
                        ["channel"] = "bili",
                        ["s_locale"] = "zh_CN"
                    }),
                    ct, ua: AppUa, referer: BigPointReferer);
                break;
            case "vipmallview":
                resp = await PostJsonAsync(ctx, "https://show.bilibili.com/api/activity/fire/common/event/dispatch",
                    new JObject { ["Csrf"] = _csrf, ["EventId"] = "hevent_oy4b7h3epeb" },
                    ct, ua: AppUa, referer: BigPointReferer);
                break;
            default:
                ctx.Log($"大积分任务 {code} 无对应完成方式，跳过");
                return false;
        }
        var ok = resp?["code"]?.Value<int>() == 0;
        if (!ok) ctx.Log($"大积分任务 {code} 完成失败 code={resp?["code"]} {resp?["message"]}");
        return ok;
    }

    // ================================ 天选时刻抽奖 ================================

    private const string LiveApi = "https://api.live.bilibili.com";

    // Wbi 签名（对齐 BiliBiliToolPro WbiService：nav 取密钥 + mixin 表打乱 + 参数排序 MD5）
    private string? _wbiImgKey;
    private string? _wbiSubKey;
    private static readonly int[] WbiMixinTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52
    ];

    /// <summary>取 Wbi 密钥（nav 的 wbi_img.img_url/sub_url 取文件名去扩展名；每执行内缓存）。</summary>
    private async Task<(string Img, string Sub)?> GetWbiKeysAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        if (_wbiImgKey != null && _wbiSubKey != null) return (_wbiImgKey, _wbiSubKey);
        var nav = await GetJsonAsync(ctx, Api + "/x/web-interface/nav", ct, referer: RefererHome);
        var imgUrl = nav?["data"]?["wbi_img"]?["img_url"]?.ToString();
        var subUrl = nav?["data"]?["wbi_img"]?["sub_url"]?.ToString();
        if (string.IsNullOrEmpty(imgUrl) || string.IsNullOrEmpty(subUrl)) return null;
        _wbiImgKey = imgUrl[(imgUrl.LastIndexOf('/') + 1)..].Split('.')[0];
        _wbiSubKey = subUrl[(subUrl.LastIndexOf('/') + 1)..].Split('.')[0];
        return (_wbiImgKey, _wbiSubKey);
    }

    /// <summary>Wbi 签名：返回 (wts, w_rid)；值先滤 [!'()*] 再转义，含 wts 按键排序后拼 mixinKey 做 MD5 小写。</summary>
    private static (long Wts, string WRid) SignWbi(Dictionary<string, string> parameters, string imgKey, string subKey)
    {
        var orig = imgKey + subKey;
        var keyBuilder = new StringBuilder();
        foreach (var idx in WbiMixinTab) keyBuilder.Append(orig[idx]);
        var mixinKey = keyBuilder.ToString()[..32];

        var wts = DateTimeOffset.Now.ToUnixTimeSeconds();
        var dic = new Dictionary<string, string> { ["wts"] = wts.ToString() };
        foreach (var kv in parameters)
        {
            var value = System.Text.RegularExpressions.Regex.Replace(kv.Value, "[!'()*]", "");
            dic[Uri.EscapeDataString(kv.Key)] = Uri.EscapeDataString(value);
        }
        var queryString = string.Join("&", dic.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}={dic[k]}"));
        var hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(queryString + mixinKey))).ToLowerInvariant();
        return (wts, hash);
    }

    /// <summary>
    /// 直播域 Cookie 预热：请求直播首页接口种 buvid3/buvid4/LIVE_BUVID 等（对齐 BiliBiliToolPro
    /// CheckLiveCookie——GetLiveHome 后合并 Set-Cookie；直播域 Cookie 缺失时 second/getList 会 -352）。
    /// </summary>
    private async Task<string> PrepareLiveCookieAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Random.Shared.Next(1500, 3000), ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, LiveApi + "/news/v1/notice/recom?product=live");
            ApplyHeaders(request, referer: "https://live.bilibili.com/");
            using var response = await ctx.Http.SendAsync(request, ct);
            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                ctx.Log("直播域预热：响应未带 Set-Cookie，沿用现有 Cookie");
                return _cookie;
            }
            var merged = new Dictionary<string, string>();
            foreach (var part in _cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0 && !merged.ContainsKey(part[..eq])) merged[part[..eq]] = part[(eq + 1)..];
            }
            var seeded = 0;
            foreach (var sc in setCookies)
            {
                var pair = sc.Split(';')[0].Trim();
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                merged[pair[..eq]] = pair[(eq + 1)..];
                seeded++;
            }
            ctx.Log($"直播域 Cookie 预热完成（种入/刷新 {seeded} 项）");
            return string.Join("; ", merged.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"直播域 Cookie 预热失败（沿用现有 Cookie 继续）：{e.Message}");
            return _cookie;
        }
    }

    /// <summary>
    /// 扫全站直播间找天选时刻房（挂件 504）自动参与。对齐 BiliBiliToolPro LiveDomainService.TianXuan：
    /// 直播域 Cookie 预热（CheckLiveCookie 等价）→ 分区列表 → second/getList 翻页找房 →
    /// Anchor/Check 过滤（已开奖/需送礼/需粉丝勋章/舰长跳过）→ Anchor/Join；
    /// 「需关注」奖项由服务端自动关注；任一环节 -352 即当日中止。
    /// </summary>
    private async Task TianXuanAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var pages = Math.Clamp(ParseInt(ctx.Variables.TryGetValue("BILI_TIANXUAN_PAGES", out var p) ? p : null, 2), 1, 5);
        var maxJoin = Math.Clamp(ParseInt(ctx.Variables.TryGetValue("BILI_TIANXUAN_MAX", out var m) ? m : null, 20), 1, 100);
        var exclude = ParseList(ctx.Variables.TryGetValue("BILI_TIANXUAN_EXCLUDE", out var ex) ? ex : null);

        try
        {
            // 0. 直播域 Cookie 预热：种 buvid3/buvid4/LIVE_BUVID 等（缺失时 getList 会 -352，对齐上游 CheckLiveCookie）
            var liveCookie = await PrepareLiveCookieAsync(ctx, ct);
            // getList 还须 Wbi 签名（上游 GetListRequest 走 WbiService 签名，缺签名同样 -352）
            var wbiKeys = await GetWbiKeysAsync(ctx, ct);
            if (wbiKeys == null) ctx.Log("Wbi 密钥未取到（getList 不签名，可能 -352）");

            // 1. 分区列表（真实响应为 data.data 双层嵌套，本机匿名实测确认）
            var areaResp = await GetJsonAsync(ctx, LiveApi + "/xlive/web-interface/v1/index/getWebAreaList?source_id=2", ct,
                referer: "https://live.bilibili.com/", origin: "https://live.bilibili.com", cookie: liveCookie);
            if (areaResp?["code"]?.Value<int>() == -352)
            {
                _report.Add(("天选时刻", "fail", "分区列表触发风控（-352），今日中止"));
                return;
            }
            if (areaResp?["data"]?["data"] is not JArray areas || areas.Count == 0)
            {
                _report.Add(("天选时刻", "fail", $"分区列表获取失败 code={areaResp?["code"]}"));
                return;
            }

            // 2. 逐分区翻页找天选房（pendant_info["2"].pendent_id == 504）
            var candidates = new List<(long Roomid, string Title)>();
            var seenRooms = new HashSet<long>();
            foreach (var area in areas)
            {
                ct.ThrowIfCancellationRequested();
                if (!long.TryParse(area["id"]?.ToString(), out var parentId)) continue;
                var areaName = area["name"]?.ToString() ?? "?";
                var sortType = "";
                for (var page = 1; page <= pages; page++)
                {
                    var listParams = new Dictionary<string, string>
                    {
                        ["platform"] = "web",
                        ["parent_area_id"] = parentId.ToString(),
                        ["area_id"] = "0",
                        ["sort_type"] = sortType,
                        ["page"] = page.ToString()
                    };
                    if (wbiKeys != null)
                    {
                        var (wts, wrid) = SignWbi(listParams, wbiKeys.Value.Img, wbiKeys.Value.Sub);
                        listParams["wts"] = wts.ToString();
                        listParams["w_rid"] = wrid;
                    }
                    var listResp = await GetJsonAsync(ctx, LiveApi + "/xlive/web-interface/v1/second/getList", ct,
                        referer: "https://live.bilibili.com/", origin: "https://live.bilibili.com", cookie: liveCookie,
                        query: listParams);
                    var code = listResp?["code"]?.Value<int>() ?? int.MinValue;
                    if (code == -352)
                    {
                        _report.Add(("天选时刻", "fail", $"扫描触发风控（-352，{areaName}），今日中止"));
                        return;
                    }
                    var data = listResp?["data"];
                    if (code != 0 || data?["list"] is not JArray list) break; // 单分区失败跳过，不终止全局
                    foreach (var item in list)
                    {
                        if (item["pendant_info"]?["2"]?["pendent_id"]?.Value<long>() != 504) continue;
                        var roomid = item["roomid"]?.Value<long>() ?? 0;
                        if (roomid <= 0 || !seenRooms.Add(roomid)) continue;
                        candidates.Add((roomid, item["title"]?.ToString() ?? roomid.ToString()));
                    }
                    if (data?["has_more"]?.Value<int>() != 1) break;
                    sortType = (data?["new_tags"] as JArray)?[0]?["sort_type"]?.ToString() ?? "";
                }
            }

            // 3. 逐房 Check → 过滤 → Join
            var joined = 0;
            var prizes = new List<string>();
            foreach (var (roomid, title) in candidates)
            {
                if (joined >= maxJoin) break;
                ct.ThrowIfCancellationRequested();
                var checkResp = await GetJsonAsync(ctx, LiveApi + $"/xlive/lottery-interface/v1/Anchor/Check?roomid={roomid}", ct,
                    referer: "https://live.bilibili.com/", origin: "https://live.bilibili.com", cookie: liveCookie);
                var ccode = checkResp?["code"]?.Value<int>() ?? int.MinValue;
                if (ccode == -352)
                {
                    _report.Add(("天选时刻", "fail", $"Check 触发风控（-352，房间 {roomid}），今日中止"));
                    return;
                }
                var check = ccode == 0 ? checkResp!["data"] : null;
                if (check == null || check["status"]?.Value<int>() != 1) continue; // 已开奖/数据异常
                var awardName = check["award_name"]?.ToString() ?? "";
                if (exclude.Any(k => awardName.Contains(k, StringComparison.Ordinal))) continue;
                if ((check["gift_price"]?.Value<int>() ?? 0) > 0) continue; // 需赠送礼物
                if (check["require_type"]?.Value<int>() is not (0 or 1)) continue; // 仅无条件/关注（跳过粉丝勋章、舰长）

                var join = await PostFormAsync(ctx, LiveApi + "/xlive/lottery-interface/v1/Anchor/Join",
                    new Dictionary<string, string>
                    {
                        ["id"] = check["id"]?.ToString() ?? "",
                        ["gift_id"] = check["gift_id"]?.ToString() ?? "0",
                        ["gift_num"] = check["gift_num"]?.ToString() ?? "0",
                        ["csrf"] = _csrf,
                        ["csrf_token"] = _csrf,
                        ["visit_id"] = BuildVisitId(),
                        ["platform"] = "pc"
                    }, ct, referer: "https://live.bilibili.com/", origin: "https://live.bilibili.com", cookie: liveCookie);
                var jcode = join?["code"]?.Value<int>() ?? int.MinValue;
                if (jcode == 0)
                {
                    joined++;
                    var prize = $"{awardName}×{check["award_num"]?.ToString() ?? "1"}";
                    if (!prizes.Contains(prize)) prizes.Add(prize);
                    ctx.Log($"天选参与成功：{Truncate(title, 18)}（{prize}）");
                }
                else if (jcode == -352)
                {
                    _report.Add(("天选时刻", "fail", "Join 触发风控（-352），今日中止"));
                    return;
                }
                else
                {
                    ctx.Log($"天选参与失败 code={jcode} {join?["message"]}（房间 {roomid}）");
                }
            }

            if (candidates.Count == 0)
                _report.Add(("天选时刻", "skip", "未发现天选房间"));
            else if (joined == 0)
                _report.Add(("天选时刻", "skip", $"发现 {candidates.Count} 房但无可参与（已开奖/需送礼/需勋章等）"));
            else
                _report.Add(("天选时刻", "ok",
                    $"参与 {joined}/{candidates.Count} 房，奖品：{string.Join("、", prizes.Take(5))}{(prizes.Count > 5 ? " 等" : "")}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"天选异常：{e.Message}");
            _report.Add(("天选时刻", "fail", e.Message));
        }
    }

    // ================================ 投币（默认关） ================================

    private async Task DonateCoinAsync(QuantumTaskContext ctx, CancellationToken ct, int target, int keep)
    {
        try
        {
            var exp = await GetJsonAsync(ctx, Api + "/x/web-interface/coin/today/exp", ct, referer: RefererHome);
            var already = (int)Math.Floor((exp?["data"]?.Value<double>() ?? 0) / 10);
            var need = target - already;
            if (need <= 0)
            {
                _report.Add(("投币", "skip", $"今日已投 {already} 枚（目标 {target}）"));
                return;
            }
            var coin = await GetJsonAsync(ctx, "https://account.bilibili.com/site/getCoin", ct,
                referer: "https://account.bilibili.com/account/coin");
            var balance = coin?["data"]?["money"]?.Value<int>() ?? 0;
            var usable = balance - keep;
            if (usable <= 0)
            {
                _report.Add(("投币", "skip", $"余额 {balance} 枚，保留 {keep} 枚"));
                return;
            }
            need = Math.Min(need, usable);

            var done = 0;
            var lastMsg = "";
            for (var attempt = 0; attempt < 10 && done < need; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var video = await PickVideoAsync(ctx, ct);
                if (video == null) break;
                var coins = await GetJsonAsync(ctx,
                    Api + $"/x/web-interface/archive/coins?aid={video.Aid}&jsonp=jsonp", ct,
                    referer: $"https://www.bilibili.com/video/{video.Bvid}/");
                if ((coins?["data"]?["multiply"]?.Value<int>() ?? 0) >= 2) continue; // 单视频上限，换一个

                var resp = await PostFormAsync(ctx, Api + "/x/web-interface/coin/add",
                    new Dictionary<string, string>
                    {
                        ["aid"] = video.Aid,
                        ["multiply"] = "1",
                        ["select_like"] = "0",
                        ["cross_domain"] = "true",
                        ["csrf"] = _csrf,
                        ["eab_x"] = "2",
                        ["ramval"] = "3",
                        ["source"] = "web_normal",
                        ["ga"] = "1"
                    }, ct, referer: $"https://www.bilibili.com/video/{video.Bvid}/{CoinRefererSuffix}");
                var code = resp?["code"]?.Value<int>() ?? int.MinValue;
                lastMsg = $"code={code} {resp?["message"]}";
                if (code == 0)
                {
                    done++;
                    await Task.Delay(Random.Shared.Next(3000, 8000), ct);
                }
                else if (CoinRetryCodes.Contains(code))
                {
                    continue; // 该视频不可投，换下一个
                }
                else
                {
                    break; // -101/-111 等，终止投币段
                }
            }
            _report.Add(done > 0
                ? ("投币", "ok", $"已投 {done}/{need} 枚（余 {balance - done}）")
                : ("投币", "fail", lastMsg));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _report.Add(("投币", "fail", e.Message));
        }
    }

    // ================================ HTTP 基础设施 ================================

    private async Task TryFillBuvidAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            _buvid = GetCookieValue(_cookie, "buvid3");
            if (!string.IsNullOrEmpty(_buvid)) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/");
            request.Headers.TryAddWithoutValidation("User-Agent", WebUa);
            using var response = await ctx.Http.SendAsync(request, ct);
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return;
            foreach (var sc in cookies)
            {
                var pair = sc.Split(';')[0].Trim();
                var eq = pair.IndexOf('=');
                if (eq <= 0 || pair[..eq] != "buvid3") continue;
                _buvid = pair[(eq + 1)..];
                _cookie = _cookie.TrimEnd() is { Length: > 0 } rest ? rest + "; buvid3=" + _buvid : "buvid3=" + _buvid;
            }
            ctx.Log(string.IsNullOrEmpty(_buvid) ? "buvid3 未获取到（分享/投币可能 -403）" : "buvid3 已自动补全");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"buvid3 获取失败（忽略）：{e.Message}");
        }
    }

    private async Task<JObject?> GetJsonAsync(QuantumTaskContext ctx, string url, CancellationToken ct,
        string? referer = null, string? origin = null, string ua = WebUa, IDictionary<string, string>? query = null,
        string? cookie = null)
    {
        await Task.Delay(Random.Shared.Next(1500, 3000), ct); // 请求节流：随机 1.5~3s，防匀速特征
        if (query is { Count: > 0 }) url += (url.Contains('?') ? "&" : "?") + BuildQuery(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, referer, origin, ua, cookie);
        using var response = await ctx.Http.SendAsync(request, ct);
        return TryParse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JObject?> PostFormAsync(QuantumTaskContext ctx, string url, IDictionary<string, string> form,
        CancellationToken ct, string? referer = null, string? origin = null, string ua = WebUa,
        IDictionary<string, string>? query = null, string? cookie = null)
    {
        await Task.Delay(Random.Shared.Next(1500, 3000), ct);
        if (query is { Count: > 0 }) url += (url.Contains('?') ? "&" : "?") + BuildQuery(query);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        ApplyHeaders(request, referer, origin, ua, cookie);
        using var response = await ctx.Http.SendAsync(request, ct);
        return TryParse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JObject?> PostJsonAsync(QuantumTaskContext ctx, string url, JObject body,
        CancellationToken ct, string? referer = null, string? origin = null, string ua = WebUa,
        IDictionary<string, string>? query = null)
    {
        await Task.Delay(Random.Shared.Next(1500, 3000), ct);
        if (query is { Count: > 0 }) url += (url.Contains('?') ? "&" : "?") + BuildQuery(query);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        ApplyHeaders(request, referer, origin, ua);
        using var response = await ctx.Http.SendAsync(request, ct);
        return TryParse(await response.Content.ReadAsStringAsync(ct));
    }

    private void ApplyHeaders(HttpRequestMessage request, string? referer = null, string? origin = null, string ua = WebUa,
        string? cookie = null)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", ua);
        request.Headers.TryAddWithoutValidation("Cookie", cookie ?? _cookie);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        if (referer != null) request.Headers.TryAddWithoutValidation("Referer", referer);
        if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
    }

    // ================================ 工具 ================================

    private static Dictionary<string, string> BaseAppParams(Dictionary<string, string> extra)
    {
        var ms = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        return new Dictionary<string, string>(extra)
        {
            ["build"] = "8451100",
            ["disable_rcmd"] = "0",
            ["mobi_app"] = "android",
            ["platform"] = "android",
            ["statistics"] = Statistics,
            ["t"] = ms.ToString(),
            ["ts"] = (ms / 1000).ToString()
        };
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    // visit_id：1~9 的数字 + 10 位随机小写字母数字 + 0（对齐 RayPro Silver2CoinRequest 的说明）
    private static string BuildVisitId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder().Append(Random.Shared.Next(1, 10));
        for (var i = 0; i < 10; i++) sb.Append(chars[Random.Shared.Next(chars.Length)]);
        return sb.Append('0').ToString();
    }

    /// <summary>整理 Cookie：去空白段；SESSDATA 值中的逗号转义为 %2C（B 站 Cookie 拼接要求）。</summary>
    private static string NormalizeCookie(string raw)
    {
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Contains('='))
            .ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            var eq = parts[i].IndexOf('=');
            var name = parts[i][..eq];
            var value = parts[i][(eq + 1)..];
            if (name == "SESSDATA" && value.Contains(',')) value = value.Replace(",", "%2C");
            parts[i] = $"{name}={value}";
        }
        return string.Join("; ", parts);
    }

    private static string GetCookieValue(string cookie, string name)
    {
        foreach (var part in cookie.Split(';'))
        {
            var p = part.Trim();
            if (p.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return p[(name.Length + 1)..];
        }
        return "";
    }

    private static HashSet<string> ParseTasks(string? conf, string[] defaults)
    {
        if (string.IsNullOrWhiteSpace(conf)) return [.. defaults];
        return [.. conf.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static List<string> ParseList(string? conf) =>
        string.IsNullOrWhiteSpace(conf)
            ? []
            : [.. conf.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse((value ?? "").Trim(), out var v) ? v : fallback;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static JObject? TryParse(string text)
    {
        try
        {
            return JObject.Parse(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task NotifyAsync(QuantumTaskContext ctx, string content, CancellationToken ct)
    {
        if (ctx.EnablePush) await ctx.Notify.SendAsync("B站任务", content, ct);
        else ctx.Log($"（推送关闭）B站任务：{content}");
    }
}
