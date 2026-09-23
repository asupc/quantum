// ============================================================================
// 什么值得买（2026-09-18 新增）：签到+签到奖励链、浏览/收藏/点赞/分享每日任务、转盘与幸运屋
// （免费档）抽奖；评论、关注类（用户/栏目/品牌）、众测默认关闭。接口与签名对齐
// hex-ci/smzdm_script（Node.js，个人自用的等价 C# 实现）。
// 2026-09-20 扩展：限时累计活动阶段奖励（任务链收尾补领，对齐 hex-ci smzdm_task.js）、
// 小黑屋检测+资产日报（网页域 jsonp_get_current 只读）、碎银商城兑换（SMZDM_TASKS 加 redeem
// 开启，链路对齐 wjztwjzt/smzdm 的 smzdm_duihuan.py，duihuan 网页通道无签名）。
// 环境变量：
//   SMZDM_COOKIE          必填。手机抓包域名为 user-api.smzdm.com 的任一请求，复制完整 Cookie 原串
//                         （浏览器 F12 的也可用；代码消费其中 sess / smzdm_id / device_id）
//   SMZDM_SK              可选。手动抓 checkin 请求的 sk 参数（DES 算法兜底，默认自动计算）
//   SMZDM_TASKS           可选。逗号分隔子集，默认 sign,view,favorite,rating,share,lottery；
//                         加 wheel 开启转盘抽奖（活动页已大面积过期，默认关）；
//                         加 follow 开启关注类任务，加 comment 且配 SMZDM_COMMENT 开启评论任务；
//                         加 redeem 开启碎银商城兑换
//   SMZDM_COMMENT         可选。评论文案（>10 个汉字；内容风控最高，发完即删，建议个性化）
//   SMZDM_CROWD_SILVER_5  可选 yes。幸运屋 5 碎银档抽奖（消耗碎银）
//   SMZDM_TESTING         可选 yes。众测能量任务
//   SMZDM_GIFT_ID         可选。兑换礼品 Id，默认 800626（600 碎银免邮券）；上下架会变，失效回报提醒换
//   SMZDM_SAFE_PASS       可选。兑换安全码兜底（优先取 Cookie 内 en_safepass 段；App 抓包 Cookie
//                         通常没有该段，需登录网页版抓 zhiyou.smzdm.com 的 Cookie）
//   SMZDM_REDEEM_MIN_SILVER 可选。兑换前碎银门槛，默认 600
// 签名说明（对齐 hex-ci bot.js，C# 等价实现）：
//   1) sign = 业务参数合并公共参（weixin=1/basic_v=0/f=android/v/time=Unix秒+"000"）后按键排序拼
//      k=v&…，再拼 &key=apr1$AwP!wRRT$gJ/q.X24poeBInlUJC，MD5 十六进制大写；空串值的键不参与
//      签名但仍随表单发送；value 参与签名前去第一段空白。
//   2) sk（仅 checkin）= DES/ECB/PKCS7(smzdm_id+device_id，key 取前 8 字节) 的 Base64。
//   3) Cookie 发送前做 iphone→android 字样替换与版本字段覆写（10.4.26 / 866）。
// 风控：请求间随机 1.5~3s、任务间 5~15s、模拟阅读 20~50s、点赞步骤间 3~10s；建议 cron 08:25 错峰。
// ============================================================================
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class SmzdmDailyTask : IQuantumTask
{
    private const string UserApi = "https://user-api.smzdm.com";
    private const string AppVersion = "10.4.26";
    private const string AppVersionRev = "866";
    private const string AndroidSignKey = "apr1$AwP!wRRT$gJ/q.X24poeBInlUJC";
    private const string SkKeyPrefix = "geZm53XA"; // CryptoJS DES 只取密钥串前 8 字节
    private const string AppUa = $"smzdm_android_V{AppVersion} rv:{AppVersionRev} (Redmi Note 3;Android10.0;zh)smzdmapp";
    private const string WebUa =
        $"Mozilla/5.0 (Linux; Android 10.0; Redmi Build/Redmi Note 3; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/95.0.4638.74 Mobile Safari/537.36 smzdm_android_V{AppVersion} rv:{AppVersionRev} (Redmi;Android10.0;zh) jsbv_1.0.0 webv_2.0 smzdmapp";
    private const string PcUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    private static readonly string[] WheelPages =
    [
        "https://m.smzdm.com/topic/bwrzf5/516lft",
        "https://m.smzdm.com/topic/zhyzhuanpan/cjzp/"
    ];

    // touchstone_event 默认底座（对齐 hex-ci getTouchstoneEvent 的 defaultObj，trafic_version 原样内置）
    private const string TouchstoneBase =
        "{\"search_tv\":\"f\",\"sourceRoot\":\"个人中心\",\"trafic_version\":\"113_a,115_b,116_e,118_b,131_b,132_b,134_b,136_b,139_a,144_a,150_b,153_a,179_a,183_b,185_b,188_b,189_b,193_a,196_b,201_a,204_a,205_a,208_b,222_b,226_a,228_a,22_b,230_b,232_b,239_b,254_a,255_b,256_b,258_b,260_b,265_a,267_a,269_a,270_c,273_b,276_a,278_a,27_a,280_a,281_a,283_b,286_a,287_a,290_a,291_b,295_a,302_a,306_b,308_b,312_b,314_a,317_a,318_a,322_b,325_a,326_a,329_b,32_c,332_b,337_c,341_a,347_a,349_b,34_a,351_a,353_b,355_a,357_b,366_b,373_B,376_b,378_b,380_b,388_b,391_b,401_d,403_b,405_b,407_b,416_a,421_a,424_b,425_b,427_a,436_b,43_j,440_a,442_a,444_b,448_a,450_b,451_b,454_b,455_a,458_c,460_a,463_c,464_b,466_b,467_b,46_a,470_b,471_b,474_b,475_a,484_b,489_a,494_b,496_b,498_a,500_a,503_b,507_b,510_bb,512_b,515_a,520_a,522_b,525_c,527_b,528_a,59_a,65_b,85_b,102_b,103_a,106_b,107_b,10_f,11_b,120_a,143_b,157_g,158_c,159_c,160_f,161_d,162_e,163_a,164_a,165_a,166_f,171_a,174_a,175_e,176_d,209_b,225_a,235_a,236_b,237_c,272_b,296_c,2_f,309_a,315_b,334_a,335_d,339_b,346_b,361_b,362_d,367_b,368_a,369_e,374_b,381_c,382_b,383_d,385_b,386_c,389_i,38_b,390_d,396_a,398_b,3_a,413_a,417_a,418_c,419_b,420_b,422_e,428_a,430_a,431_d,432_e,433_a,437_b,438_c,478_b,479_b,47_a,480_a,481_b,482_a,483_a,488_b,491_j,492_j,504_b,505_a,514_a,518_b,52_d,53_d,54_v,55_z1,56_z3,66_a,67_i,68_a1,69_i,74_i,77_d,93_a\",\"tv\":\"z1\"}";

    private static readonly Regex FirstWhitespace = new(@"\s+", RegexOptions.Compiled);

    private string _cookie = "";
    private string _token = "";
    private string _sk = "1";
    private bool _crowdSilver5;
    private bool _cookieDead;
    private readonly List<(string Label, string State, string Detail)> _report = [];

    private sealed record Article(string Id, string ChannelId, string Title, bool IsHaojia);

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // ------------------------------------------------------------------ 1. 凭据
        if (!ctx.Variables.TryGetValue("SMZDM_COOKIE", out var rawCookie) || string.IsNullOrWhiteSpace(rawCookie))
        {
            await NotifyAsync(ctx, $"{QuantumText.Tag("red", "未配置凭据")} 环境变量 SMZDM_COOKIE 缺失：手机抓包 user-api.smzdm.com 任一请求复制完整 Cookie 配到环境变量并启用", ct);
            return;
        }
        _token = MatchCookie(rawCookie, "sess");
        var smzdmId = MatchCookie(rawCookie, "smzdm_id");
        var deviceId = MatchCookie(rawCookie, "device_id");
        if (string.IsNullOrEmpty(deviceId)) deviceId = Random32();
        if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(smzdmId))
        {
            // 诊断信息只报字段缺失，不回显值：抓到未登录态的设备 Cookie 时（字段全是 z_df/did/ch 等
            // 环境参数），提示用户在「已登录的 App」里抓 user-api.smzdm.com 的请求
            var missing = new List<string>();
            if (string.IsNullOrEmpty(_token)) missing.Add("sess（登录令牌）");
            if (string.IsNullOrEmpty(smzdmId)) missing.Add("smzdm_id（用户标识）");
            var fieldCount = rawCookie.Split(';', '\n').Count(p => p.Contains('='));
            await NotifyAsync(ctx,
                $"{QuantumText.Tag("red", "凭据不完整")} Cookie 缺少 {string.Join("、", missing)}（现有 {fieldCount} 个字段）。" +
                "请打开什么值得买 App 并确认已登录，抓包过滤 user-api.smzdm.com 的任意请求（如进「我的」页触发一条），复制该请求的完整 Cookie", ct);
            return;
        }
        _cookie = PrepareCookie(rawCookie);
        if (ctx.Variables.TryGetValue("SMZDM_SK", out var skEnv) && !string.IsNullOrWhiteSpace(skEnv))
            _sk = skEnv.Trim();
        else
            _sk = ComputeSk(smzdmId, deviceId);
        _crowdSilver5 = "yes".Equals(ctx.Variables.TryGetValue("SMZDM_CROWD_SILVER_5", out var silver5) ? silver5 : null, StringComparison.OrdinalIgnoreCase);

        var tasks = ParseTasks(ctx.Variables.TryGetValue("SMZDM_TASKS", out var taskConf) ? taskConf : null,
            ["sign", "view", "favorite", "rating", "share", "lottery"]);
        var wheel = tasks.Contains("wheel"); // 转盘活动页已大面积过期（上游亦注释停用），默认只抽幸运屋
        var comment = ctx.Variables.TryGetValue("SMZDM_COMMENT", out var commentEnv) && !string.IsNullOrWhiteSpace(commentEnv)
            ? commentEnv.Trim() : null;
        var wantTesting = "yes".Equals(ctx.Variables.TryGetValue("SMZDM_TESTING", out var testing) ? testing : null, StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ 2. 签到 + 奖励链
        if (tasks.Contains("sign"))
            await CheckinChainAsync(ctx, ct);

        // ------------------------------------------------------------------ 3. 每日任务（浏览/收藏/点赞/分享/评论/关注）
        var taskKinds = tasks.Where(t => t is "view" or "favorite" or "rating" or "share" or "follow").ToList();
        if (comment != null && tasks.Contains("comment")) taskKinds.Add("comment");
        if (!_cookieDead && taskKinds.Count > 0)
            await TaskChainAsync(ctx, ct, [.. taskKinds], comment);

        // ------------------------------------------------------------------ 4. 抽奖（幸运屋；转盘需 wheel 显式开启）
        if (!_cookieDead && tasks.Contains("lottery"))
            await LotteryAsync(ctx, ct, wheel);

        // ------------------------------------------------------------------ 5. 众测能量任务（默认关）
        if (!_cookieDead && wantTesting)
            await TestingAsync(ctx, ct, [.. taskKinds]);

        // ------------------------------------------------------------------ 5.5 碎银商城兑换（SMZDM_TASKS 加 redeem 开启）
        JObject? webUser = null;
        if (!_cookieDead && tasks.Contains("redeem"))
            webUser = await RedeemAsync(ctx, ct);

        // ------------------------------------------------------------------ 5.6 小黑屋检测 + 资产日报（只读）
        await AssetReportAsync(ctx, ct, webUser);

        // ------------------------------------------------------------------ 6. 汇总推送
        var sb = new StringBuilder($"什么值得买 {DateTime.Now:MM-dd HH:mm}");
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
        if (_cookieDead)
            sb.Append('\n').Append(QuantumText.Tag("red", "Cookie 已失效")).Append(" 请重新抓包更新 SMZDM_COOKIE");
        await NotifyAsync(ctx, sb.ToString(), ct);
        ctx.Log("什么值得买任务执行完毕。");
    }

    // ================================ 签到 + 奖励链 ================================

    private async Task CheckinChainAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            var checkin = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/checkin", new Dictionary<string, string>
            {
                ["touchstone_event"] = "",
                ["sk"] = _sk,
                ["token"] = _token,
                ["captcha"] = ""
            }, sign: true, ct);
            var code = ErrorCode(checkin);
            if (code == 0)
            {
                var d = checkin!["data"];
                _report.Add(("签到", "ok",
                    $"连续 {d["daily_num"]} 天 · 金币 {d["cgold"]} · 碎银 {d["pre_re_silver"]} · 积分 {d["cpoints"]} · 补签卡 {d["cards"]}"));
            }
            else
            {
                var msg = checkin?["error_msg"]?.ToString() ?? "无返回";
                if (msg.Contains("登录", StringComparison.Ordinal) || msg.Contains("失效", StringComparison.Ordinal) || code == 11)
                    _cookieDead = true;
                if (msg.Contains("重复", StringComparison.Ordinal) || msg.Contains("已签", StringComparison.Ordinal))
                    _report.Add(("签到", "skip", "今日已签到"));
                else
                    _report.Add(("签到", "fail", $"error_code={code} {msg}"));
                // 凭据失效才终止；重复签到等业务态继续走奖励链与任务
                if (_cookieDead) return;
            }

            await DelayAsync(5, 15, ct);

            // 签到奖励（error_code==4 视为今日已领/无奖励，静默跳过）
            var reward = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/checkin/all_reward", null, sign: true, ct);
            var rewardCode = ErrorCode(reward);
            var rewardText = "";
            if (rewardCode == 0)
            {
                var nr = reward!["data"]?["normal_reward"]?["reward_add"];
                rewardText = nr?["title"]?.ToString() ?? "";
                var gift = reward["data"]?["gift"];
                if (string.IsNullOrEmpty(rewardText))
                    rewardText = gift?["title"]?.ToString() ?? gift?["sub_content"]?.ToString() ?? "";
                _report.Add(("签到奖励", "ok", StripHtml(rewardText)));
            }
            else if (rewardCode != 4)
            {
                _report.Add(("签到奖励", "fail", $"error_code={rewardCode} {reward?["error_msg"]}"));
            }

            // 连续签到额外奖励（先查 show_view_v2 有无可领）
            var showView = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/checkin/show_view_v2", null, sign: true, ct);
            var hasExtra = (showView?["data"]?["rows"] as JArray)?.Any(r => r["cell_type"]?.ToString() == "18001") == true;
            if (hasExtra)
            {
                var extra = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/checkin/extra_reward", null, sign: true, ct);
                if (ErrorCode(extra) == 0)
                {
                    var text = extra!["data"]?["gift"]?["content"]?.ToString() ?? extra["data"]?["title"]?.ToString() ?? "";
                    _report.Add(("连续签到奖励", "ok", StripHtml(text)));
                }
            }

            // 会员信息
            var vip = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/vip",
                new Dictionary<string, string> { ["token"] = _token }, sign: true, ct);
            if (ErrorCode(vip) == 0)
            {
                var v = vip!["data"]?["vip"];
                // exp_level 为纯数字等级（如 7），exp_current_level 实测是成长值分，不作等级展示
                var levelName = v?["exp_level"]?.ToString();
                if (string.IsNullOrEmpty(levelName)) levelName = v?["exp_current_level"]?.ToString() ?? "?";
                _report.Add(("会员", "skip", $"VIP{levelName}（{v?["exp_level_expire"]} 到期，成长值 {v?["exp_current"]}）"));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"签到链异常：{e.Message}");
            _report.Add(("签到", "fail", e.Message));
        }
    }

    // ================================ 每日任务 ================================

    private async Task TaskChainAsync(QuantumTaskContext ctx, CancellationToken ct, HashSet<string> kinds, string? comment)
    {
        var listResp = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/task/list_v2", null, sign: true, ct);
        var groups = listResp?["data"]?["rows"]?[0]?["cell_data"]?["activity_task"]?["default_list_v2"] as JArray;
        if (groups == null)
        {
            _report.Add(("每日任务", "fail", $"任务列表获取失败 error_code={ErrorCode(listResp)} {listResp?["error_msg"]}"));
            return;
        }
        await RunTaskGroupsAsync(ctx, groups, kinds, comment, receiveViaRobotToken: true, ct);

        // 限时累计活动阶段奖励：任务跑完后再查一次，有可领即领（对齐 hex-ci smzdm_task.js）
        await DelayAsync(5, 15, ct);
        var recheck = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/task/list_v2", null, sign: true, ct);
        var cell = recheck?["data"]?["rows"]?[0]?["cell_data"];
        if (cell?["activity_reward_status"]?.ToString() == "1")
        {
            var activityId = cell!["activity_id"]?.ToString();
            if (!string.IsNullOrEmpty(activityId))
            {
                await DelayAsync(5, 15, ct);
                var claim = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/task/activity_receive",
                    new Dictionary<string, string> { ["activity_id"] = activityId! }, sign: true, ct);
                if (ErrorCode(claim) == 0)
                    _report.Add(("限时累计奖励", "ok", StripHtml(claim!["data"]?["reward_msg"]?.ToString())));
                else
                    _report.Add(("限时累计奖励", "fail", $"error_code={ErrorCode(claim)} {claim?["error_msg"]}"));
            }
        }
        else
        {
            ctx.Log("限时累计活动：当前无可领阶段奖励");
        }
    }

    /// <summary>遍历任务分组并按类型分发执行（task/list_v2 与众测 activity_info 结构同构复用）。</summary>
    private async Task RunTaskGroupsAsync(QuantumTaskContext ctx, JArray groups, HashSet<string> kinds, string? comment,
        bool receiveViaRobotToken, CancellationToken ct)
    {
        var all = new List<JToken>();
        foreach (var group in groups)
        {
            if (group["task_list"] is JArray list) all.AddRange(list);
        }
        if (all.Count == 0)
        {
            _report.Add(("每日任务", "skip", "无待办任务"));
            return;
        }

        var done = 0;
        var total = 0;
        var names = new List<string>();
        foreach (var task in all)
        {
            if (_cookieDead) break;
            ct.ThrowIfCancellationRequested();
            var eventId = task["task_event_type"]?.ToString() ?? "";
            var name = task["task_name"]?.ToString() ?? eventId;
            var status = task["task_status"]?.ToString();
            var kind = eventId.Split('.').Last() switch
            {
                "article" when kinds.Contains("view") => "view",
                "favorite" when kinds.Contains("favorite") => "favorite",
                "rating" when kinds.Contains("rating") => "rating",
                "share" when kinds.Contains("share") => "share",
                "comment" when comment != null && kinds.Contains("comment") => "comment",
                "user" or "tag" or "brand" when kinds.Contains("follow") => "follow",
                _ => null
            };
            if (kind == null)
            {
                ctx.Log($"跳过任务「{name}」（{eventId}，未启用）");
                continue;
            }

            total++;
            try
            {
                if (status == "3") // 待领奖：任务此前已完成，直接领
                {
                    if (await ReceiveRewardAsync(ctx, task["task_id"]?.ToString() ?? "", receiveViaRobotToken, ct))
                    {
                        done++;
                        names.Add(name);
                    }
                    continue;
                }
                if (status != "2")
                {
                    // 其余状态（今日已完成/已关闭）：计入完成，避免误报失败
                    done++;
                    names.Add(name);
                    continue;
                }

                await DelayAsync(5, 15, ct);
                var ok = kind switch
                {
                    "view" => await DoViewTaskAsync(ctx, task, ct),
                    "favorite" => await DoArticleLoopTaskAsync(ctx, task, "favorite", ct),
                    "rating" => await DoArticleLoopTaskAsync(ctx, task, "rating", ct),
                    "share" => await DoArticleLoopTaskAsync(ctx, task, "share", ct),
                    "comment" => await DoCommentTaskAsync(ctx, task, comment!, ct),
                    "follow" => await DoFollowTaskAsync(ctx, task, eventId, ct),
                    _ => false
                };
                if (ok && await ReceiveRewardAsync(ctx, task["task_id"]?.ToString() ?? "", receiveViaRobotToken, ct))
                {
                    done++;
                    names.Add(name);
                }
                else if (!ok)
                {
                    ctx.Log($"任务「{name}」（{eventId}）未完成");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                ctx.Log($"任务「{name}」异常：{e.Message}");
            }
        }
        _report.Add(("每日任务", total == 0 || done >= total ? (total == 0 ? "skip" : "ok") : "fail",
            total == 0 ? "" : $"{done}/{total}（{string.Join("、", names)}）"));
    }

    private async Task<bool> ReceiveRewardAsync(QuantumTaskContext ctx, string taskId, bool viaRobotToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(taskId)) return false;
        Dictionary<string, string> data;
        if (viaRobotToken)
        {
            var robot = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/robot/token", null, sign: true, ct);
            var robotToken = robot?["data"]?["token"]?.ToString();
            if (string.IsNullOrEmpty(robotToken))
            {
                ctx.Log($"robot_token 获取失败（task={taskId}）");
                return false;
            }
            data = new Dictionary<string, string>
            {
                ["robot_token"] = robotToken!,
                ["geetest_seccode"] = "",
                ["geetest_validate"] = "",
                ["geetest_challenge"] = "",
                ["captcha"] = "",
                ["task_id"] = taskId
            };
        }
        else
        {
            data = new Dictionary<string, string> { ["task_id"] = taskId };
        }
        var resp = await ApiAsync(ctx, HttpMethod.Post,
            viaRobotToken ? UserApi + "/task/activity_task_receive" : "https://zhiyou.m.smzdm.com/task/task/ajax_activity_task_receive",
            data, sign: viaRobotToken, ct,
            web: !viaRobotToken, origin: viaRobotToken ? null : "https://test.m.smzdm.com/",
            referer: viaRobotToken ? null : "https://test.m.smzdm.com/");
        var code = ErrorCode(resp);
        if (code != 0)
            ctx.Log($"领奖失败 task={taskId} error_code={code} {resp?["error_msg"]}（可能触发验证码）");
        return code == 0;
    }

    private static int Remaining(JToken task)
    {
        var need = ParseInt(task["task_even_num"]?.ToString());
        var finished = ParseInt(task["task_finished_num"]?.ToString());
        return Math.Clamp(need - finished, 1, 5);
    }

    // ================================ 浏览文章任务 ================================

    private async Task<bool> DoViewTaskAsync(QuantumTaskContext ctx, JToken task, CancellationToken ct)
    {
        var taskId = task["task_id"]?.ToString() ?? "";
        var remain = Remaining(task);
        // 任务对象自带预期文章与频道（服务端按 task.article_id/task.channel_id 校验）；
        // 未指定文章的任务才回退到榜单取文章
        var expectedArticle = task["article_id"]?.ToString();
        var taskChannel = task["channel_id"]?.ToString();
        List<Article> articles;
        if (!string.IsNullOrEmpty(expectedArticle))
        {
            articles = [new Article(expectedArticle, taskChannel ?? "0", expectedArticle, false)];
        }
        else
        {
            articles = await GetArticlesAsync(ctx, remain, ct);
        }
        if (articles.Count == 0)
        {
            ctx.Log("浏览任务：未取到文章列表");
            return false;
        }
        foreach (var article in articles)
        {
            ct.ThrowIfCancellationRequested();
            // 模拟打开文章详情（好价类走 haojia-api）
            var detailUrl = article.IsHaojia
                ? $"https://haojia-api.smzdm.com/detail/{article.Id}?imgmode=0&hashcode=&h5hash="
                : $"https://article-api.smzdm.com/article_detail/{article.Id}?comment_flow=&hashcode=&lastest_update_time=&uhome=0&imgmode=0&article_channel_id=0&h5hash=";
            await ApiAsync(ctx, HttpMethod.Get, detailUrl, null, sign: true, ct);

            await DelayAsync(20, 50, ct); // 模拟阅读时长

            var sync = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/task/event_view_article_sync",
                new Dictionary<string, string>
                {
                    ["article_id"] = article.Id,
                    ["channel_id"] = string.IsNullOrEmpty(taskChannel) ? article.ChannelId : taskChannel!,
                    ["task_id"] = taskId
                }, sign: true, ct);
            if (ErrorCode(sync) != 0)
            {
                ctx.Log($"浏览上报失败 article={article.Id} error_code={ErrorCode(sync)} {sync?["error_msg"]}");
                return false;
            }
        }
        return true;
    }

    // ================================ 收藏 / 点赞 / 分享（还原型文章任务） ================================

    private async Task<bool> DoArticleLoopTaskAsync(QuantumTaskContext ctx, JToken task, string kind, CancellationToken ct)
    {
        var remain = Remaining(task);
        var articles = await GetArticlesAsync(ctx, Math.Min(remain, 3), ct);
        if (articles.Count == 0)
        {
            ctx.Log($"{kind} 任务：未取到文章列表");
            return false;
        }
        var ok = true;
        foreach (var article in articles)
        {
            ct.ThrowIfCancellationRequested();
            var touchstone = TouchstoneEvent(new JObject
            {
                ["event_value"] = new JObject { ["aid"] = article.Id, ["cid"] = article.ChannelId, ["is_detail"] = true },
                ["sourceMode"] = "我的_我的任务页",
                ["sourcePage"] = $"Android/长图文/P/{article.Id}/",
                ["upperLevel_url"] = "个人中心/赚奖励/"
            });
            var one = kind switch
            {
                "favorite" => await DoFavoriteOnceAsync(ctx, article, touchstone, ct),
                "rating" => await DoRatingOnceAsync(ctx, article, touchstone, ct),
                _ => await DoShareOnceAsync(ctx, article, touchstone, ct)
            };
            ok = ok && one;
            await DelayAsync(5, 15, ct);
        }
        return ok;
    }

    /// <summary>收藏任务：destroy → create → destroy（还原为未收藏）。</summary>
    private async Task<bool> DoFavoriteOnceAsync(QuantumTaskContext ctx, Article article, string touchstone, CancellationToken ct)
    {
        foreach (var action in new[] { "destroy", "create", "destroy" })
        {
            var resp = await ApiAsync(ctx, HttpMethod.Post, UserApi + $"/favorites/{action}",
                new Dictionary<string, string>
                {
                    ["touchstone_event"] = touchstone,
                    ["token"] = _token,
                    ["id"] = article.Id,
                    ["channel_id"] = article.ChannelId
                }, sign: true, ct);
            if (ErrorCode(resp) != 0)
            {
                ctx.Log($"收藏 {action} 失败 article={article.Id} error_code={ErrorCode(resp)}");
                return false;
            }
            await DelayAsync(3, 10, ct);
        }
        return true;
    }

    /// <summary>点赞任务：普通文章 like 三连（cancel→create→cancel→create→cancel），好价 worth 三连（带 wtype）。</summary>
    private async Task<bool> DoRatingOnceAsync(QuantumTaskContext ctx, Article article, string touchstone, CancellationToken ct)
    {
        var sequence = article.IsHaojia
            ? new[] { ("worth_cancel", "3"), ("worth_create", "1"), ("worth_cancel", "3") }
            : new[] { ("like_cancel", ""), ("like_create", ""), ("like_cancel", ""), ("like_create", ""), ("like_cancel", "") };
        foreach (var (method, wtype) in sequence)
        {
            var data = new Dictionary<string, string>
            {
                ["touchstone_event"] = touchstone,
                ["token"] = _token,
                ["id"] = article.Id,
                ["channel_id"] = article.ChannelId
            };
            if (!string.IsNullOrEmpty(wtype)) data["wtype"] = wtype;
            var resp = await ApiAsync(ctx, HttpMethod.Post, UserApi + $"/rating/{method}", data, sign: true, ct);
            if (ErrorCode(resp) != 0)
            {
                ctx.Log($"点赞 {method} 失败 article={article.Id} error_code={ErrorCode(resp)}");
                return false;
            }
            await DelayAsync(3, 10, ct);
        }
        return true;
    }

    /// <summary>分享任务：complete_share_rule → daily_reward → callback。</summary>
    private async Task<bool> DoShareOnceAsync(QuantumTaskContext ctx, Article article, string touchstone, CancellationToken ct)
    {
        var first = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/share/complete_share_rule",
            new Dictionary<string, string>
            {
                ["token"] = _token,
                ["article_id"] = article.Id,
                ["channel_id"] = article.ChannelId,
                ["tag_name"] = "gerenzhongxin"
            }, sign: true, ct);
        if (ErrorCode(first) != 0)
        {
            ctx.Log($"分享规则上报失败 article={article.Id} error_code={ErrorCode(first)}");
            return false;
        }
        await ApiAsync(ctx, HttpMethod.Post, UserApi + "/share/daily_reward",
            new Dictionary<string, string> { ["token"] = _token, ["channel_id"] = article.ChannelId }, sign: true, ct);
        await DelayAsync(3, 10, ct);
        var callback = await ApiAsync(ctx, HttpMethod.Post, UserApi + "/share/callback",
            new Dictionary<string, string>
            {
                ["token"] = _token,
                ["article_id"] = article.Id,
                ["channel_id"] = article.ChannelId,
                ["touchstone_event"] = TouchstoneEvent(new JObject
                {
                    ["sourceMode"] = "排行榜_社区_好文精选",
                    ["sourcePage"] = $"Android/长图文/P/{article.Id}/",
                    ["upperLevel_url"] = "排行榜/社区/好文精选/文章_24H/"
                })
            }, sign: true, ct);
        return ErrorCode(callback) == 0;
    }

    // ================================ 评论任务（默认关） ================================

    private async Task<bool> DoCommentTaskAsync(QuantumTaskContext ctx, JToken task, string comment, CancellationToken ct)
    {
        var articles = await GetArticlesAsync(ctx, 1, ct);
        if (articles.Count == 0) return false;
        var article = articles[Random.Shared.Next(articles.Count)];
        var touchstone = TouchstoneEvent(new JObject
        {
            ["sourceMode"] = "好物社区_全部",
            ["sourcePage"] = $"Android/长图文/{article.Id}/评论页/",
            ["upperLevel_url"] = "好物社区/首页/全部/",
            ["sourceRoot"] = "社区"
        });
        var submit = await ApiAsync(ctx, HttpMethod.Post, "https://comment-api.smzdm.com/comments/submit",
            new Dictionary<string, string>
            {
                ["touchstone_event"] = touchstone,
                ["is_like"] = "3",
                ["reply_from"] = "3",
                ["smiles"] = "0",
                ["atta"] = "0",
                ["parentid"] = "0",
                ["token"] = _token,
                ["article_id"] = article.Id,
                ["channel_id"] = article.ChannelId,
                ["content"] = comment
            }, sign: true, ct);
        if (ErrorCode(submit) != 0)
        {
            ctx.Log($"评论发布失败 error_code={ErrorCode(submit)} {submit?["error_msg"]}");
            return false;
        }
        var commentId = submit?["data"]?["comment_ID"]?.ToString();
        await DelayAsync(20, 30, ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var delete = await ApiAsync(ctx, HttpMethod.Post, "https://comment-api.smzdm.com/comments/delete_comment",
                new Dictionary<string, string> { ["comment_id"] = commentId ?? "" }, sign: true, ct);
            if (ErrorCode(delete) == 0) return true;
            await DelayAsync(10, 20, ct);
        }
        ctx.Log($"评论已发布但删除失败（comment_id={commentId}），请手动清理");
        return true; // 任务本身已完成
    }

    // ================================ 关注类任务（默认关） ================================

    private async Task<bool> DoFollowTaskAsync(QuantumTaskContext ctx, JToken task, string eventId, CancellationToken ct)
    {
        return eventId.Split('.').Last() switch
        {
            "user" => await DoFollowUserAsync(ctx, Remaining(task), ct),
            "tag" => await DoFollowTagAsync(ctx, Remaining(task), ct),
            "brand" => await DoFollowBrandAsync(ctx, task, ct),
            _ => false
        };
    }

    /// <summary>关注用户：随机推荐用户 → 关注 → 取关（还原）。</summary>
    private async Task<bool> DoFollowUserAsync(QuantumTaskContext ctx, int remain, CancellationToken ct)
    {
        var search = await ApiAsync(ctx, HttpMethod.Post, "https://dingyue-api.smzdm.com/tuijian/search_result",
            new Dictionary<string, string> { ["nav_id"] = "0", ["page"] = "1", ["type"] = "user", ["time_code"] = "" },
            sign: true, ct);
        var rows = search?["data"]?["rows"] as JArray;
        if (rows == null || rows.Count == 0)
        {
            ctx.Log("关注用户：推荐列表为空");
            return false;
        }
        var ok = true;
        for (var i = 0; i < remain; i++)
        {
            var user = (JObject)rows[Random.Shared.Next(rows.Count)];
            var keywordId = user["keyword_id"]?.ToString() ?? user["keywordId"]?.ToString() ?? "";
            var keyword = user["keyword"]?.ToString() ?? user["name"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(keywordId)) continue;
            var followed = await DingyueToggleAsync(ctx, "create", keywordId, keyword, "user", ct);
            if (!followed)
            {
                // 已关注过：先取关再关注
                await DingyueToggleAsync(ctx, "destroy", keywordId, keyword, "user", ct);
                followed = await DingyueToggleAsync(ctx, "create", keywordId, keyword, "user", ct);
            }
            if (followed) await DingyueToggleAsync(ctx, "destroy", keywordId, keyword, "user", ct); // 还原
            ok = ok && followed;
            await DelayAsync(5, 15, ct);
        }
        return ok;
    }

    /// <summary>关注栏目：随机推荐栏目 → 关注 → 取关（还原）。</summary>
    private async Task<bool> DoFollowTagAsync(QuantumTaskContext ctx, int remain, CancellationToken ct)
    {
        var search = await ApiAsync(ctx, HttpMethod.Post, "https://dingyue-api.smzdm.com/tuijian/search_result",
            new Dictionary<string, string> { ["nav_id"] = "0", ["page"] = "1", ["type"] = "tag", ["time_code"] = "" },
            sign: true, ct);
        var rows = search?["data"]?["rows"] as JArray;
        if (rows == null || rows.Count == 0)
        {
            ctx.Log("关注栏目：推荐列表为空");
            return false;
        }
        var ok = true;
        for (var i = 0; i < remain; i++)
        {
            var tag = (JObject)rows[Random.Shared.Next(rows.Count)];
            var keywordId = tag["keyword_id"]?.ToString() ?? tag["keywordId"]?.ToString() ?? "";
            var keyword = tag["keyword"]?.ToString() ?? tag["name"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(keywordId)) continue;
            var followed = await DingyueToggleAsync(ctx, "create", keywordId, keyword, "tag", ct);
            if (followed) await DingyueToggleAsync(ctx, "destroy", keywordId, keyword, "tag", ct);
            ok = ok && followed;
            await DelayAsync(5, 15, ct);
        }
        return ok;
    }

    /// <summary>关注品牌：任务指定品牌 → 取关 → 关注 → 取关（还原）。</summary>
    private async Task<bool> DoFollowBrandAsync(QuantumTaskContext ctx, JToken task, CancellationToken ct)
    {
        var brandId = task["task_redirect_url"]?["link_val"]?.ToString();
        if (string.IsNullOrEmpty(brandId))
        {
            ctx.Log("关注品牌：任务未指定品牌 id");
            return false;
        }
        var basic = await ApiAsync(ctx, HttpMethod.Get, $"https://brand-api.smzdm.com/brand/brand_basic?brand_id={brandId}",
            null, sign: true, ct);
        var brandName = basic?["data"]?["title"]?.ToString() ?? brandId;
        var touchstone = TouchstoneEvent(null);
        var refer = $"Android/其他/品牌详情页/{brandName}/{brandId}/";
        var ok = true;
        // del → add → del（对齐 hex-ci 序列）
        foreach (var action in new[] { "dingyue_lanmu_del", "dingyue_lanmu_add", "dingyue_lanmu_del" })
        {
            var resp = await ApiAsync(ctx, HttpMethod.Post, "https://dingyue-api.smzdm.com/dy/util/api/user_action",
                new Dictionary<string, string>
                {
                    ["action"] = action,
                    ["params"] = new JObject
                    {
                        ["keyword"] = brandId!,
                        ["keyword_id"] = brandId!,
                        ["type"] = "brand"
                    }.ToString(Formatting.None),
                    ["refer"] = refer,
                    ["touchstone_event"] = touchstone
                }, sign: true, ct);
            if (ErrorCode(resp) != 0)
            {
                ctx.Log($"关注品牌 {action} 失败 error_code={ErrorCode(resp)}");
                ok = false;
            }
            await DelayAsync(3, 10, ct);
        }
        return ok;
    }

    private async Task<bool> DingyueToggleAsync(QuantumTaskContext ctx, string action, string keywordId, string keyword,
        string type, CancellationToken ct)
    {
        var resp = await ApiAsync(ctx, HttpMethod.Post, $"https://dingyue-api.smzdm.com/dingyue/{action}",
            new Dictionary<string, string>
            {
                ["touchstone_event"] = TouchstoneEvent(null),
                ["refer"] = "",
                ["keyword_id"] = keywordId,
                ["keyword"] = keyword,
                ["type"] = type
            }, sign: true, ct);
        return ErrorCode(resp) == 0;
    }

    // ================================ 抽奖 ================================

    private async Task LotteryAsync(QuantumTaskContext ctx, CancellationToken ct, bool wheel)
    {
        var wheelResults = new List<string>();
        if (wheel)
        {
            foreach (var pageUrl in WheelPages)
            {
                try
                {
                    var html = await WebAsync(ctx, HttpMethod.Get, pageUrl, null, ct,
                        referer: "https://m.smzdm.com/", xRequested: true);
                    var match = Regex.Match(html, "\"hashId\\\\\":\\\\\"([^\\\\]+)\\\\\"");
                    if (!match.Success)
                    {
                        wheelResults.Add("转盘：页面未找到活动 id");
                        continue;
                    }
                    var drawText = await WebAsync(ctx, HttpMethod.Post, "https://zhiyou.smzdm.com/user/lottery/jsonp_draw",
                        new Dictionary<string, string>
                        {
                            ["active_id"] = match.Groups[1].Value,
                            ["callback"] = "jQuery34107538452897131465_" + DateTimeOffset.Now.ToUnixTimeMilliseconds()
                        }, ct, referer: "https://m.smzdm.com/", xRequested: true);
                    var draw = ParseJsonp(drawText);
                    var code = ErrorCode(draw);
                    wheelResults.Add(code == 0
                        ? $"转盘：{draw?["data"]?["title"] ?? draw?["error_msg"]?.ToString() ?? "已抽"}"
                        : $"转盘：{draw?["error_msg"]?.ToString() ?? $"error_code={code}"}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    wheelResults.Add($"转盘异常：{e.Message}");
                }
                await DelayAsync(5, 15, ct);
            }
        }

        // 幸运屋：免费档必抽；5 碎银档按开关
        var crowdResult = "幸运屋：无可用免费抽奖";
        try
        {
            var crowdHtml = await WebAsync(ctx, HttpMethod.Get, "https://zhiyou.smzdm.com/user/crowd/", null, ct,
                referer: "https://zhiyou.smzdm.com/user/crowd/");
            var freeId = MatchCrowdId(crowdHtml, "免费");
            var silverId = _crowdSilver5 ? MatchCrowdId(crowdHtml, "5碎银子") : null;
            var picked = freeId ?? silverId;
            if (picked != null)
            {
                var participate = await WebAsync(ctx, HttpMethod.Post,
                    "https://zhiyou.m.smzdm.com/user/crowd/ajax_participate",
                    new Dictionary<string, string>
                    {
                        ["crowd_id"] = picked,
                        ["sourcePage"] = $"https://zhiyou.m.smzdm.com/user/crowd/p/{picked}/",
                        ["client_type"] = "android",
                        ["sourceRoot"] = "个人中心",
                        ["sourceMode"] = "幸运屋抽奖",
                        ["price_id"] = "1"
                    }, ct, origin: "https://zhiyou.m.smzdm.com",
                    referer: $"https://zhiyou.m.smzdm.com/user/crowd/p/{picked}/");
                var json = TryParse(participate);
                crowdResult = ErrorCode(json) == 0
                    ? $"幸运屋：{(freeId != null ? "免费档" : "5碎银档")}已参与"
                    : $"幸运屋：{json?["error_msg"]?.ToString() ?? participate[..Math.Min(60, participate.Length)]}";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            crowdResult = $"幸运屋异常：{e.Message}";
        }

        var lotteryText = wheelResults.Count > 0 ? string.Join("；", wheelResults) + "；" : "";
        _report.Add(("抽奖", "ok", $"{lotteryText}{crowdResult}"));
    }

    private static string? MatchCrowdId(string html, string label)
    {
        var match = Regex.Match(html, $@"<button\s+([^>]+?)>\s*<div[^>]+>\s*{Regex.Escape(label)}[^<]*</div>");
        if (!match.Success) return null;
        var id = Regex.Match(match.Groups[1].Value, "data-crowd_id=\"(\\d+)\"");
        return id.Success ? id.Groups[1].Value : null;
    }

    // ================================ 众测能量任务（默认关） ================================

    private async Task TestingAsync(QuantumTaskContext ctx, CancellationToken ct, HashSet<string> kinds)
    {
        try
        {
            var idText = await WebAsync(ctx, HttpMethod.Get,
                "https://zhiyou.m.smzdm.com/task/task/ajax_get_activity_id", new Dictionary<string, string> { ["from"] = "zhongce" },
                ct, origin: "https://test.m.smzdm.com/", referer: "https://test.m.smzdm.com/");
            var idJson = TryParse(idText);
            var activityId = idJson?["data"]?["activity_id"]?.ToString();
            if (string.IsNullOrEmpty(activityId))
            {
                _report.Add(("众测能量", "skip", "无进行中的众测活动"));
                return;
            }
            var infoText = await WebAsync(ctx, HttpMethod.Get,
                "https://zhiyou.m.smzdm.com/task/task/ajax_get_activity_info",
                new Dictionary<string, string> { ["activity_id"] = activityId }, ct,
                origin: "https://test.m.smzdm.com/", referer: "https://test.m.smzdm.com/");
            var info = TryParse(infoText);
            if (info?["data"]?["activity_task"]?["default_list"] is not JArray groups || groups.Count == 0)
            {
                _report.Add(("众测能量", "skip", "无待办任务"));
                return;
            }
            await RunTaskGroupsAsync(ctx, groups, kinds, null, receiveViaRobotToken: false, ct);

            var energyText = await WebAsync(ctx, HttpMethod.Get, "https://test.m.smzdm.com/win_coupon/user_data", null, ct,
                origin: "https://test.m.smzdm.com/", referer: "https://test.m.smzdm.com/");
            var energy = TryParse(energyText)?["data"]?["my_energy"]?["my_energy_total"];
            if (energy != null) _report.Add(("众测能量", "ok", $"当前能量 {energy}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"众测任务异常：{e.Message}");
            _report.Add(("众测能量", "fail", e.Message));
        }
    }

    // ================================ 碎银商城兑换（默认关，链路对齐 wjztwjzt/smzdm） ================================

    /// <summary>碎银达标自动兑换礼品并回报「我的礼品」最新状态；返回已取的网页域用户信息（供资产日报复用）。</summary>
    private async Task<JObject?> RedeemAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        JObject? user = null;
        try
        {
            var giftId = ctx.Variables.TryGetValue("SMZDM_GIFT_ID", out var gid) && !string.IsNullOrWhiteSpace(gid)
                ? gid.Trim() : "800626";
            var minSilver = 600;
            if (ctx.Variables.TryGetValue("SMZDM_REDEEM_MIN_SILVER", out var ms) && int.TryParse((ms ?? "").Trim(), out var parsedMin))
                minSilver = parsedMin;

            user = await GetWebUserInfoAsync(ctx, ct);
            if (user == null || user["smzdm_id"]?.Value<long>() is not > 0)
            {
                _report.Add(("碎银兑换", "fail", "网页域用户信息获取失败（Cookie 可能不含网页域凭据）"));
                return user;
            }
            var silver = ParseInt(user["silver"]?.ToString());
            if (silver < minSilver)
            {
                _report.Add(("碎银兑换", "skip", $"碎银 {silver} 不足 {minSilver}，未尝试（礼品 {giftId}）"));
            }
            else
            {
                // safe_pass：Cookie 内 en_safepass 段优先，SMZDM_SAFE_PASS 兜底
                var safePass = MatchCookie(_cookie, "en_safepass");
                if (string.IsNullOrEmpty(safePass) && ctx.Variables.TryGetValue("SMZDM_SAFE_PASS", out var spEnv) &&
                    !string.IsNullOrWhiteSpace(spEnv))
                    safePass = spEnv.Trim();
                if (string.IsNullOrEmpty(safePass))
                {
                    _report.Add(("碎银兑换", "fail",
                        "缺少兑换安全码：登录什么值得买网页版，抓 zhiyou.smzdm.com 任一请求的 Cookie，把 en_safepass=… 段并入 SMZDM_COOKIE，或单独配 SMZDM_SAFE_PASS"));
                }
                else
                {
                    var respText = await PcWebAsync(ctx, HttpMethod.Post, $"https://duihuan.smzdm.com/quan/lingqugift/{giftId}",
                        new Dictionary<string, string>
                        {
                            ["safe_pass"] = safePass!,
                            ["client_type"] = "PC",
                            ["sourcePage"] = $"https://duihuan.smzdm.com/d/{giftId}/"
                        }, ct, origin: "https://duihuan.smzdm.com", referer: $"https://duihuan.smzdm.com/d/{giftId}/",
                        xRequested: true);
                    var resp = TryParse(respText);
                    if (ErrorCode(resp) == 0)
                        _report.Add(("碎银兑换", "ok", $"已提交礼品 {giftId}：{StripHtml(resp?["data"]?["reward_msg"]?.ToString() ?? resp?["data"]?["msg"]?.ToString())}"));
                    else
                        _report.Add(("碎银兑换", "fail",
                            $"error_code={ErrorCode(resp)} {resp?["error_msg"]?.ToString() ?? Truncate(respText, 50)}（礼品 {giftId}，可能已下架或已抢完）"));
                }
            }

            // 「我的礼品」最新状态（无论是否尝试兑换都回报，审核通过带券码）
            var giftHtml = await PcWebAsync(ctx, HttpMethod.Get, "https://zhiyou.smzdm.com/user/gift/", null, ct,
                referer: "https://zhiyou.smzdm.com/user/coupon/");
            var records = ParseGiftRecords(giftHtml);
            if (records.Count == 0)
                _report.Add(("礼品状态", "skip", "暂无礼品记录"));
            else
                _report.Add(("礼品状态", "skip", string.Join("；", records.Take(2).Select(r =>
                    $"{r.Date}｜{r.Status}｜{Truncate(r.Title, 14)}" +
                    (r.Status.Contains("审核通过", StringComparison.Ordinal) && r.Secret.Length > 0 ? $"｜券码 {r.Secret}" : "")))));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"兑换链异常：{e.Message}");
            _report.Add(("碎银兑换", "fail", e.Message));
        }
        return user;
    }

    private sealed record GiftRecord(string Id, string Title, string Status, string Date, string Secret);

    // 「我的礼品」页解析（对齐 wjztwjzt smzdm_duihuan.py 的正则回退方案）
    private static readonly Regex GiftHrefRegex =
        new(@"href\s*=\s*[""'](https?://duihuan\.smzdm\.com/d/(\d+)[^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScoreLeftRegex =
        new(@"<div\s+class=""[^""]*scoreLeft[^""]*""[^>]*>([^<]*)</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScoreUseRegex =
        new(@"<div\s+class=""[^""]*scoreUse[^""]*""[^>]*>([^<]*)</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SubNoticeRegex =
        new(@"<div\s+class=""[^""]*subNoticeYellow[^""]*""[^>]*>([^<]*)</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>解析「我的礼品」页：锚点 duihuan.smzdm.com/d/{id}，前文最近 scoreLeft=日期、后文首个 scoreUse=状态、subNoticeYellow=券码。</summary>
    private static List<GiftRecord> ParseGiftRecords(string html)
    {
        var result = new List<GiftRecord>();
        var seen = new HashSet<string>();
        foreach (var m in GiftHrefRegex.Matches(html).Cast<Match>())
        {
            var id = m.Groups[2].Value;
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            var before = html[Math.Max(0, m.Index - 800)..m.Index];
            var after = html[m.Index..Math.Min(html.Length, m.Index + m.Length + 600)];
            var date = ScoreLeftRegex.Matches(before).Cast<Match>().LastOrDefault()?.Groups[1].Value.Trim() ?? "";
            var status = ScoreUseRegex.Match(after).Groups[1].Value.Trim();
            var titleMatch = Regex.Match(before + html[m.Index..Math.Min(html.Length, m.Index + m.Length + 200)],
                @"<a\s+href\s*=\s*[""'][^""]*duihuan\.smzdm\.com/d/\d+[^""]*[""'][^>]*>([^<]+)", RegexOptions.IgnoreCase);
            var title = titleMatch.Success ? titleMatch.Groups[1].Value : "";
            title = Regex.Replace(title, @"\s*[>›]\s*$", "").Trim();
            title = Truncate(Regex.Replace(title, "<[^>]+>", " ").Trim(), 20);
            if (string.IsNullOrEmpty(title)) title = "礼品 " + id;
            var secret = SubNoticeRegex.Match(before + after).Groups[1].Value.Replace("\u00a0", " ").Trim();
            result.Add(new GiftRecord(id, title, status, date, secret));
        }
        return result;
    }

    // ================================ 小黑屋检测 + 资产日报（只读） ================================

    /// <summary>网页域资产日报：金币/碎银/连签；进小黑屋红色告警（会影响幸运屋资格）。cached 为兑换链已取的用户信息，避免重复请求。</summary>
    private async Task AssetReportAsync(QuantumTaskContext ctx, CancellationToken ct, JObject? cached)
    {
        try
        {
            var info = cached ?? await GetWebUserInfoAsync(ctx, ct);
            if (info == null || info["smzdm_id"]?.Value<long>() is not > 0)
            {
                ctx.Log("资产日报：网页域用户信息未取到（Cookie 可能不含网页域凭据），跳过");
                return;
            }
            var gold = info["gold"]?.ToString();
            var silver = info["silver"]?.ToString();
            var daily = info["checkin"]?["daily_checkin_num"]?.ToString();
            _report.Add(("资产", "skip",
                $"金币 {(string.IsNullOrEmpty(gold) ? "?" : gold)} · 碎银 {(string.IsNullOrEmpty(silver) ? "?" : silver)} · 连签 {(string.IsNullOrEmpty(daily) ? "?" : daily)} 天"));
            var black = info["blackroom_desc"]?.ToString();
            if (!string.IsNullOrEmpty(black))
                _report.Add(("小黑屋", "fail", $"{black}（等级 {info["blackroom_level"]}）"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"资产日报异常：{e.Message}");
        }
    }

    /// <summary>网页域用户信息（JSONP）：gold/silver/checkin/blackroom 字段（接口与幸运屋同域，App Cookie 实测可过）。</summary>
    private async Task<JObject?> GetWebUserInfoAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var text = await WebAsync(ctx, HttpMethod.Get,
            $"https://zhiyou.smzdm.com/user/info/jsonp_get_current?with_avatar_ornament=1&callback=jQuery112403507528653716241_{ts / 1000}&_={ts}",
            null, ct, referer: "https://zhiyou.smzdm.com/user/");
        return ParseJsonp(text);
    }

    /// <summary>PC 网页通道请求（兑换 duihuan / 我的礼品页）：PC UA，无签名。</summary>
    private async Task<string> PcWebAsync(QuantumTaskContext ctx, HttpMethod method, string url,
        Dictionary<string, string>? data, CancellationToken ct, string? referer = null, string? origin = null,
        bool xRequested = false)
    {
        await Task.Delay(Random.Shared.Next(1500, 3000), ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", PcUa);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        if (xRequested) request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (referer != null) request.Headers.TryAddWithoutValidation("Referer", referer);
        if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (data != null && data.Count > 0)
        {
            if (method == HttpMethod.Get)
                request.RequestUri = new Uri(url + (url.Contains('?') ? "&" : "?") + BuildQuery(data));
            else
                request.Content = new FormUrlEncodedContent(data);
        }
        using var response = await ctx.Http.SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ================================ 文章来源 ================================

    private async Task<List<Article>> GetArticlesAsync(QuantumTaskContext ctx, int count, CancellationToken ct)
    {
        var result = new List<Article>();
        var resp = await ApiAsync(ctx, HttpMethod.Get, "https://article-api.smzdm.com/ranking_list/articles",
            new Dictionary<string, string>
            {
                ["offset"] = "0",
                ["channel_id"] = "76",
                ["tab"] = "2",
                ["order"] = "0",
                ["limit"] = "20",
                ["exclude_article_ids"] = "",
                ["stream"] = "a",
                ["ab_code"] = "b"
            }, sign: true, ct);
        if (resp?["data"]?["rows"] is not JArray rows) return result;
        foreach (var row in rows)
        {
            if (result.Count >= count) break;
            var id = row["article_id"]?.ToString() ?? row["articleid"]?.ToString();
            var channelId = row["channel_id"]?.ToString() ?? "76";
            if (string.IsNullOrEmpty(id)) continue;
            var price = row["article_price"]?.ToString();
            result.Add(new Article(id!, channelId, row["article_title"]?.ToString() ?? id!, !string.IsNullOrEmpty(price)));
        }
        return result;
    }

    // ================================ 签名与加密 ================================

    /// <summary>对齐 bot.js signFormData：合并公共参数 → 去空值 → 键排序 → 拼 &key 后 MD5 大写。</summary>
    private static Dictionary<string, string> SignFormData(Dictionary<string, string> data)
    {
        var newData = new Dictionary<string, string>(data)
        {
            ["weixin"] = "1",
            ["basic_v"] = "0",
            ["f"] = "android",
            ["v"] = AppVersion,
            ["time"] = DateTimeOffset.Now.ToUnixTimeSeconds() + "000"
        };
        var signData = string.Join("&", newData.Keys
            .Where(k => newData[k] != "")
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{k}={FirstWhitespace.Replace(newData[k], "", 1)}"));
        newData["sign"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signData + "&key=" + AndroidSignKey)));
        return newData;
    }

    /// <summary>对齐 smzdm_checkin.js getSk：DES/ECB/PKCS7(smzdm_id+device_id) → Base64。</summary>
    private static string ComputeSk(string smzdmId, string deviceId)
    {
        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;
        des.Key = Encoding.UTF8.GetBytes(SkKeyPrefix);
        using var encryptor = des.CreateEncryptor();
        var plain = Encoding.UTF8.GetBytes(smzdmId + deviceId);
        return Convert.ToBase64String(encryptor.TransformFinalBlock(plain, 0, plain.Length));
    }

    /// <summary>Cookie 预处理：iphone→android 字样替换 + 版本字段覆写（对齐 bot.js 构造函数）。</summary>
    private static string PrepareCookie(string raw)
    {
        var cookie = raw.Replace("iphone", "android").Replace("iPhone", "Android");
        cookie = SetCookieValue(cookie, "smzdm_version", AppVersion);
        cookie = SetCookieValue(cookie, "device_smzdm_version", AppVersion);
        cookie = SetCookieValue(cookie, "v", AppVersion);
        cookie = SetCookieValue(cookie, "device_smzdm_version_code", AppVersionRev);
        cookie = SetCookieValue(cookie, "device_system_version", "10.0");
        cookie = SetCookieValue(cookie, "apk_partner_name", "smzdm_download");
        cookie = SetCookieValue(cookie, "partner_name", "smzdm_download");
        cookie = SetCookieValue(cookie, "device_type", "Android");
        cookie = SetCookieValue(cookie, "device_smzdm", "android");
        cookie = SetCookieValue(cookie, "device_name", "Android");
        return cookie;
    }

    private static string SetCookieValue(string cookie, string name, string value)
    {
        var parts = cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            if (parts[i][..eq] != name) continue;
            parts[i] = $"{name}={value}";
            return string.Join("; ", parts);
        }
        parts.Add($"{name}={value}");
        return string.Join("; ", parts);
    }

    private static string TouchstoneEvent(JObject? extra)
    {
        if (extra == null || extra.Count == 0) return TouchstoneBase;
        var merged = JObject.Parse(TouchstoneBase);
        merged.Merge(extra);
        return merged.ToString(Formatting.None);
    }

    // ================================ HTTP 基础设施 ================================

    /// <summary>App 接口请求：默认走 sign 签名（zhiyou/m 网页接口传 sign:false + web:true）。</summary>
    private async Task<JObject?> ApiAsync(QuantumTaskContext ctx, HttpMethod method, string url,
        Dictionary<string, string>? data, bool sign, CancellationToken ct, bool web = false,
        string? referer = null, string? origin = null)
    {
        await Task.Delay(Random.Shared.Next(1500, 3000), ct); // 请求节流：随机 1.5~3s，防匀速特征
        var parameters = data == null ? new Dictionary<string, string>() : new Dictionary<string, string>(data);
        if (sign) parameters = SignFormData(parameters);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", web ? WebUa : AppUa);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-Hans-CN;q=1");
        request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        if (!web)
            request.Headers.TryAddWithoutValidation("request_key",
                Random.Shared.NextInt64(100_000_000_000_000_000, 900_000_000_000_000_000).ToString());
        if (method == HttpMethod.Get)
        {
            if (parameters.Count > 0)
                request.RequestUri = new Uri(url + (url.Contains('?') ? "&" : "?") + BuildQuery(parameters));
        }
        else
        {
            request.Content = new FormUrlEncodedContent(parameters);
        }
        if (referer != null) request.Headers.TryAddWithoutValidation("Referer", referer);
        if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await ctx.Http.SendAsync(request, ct);
        return TryParse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>网页接口/HTML 请求：WebView UA，无签名（转盘/幸运屋/众测）。</summary>
    private async Task<string> WebAsync(QuantumTaskContext ctx, HttpMethod method, string url,
        Dictionary<string, string>? data, CancellationToken ct, string? referer = null, string? origin = null,
        bool xRequested = false)
    {
        await Task.Delay(Random.Shared.Next(1500, 3000), ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", WebUa);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-Hans-CN;q=1");
        request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        if (xRequested) request.Headers.TryAddWithoutValidation("x-requested-with", "com.smzdm.client.android");
        if (referer != null) request.Headers.TryAddWithoutValidation("Referer", referer);
        if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (data != null && data.Count > 0)
        {
            if (method == HttpMethod.Get)
                request.RequestUri = new Uri(url + (url.Contains('?') ? "&" : "?") + BuildQuery(data));
            else
                request.Content = new FormUrlEncodedContent(data);
        }
        using var response = await ctx.Http.SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ================================ 工具 ================================

    private static int ErrorCode(JObject? obj) =>
        obj == null ? -1 : int.TryParse(obj["error_code"]?.ToString(), out var code) ? code : -1;

    private static string MatchCookie(string cookie, string name) =>
        Regex.Match(cookie, name + "=([^;]*)").Groups[1].Value.Trim();

    private static string Random32()
    {
        const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
        var sb = new StringBuilder();
        for (var i = 0; i < 32; i++) sb.Append(chars[Random.Shared.Next(chars.Length)]);
        return sb.ToString();
    }

    private static string StripHtml(string? text) =>
        string.IsNullOrEmpty(text) ? "" : Regex.Replace(text, "<[^>]+>", "").Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static Task DelayAsync(int minSeconds, int maxSeconds, CancellationToken ct) =>
        Task.Delay(Random.Shared.Next(minSeconds * 1000, maxSeconds * 1000), ct);

    private static JObject? ParseJsonp(string text)
    {
        var start = text.IndexOf('(');
        var end = text.LastIndexOf(')');
        return start >= 0 && end > start ? TryParse(text[(start + 1)..end]) : TryParse(text);
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static HashSet<string> ParseTasks(string? conf, string[] defaults)
    {
        if (string.IsNullOrWhiteSpace(conf)) return [.. defaults];
        return [.. conf.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static int ParseInt(string? value) => int.TryParse((value ?? "").Trim(), out var v) ? v : 0;

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
        if (ctx.EnablePush) await ctx.Notify.SendAsync("什么值得买", content, ct);
        else ctx.Log($"（推送关闭）什么值得买：{content}");
    }
}
