// ============================================================================
// qb 慢速/死种磁力自动转飞牛迅雷（2026-10-12）：定时巡检 qBittorrent 未完成种子，
// 「持续低速」或「完全死种」的磁力自动提交到飞牛迅雷下载应用，提交成功后用 qB 自带删除功能
// 删掉 qB 中的种子及文件（torrents/delete + deleteFiles=true）。
// 判定口径（默认值经需求确认，均可用环境变量覆盖）：
//   低速：采样窗口平均速度（本次 total_downloaded 增量 / 距上次采样耗时）低于阈值，持续 qbSlowMinutes 分钟（默认 60）；
//   死种：qB 报告种源总数 num_complete=0（-1 未知不算死），持续 qbDeadMinutes 分钟（默认 30）。
// 采样状态存自定义数据 qb_xunlei_transfer，每轮「删旧 id + 加新行」整批快照写回（规避同轮 Update 主键冲突）。
// 监控范围：state ∈ metaDL/downloading/stalledDL/forcedDL 且未完成——用户暂停/排队/出错/做种不转移
//   （qB 无法区分磁力与种子文件来源，按全部下载中任务监控；转走的种子若被手动重新添加，按用户意图放弃清理）。
// 飞牛迅雷 API（飞牛应用中心迅雷 = 迅雷 NAS 版容器，接口为社区逆向口径，迅雷升级可能变动）：
//   {xlURL}/webman/3rdparty/pan-xunlei-com/index.cgi/ 首页提 pan_auth → device/info/watch 取设备ID
//   → drive/v1/resource/list 服务端解析磁力文件清单 → drive/v1/task 建任务（sub_file_index=--1 全部文件）。
// 环境变量：qBittorrentURL（必填，如 http://192.168.x.x:8080，带 http:// 或 https:// 前缀）
//   xlURL（必填，飞牛迅雷直连地址如 http://192.168.x.x:2345——飞牛「应用中心 → 迅雷」详情页看访问端口；
//     与 qBittorrentURL 一样属内网地址，需先在「系统设置 → 任务外部请求内网白名单」登记，否则 ctx.Http 拒绝）
//   xlSavePath（必填，迅雷保存目录 = 迅雷 App 新建任务页里选择的路径，如 /downloads/）
//   qbusername / qbpassword（可选；缺失则跳过登录，需在 qB 设置>Web UI 中对量子所在 IP 放行或关闭身份校验）
//   qbSID（自动维护，勿手改：qB 登录会话缓存，与 qBittorrentAdd 等任务共用，12 小时内免登录复用）
//   xlToken（可选，迅雷前端加了 Basic 认证时填 Authorization 头值，如 Basic xxx；飞牛直连端口一般不需要）
//   qbSlowSpeed（可选，低速阈值 KB/s，默认 100）/ qbSlowMinutes（可选，默认 60）/ qbDeadMinutes（可选，默认 30）
//   xlDailyLimit（可选，迅雷每日提交上限，默认 3——本版本迅雷每日仅 3 个下载任务；0 = 不限制）
//   xlQuota（自动维护，勿手改：当日已提交迅雷的次数，格式「yyyy-MM-dd|次数」，跨日自动清零）
// 每日额度：按 xlQuota 统计的当日已建任务数限流，额度用完的候选保留 qB 次日自动转移（不发冷却不重试）；
//   多个候选同轮触发时死种优先、再按触发时间先后排序，把额度花在最需要的资源上。
// 安全口径：只有迅雷建任务确认 HTTP 200 后才删 qB（含文件）；提交失败只记阶梯冷却重试（15→30→60→120→240 分钟封顶），
//   绝不删 qB；提交成功但 qB 删除失败 → 状态记 submitted 下轮只补删不重提，每笔转移都推送通知。
// ============================================================================
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class QBittorrentXunleiTransferTask : IQuantumTask
{
    private const string CustomDataType = "qb_xunlei_transfer";
    private const string QbSidEnv = "qbSID";
    private const string QuotaEnv = "xlQuota";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>采样断档上限：超过视为停机/停任务，重置基线与低速/死种计时。</summary>
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromMinutes(30);

    private static readonly string[] Titles =
        ["哈希", "名称", "状态", "上次采样", "上次字节", "低速起始", "死种起始", "失败冷却至", "迅雷任务ID", "提交时间", "连续失败"];

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var qbUrl = RequireVar(ctx, "qBittorrentURL").TrimEnd('/');
        var xlUrl = RequireVar(ctx, "xlURL").TrimEnd('/');
        var savePath = RequireVar(ctx, "xlSavePath");
        var slowBytes = Math.Max(1, OptInt(ctx, "qbSlowSpeed", 100)) * 1024L;
        var slowSpan = TimeSpan.FromMinutes(Math.Max(1, OptInt(ctx, "qbSlowMinutes", 60)));
        var deadSpan = TimeSpan.FromMinutes(Math.Max(1, OptInt(ctx, "qbDeadMinutes", 30)));

        await ctx.CustomData.SaveTitleAsync(CustomDataType, "qb转迅雷监控", Titles, ct);

        // ① qB 侧：登录 + 拉全量种子；失败抛异常让本轮运行标记失败（运行记录可见，不刷推送）
        var cookie = await GetCookieAsync(ctx, qbUrl, ct);
        var torrents = await GetTorrentsAsync(ctx, qbUrl, cookie, ct);
        if (torrents == null)
        {
            throw new Exception("无法拉取 qB 种子列表（详见任务日志），本轮监控中止");
        }
        var now = DateTime.Now;

        // ② 装载上轮状态；先处理「已提交待补删」的行（不重提迅雷，只补删 qB）
        var states = await LoadStatesAsync(ctx, ct);
        var byHash = torrents.ToDictionary(t => t.Hash, t => t);
        foreach (var pending in states.Values.Where(s => s.Status == "submitted").ToList())
        {
            if (!byHash.TryGetValue(pending.Hash, out var t))
            {
                ctx.Log($"[{pending.Name}] 已提交迅雷且 qB 中种子已不在，清理状态行。");
                states.Remove(pending.Hash);
                continue;
            }
            // 提交之后 added_on 变新 = 用户把同一磁力手动加回了 qB：尊重手动行为，放弃清理重新跟踪
            var submittedAt = pending.SubmittedAt ?? now;
            if (t.AddedOn > 0 && DateTimeOffset.FromUnixTimeSeconds(t.AddedOn).LocalDateTime > submittedAt.AddMinutes(1))
            {
                ctx.Log($"[{t.Name}] 该种子在提交迅雷之后被重新添加到 qB，按手动添加处理，放弃清理。");
                states.Remove(pending.Hash);
                continue;
            }
            ctx.Log($"[{t.Name}] 上轮已提交迅雷，本轮补删 qB 种子及文件。");
            if (await DeleteTorrentAsync(ctx, qbUrl, cookie, t.Hash, t.Name, ct))
            {
                await ctx.Notify.SendAsync("qBittorrent",
                    $"{QuantumText.Tag("green", "qB清理完成")} 《{t.Name}》上轮已转迅雷，本轮已删除 qB 任务及文件。", ct);
                states.Remove(pending.Hash);
            }
        }

        // ③ 对监控集逐个更新采样基线与低速/死种计时
        var monitor = torrents.Where(t => InMonitor(t) && !string.IsNullOrEmpty(t.MagnetUri)).ToList();
        foreach (var t in monitor)
        {
            if (!states.TryGetValue(t.Hash, out var s))
            {
                states[t.Hash] = new TrackState { Hash = t.Hash, Name = t.Name, LastSeen = now, LastBytes = t.TotalDownloaded };
                ctx.Log($"[{t.Name}] 开始跟踪（state={t.State}，累计已下载 {FmtSize(t.TotalDownloaded)}）。");
                continue;
            }
            if (s.Status == "submitted")
            {
                continue; // 已在提交/补删流程里，不再计时
            }
            var elapsed = now - s.LastSeen;
            if (elapsed <= TimeSpan.Zero || elapsed > MaxSampleGap || t.TotalDownloaded < s.LastBytes)
            {
                // 断档过久（停机/停任务）或字节数回退（种子被重加）：重置基线与计时，避免用坏窗口误判
                s.LowSince = null;
                s.DeadSince = null;
                s.Fails = 0;
                s.LastSeen = now;
                s.LastBytes = t.TotalDownloaded;
                continue;
            }
            var avg = (t.TotalDownloaded - s.LastBytes) / elapsed.TotalSeconds; // bytes/s
            s.LastSeen = now;
            s.LastBytes = t.TotalDownloaded;
            if (avg < slowBytes)
            {
                s.LowSince ??= now;
            }
            else
            {
                s.LowSince = null;
            }
            if (t.NumComplete == 0)
            {
                s.DeadSince ??= now;
            }
            else
            {
                s.DeadSince = null;
            }
            ctx.Log($"[{t.Name}] 窗口均速 {avg / 1024:F1} KB/s（阈值 {slowBytes / 1024} KB/s）"
                + $"，低速计时 {Min(now, s.LowSince)} 分钟，种源数 {t.NumComplete}，死种计时 {Min(now, s.DeadSince)} 分钟。");
        }

        // ④ 收集触发转移的候选（不在失败冷却期）
        var triggered = new List<(QbTorrent T, TrackState S, string Reason)>();
        foreach (var t in monitor)
        {
            var s = states[t.Hash];
            if (s.Status != "tracking" || (s.FailUntil is DateTime f && now < f))
            {
                continue;
            }
            var reason = TriggerReason(s, now, slowSpan, deadSpan);
            if (reason != null)
            {
                triggered.Add((t, s, reason));
            }
        }

        // ⑤ 转移：按每日额度排队（死种优先、触发早者优先）→ 建迅雷会话（每轮一次）→ 逐个「资源解析 → 建任务 → 删 qB」
        var used = 0;
        var usedAtStart = 0;
        var blockedCount = 0;
        if (triggered.Count > 0)
        {
            var dailyLimit = ReadDailyLimit(ctx);
            if (dailyLimit > 0)
            {
                usedAtStart = used = await LoadQuotaAsync(ctx, ct);
            }
            triggered.Sort((a, b) => Rank(a.S).CompareTo(Rank(b.S)));
            var quotaLeft = dailyLimit <= 0 ? int.MaxValue : Math.Max(0, dailyLimit - used);
            if (triggered.Count > quotaLeft)
            {
                blockedCount = triggered.Count - quotaLeft;
                var blockedNames = triggered.Skip(quotaLeft).Select(x => x.T.Name).ToList();
                ctx.Log($"今日迅雷额度已用 {used}/{dailyLimit}，{blockedCount} 个候选保留 qB 次日自动转移：{string.Join("、", blockedNames)}");
                triggered = triggered.Take(quotaLeft).ToList();
            }

            if (triggered.Count > 0)
            {
                XlSession xl = null;
                try
                {
                    xl = await OpenXlSessionAsync(ctx, xlUrl, ct);
                }
                catch (Exception e)
                {
                    ctx.Log($"迅雷会话建立异常：{Kind(e)} {e.Message}");
                }
                if (xl == null)
                {
                    foreach (var (t, s, _) in triggered)
                    {
                        Cool(ctx, s, now, t.Name, "无法建立迅雷会话，本轮未提交");
                    }
                    await ctx.Notify.SendAsync("qBittorrent",
                        $"{QuantumText.Tag("red", "转迅雷失败")} 无法建立迅雷会话（检查 xlURL 地址、内网白名单与迅雷是否在线），"
                        + $"本轮 {triggered.Count} 个候选全部保留 qB，按阶梯冷却自动重试。", ct);
                }
                else
                {
                    var failed = new List<string>();
                    foreach (var (t, s, reason) in triggered)
                    {
                        var result = await TryTransferAsync(ctx, xl, qbUrl, cookie, t, s, reason, savePath, now, ct);
                        if (result == TransferResult.Failed)
                        {
                            failed.Add(t.Name);
                            continue;
                        }
                        // 任务已在迅雷创建（Done / PendingDelete 均算），立即累加并回写当日额度
                        used++;
                        await SaveQuotaAsync(ctx, used, ct);
                        if (result == TransferResult.Done)
                        {
                            states.Remove(t.Hash); // 已转迅雷且 qB 已删，状态行随本轮写回收敛移除
                        }
                    }
                    if (failed.Count > 0)
                    {
                        await ctx.Notify.SendAsync("qBittorrent",
                            $"{QuantumText.Tag("orange", "转迅雷部分失败")} {failed.Count}/{triggered.Count} 个提交未成功，已保留 qB 稍后重试：\n"
                            + string.Join("\n", failed), ct);
                    }
                    else if (dailyLimit > 0 && usedAtStart < dailyLimit && used >= dailyLimit)
                    {
                        // 额度恰在本轮用满才推送（已满后的候选阻塞只记日志，避免每轮刷推送）
                        await ctx.Notify.SendAsync("qBittorrent",
                            $"{QuantumText.Tag("orange", "迅雷额度用完")} 今日已提交 {used}/{dailyLimit} 个任务"
                            + (blockedCount > 0 ? $"，{blockedCount} 个候选保留 qB 明日自动转移" : "") + "。", ct);
                    }
                }
            }
        }

        // ⑥ 清理已离开监控范围的计时行（完成/暂停/排队/出错/已被手动删除）：重新进入时重建基线，
        //    避免跨状态时段稀释窗口均速造成误判（submitted 行不受影响，继续走补删流程）
        var monitorHashes = new HashSet<string>(monitor.Select(t => t.Hash));
        foreach (var row in states.Values.Where(s => s.Status == "tracking" && !monitorHashes.Contains(s.Hash)).ToList())
        {
            ctx.Log($"[{row.Name}] 离开监控范围或已不在 qB，清除计时。");
            states.Remove(row.Hash);
        }

        // ⑦ 整批写回状态快照（删旧 id + 加新行，同轮不 Update）
        var oldIds = states.Values.Where(s => !string.IsNullOrEmpty(s.Id)).Select(s => s.Id).ToList();
        if (oldIds.Count > 0)
        {
            await ctx.CustomData.DeleteAsync(oldIds, ct);
        }
        var newRows = states.Values.Select(ToRow).ToList();
        if (newRows.Count > 0)
        {
            await ctx.CustomData.AddAsync(newRows, ct);
        }

        var low = monitor.Count(t => states.TryGetValue(t.Hash, out var s) && s.LowSince != null);
        var dead = monitor.Count(t => states.TryGetValue(t.Hash, out var s) && s.DeadSince != null);
        var limit = ReadDailyLimit(ctx);
        var quotaText = limit <= 0 ? "不限" : $"{used}/{limit}";
        ctx.Log($"本轮概况：qB 种子 {torrents.Count} 个，监控下载中 {monitor.Count} 个，"
            + $"低速计时 {low}，死种计时 {dead}，本轮尝试转移 {triggered.Count} 个，额度阻塞 {blockedCount} 个，今日迅雷 {quotaText}。");
    }

    // ---------------------------------------------------------------- 判定辅助

    /// <summary>监控集：未完成且处于下载类状态（用户暂停/排队/出错/做种不碰）。</summary>
    private static bool InMonitor(QbTorrent t)
        => t.Progress < 1 && t.State is "metaDL" or "downloading" or "stalledDL" or "forcedDL";

    /// <summary>转移触发原因：死种优先于低速；未达时长返回 null。</summary>
    private static string TriggerReason(TrackState s, DateTime now, TimeSpan slowSpan, TimeSpan deadSpan)
    {
        if (s.DeadSince is DateTime d && now - d >= deadSpan)
        {
            return $"死种 {(now - d).TotalMinutes:F0} 分钟（种源数 0）";
        }
        if (s.LowSince is DateTime l && now - l >= slowSpan)
        {
            return $"持续低速 {(now - l).TotalMinutes:F0} 分钟";
        }
        return null;
    }

    private static double Min(DateTime now, DateTime? since)
        => since is DateTime v ? (now - v).TotalMinutes : 0;

    /// <summary>候选排队优先级：死种（tier 0）优先于低速（tier 1），同层按触发时间先后。</summary>
    private static (int Tier, long Ticks) Rank(TrackState s)
    {
        if (s.DeadSince is DateTime d)
        {
            return (0, d.Ticks);
        }
        if (s.LowSince is DateTime l)
        {
            return (1, l.Ticks);
        }
        return (2, 0);
    }

    /// <summary>读每日限额（xlDailyLimit，默认 3=本版本迅雷每日任务数；0=不限制，负数按默认处理）。</summary>
    private static int ReadDailyLimit(QuantumTaskContext ctx)
    {
        var raw = Var(ctx, "xlDailyLimit");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 3;
        }
        if (int.TryParse(raw.Trim(), out var v) && v >= 0)
        {
            return v;
        }
        ctx.Log($"环境变量 xlDailyLimit={raw} 不是有效非负整数，按默认 3 处理。");
        return 3;
    }

    /// <summary>读当日已提交迅雷数（xlQuota=「yyyy-MM-dd|次数」，跨日自动清零；读不到按 0）。</summary>
    private static async Task<int> LoadQuotaAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            var row = (await ctx.Env.QueryAsync(QuotaEnv, null, ct)).FirstOrDefault();
            if (row == null || string.IsNullOrEmpty(row.Value))
            {
                return 0;
            }
            var parts = row.Value.Split('|');
            return parts.Length == 2 && parts[0] == DateTime.Now.ToString("yyyy-MM-dd")
                && int.TryParse(parts[1], out var n) && n > 0 ? n : 0;
        }
        catch (Exception e)
        {
            ctx.Log($"读取 {QuotaEnv} 当日额度缓存异常（按 0 处理）：{Kind(e)} {e.Message}");
            return 0;
        }
    }

    /// <summary>回写当日已提交迅雷数（同名覆盖；异常只记日志——宁可少计不阻断转移）。</summary>
    private static async Task SaveQuotaAsync(QuantumTaskContext ctx, int count, CancellationToken ct)
    {
        try
        {
            await ctx.Env.SaveAsync(QuotaEnv, $"{DateTime.Now:yyyy-MM-dd}|{count}",
                "迅雷当日提交数（任务自动维护，勿手改）", true, ct);
        }
        catch (Exception e)
        {
            ctx.Log($"写入 {QuotaEnv} 当日额度缓存异常（不影响本次转移）：{Kind(e)} {e.Message}");
        }
    }

    // ---------------------------------------------------------------- 转移流程

    /// <summary>单笔转移结果：Done=已转迅雷并删 qB；PendingDelete=已提交但 qB 删除失败待补删；Failed=记冷却保留 qB。</summary>
    private enum TransferResult { Done, PendingDelete, Failed }

    /// <summary>单笔转移：迅雷资源解析 → 建任务（HTTP 200 算成功）→ 删 qB 种子及文件。</summary>
    private static async Task<TransferResult> TryTransferAsync(QuantumTaskContext ctx, XlSession xl, string qbUrl, string cookie,
        QbTorrent t, TrackState s, string reason, string savePath, DateTime now, CancellationToken ct)
    {
        ctx.Log($"[{t.Name}] 触发转移（{reason}），开始提交迅雷：{t.MagnetUri}");

        // 1) 服务端解析磁力文件清单（取文件名/大小/数量供建任务）
        var (listStatus, listBody) = await XlSendAsync(ctx, xl, HttpMethod.Post, "/drive/v1/resource/list",
            JsonConvert.SerializeObject(new { page_size = 2000, urls = t.MagnetUri }), ct);
        if (listStatus != 200)
        {
            Cool(ctx, s, now, t.Name, $"迅雷解析磁力失败：HTTP {listStatus} {TrimBody(listBody)}");
            return TransferResult.Failed;
        }
        var leaves = ExtractLeaves(listBody);
        if (leaves.Count == 0)
        {
            Cool(ctx, s, now, t.Name, "迅雷未能解析出该磁力的文件清单（资源可能已失效）");
            return TransferResult.Failed;
        }
        var totalSize = leaves.Sum(l => l.Value<long?>("file_size") ?? 0);
        var firstName = leaves[0].Value<string>("name") ?? t.Name;
        var firstMime = leaves[0]["meta"]?.Value<string>("mime_type") ?? "";
        var firstId = leaves[0].Value<string>("id") ?? "";

        // 2) 建下载任务（全部文件；保存到 xlSavePath）
        var payload = new Dictionary<string, object>
        {
            ["type"] = "user#download-url",
            ["name"] = firstName,
            ["file_name"] = firstName,
            ["file_size"] = totalSize.ToString(CultureInfo.InvariantCulture),
            ["space"] = "device_id#" + xl.DeviceId,
            ["params"] = new Dictionary<string, object>
            {
                ["target"] = "device_id#" + xl.DeviceId,
                ["url"] = t.MagnetUri,
                ["total_file_count"] = leaves.Count.ToString(CultureInfo.InvariantCulture),
                ["parent_folder_id"] = "",
                ["parent_folder_path"] = savePath,
                ["sub_file_index"] = "--1",
                ["mime_type"] = firstMime,
                ["file_id"] = firstId,
            },
        };
        var (taskStatus, taskBody) = await XlSendAsync(ctx, xl, HttpMethod.Post, "/drive/v1/task",
            JsonConvert.SerializeObject(payload), ct);
        if (taskStatus != 200)
        {
            Cool(ctx, s, now, t.Name, $"迅雷建任务失败：HTTP {taskStatus} {TrimBody(taskBody)}");
            return TransferResult.Failed;
        }
        string taskId = null;
        try
        {
            var obj = JObject.Parse(taskBody);
            taskId = obj["task"]?["id"]?.ToString() ?? obj["id"]?.ToString();
        }
        catch (Exception e)
        {
            ctx.Log($"[{t.Name}] 迅雷建任务响应解析任务ID异常（不影响提交结果）：{Kind(e)} {e.Message}");
        }
        s.XlTaskId = taskId ?? "";
        s.SubmittedAt = now;
        s.Fails = 0; // 提交已确认成功，后续只差删 qB
        ctx.Log($"[{t.Name}] 迅雷任务已创建（ID：{s.XlTaskId}，{FmtSize(totalSize)} / {leaves.Count} 个文件，保存到 {savePath}）。");

        // 3) 提交确认成功，才用 qB 自带删除功能删种子及文件
        if (!await DeleteTorrentAsync(ctx, qbUrl, cookie, t.Hash, t.Name, ct))
        {
            s.Status = "submitted"; // 下轮只补删，不再重复提交迅雷
            await ctx.Notify.SendAsync("qBittorrent",
                $"{QuantumText.Tag("orange", "已提交迅雷")} 《{t.Name}》\n原因：{reason}\n"
                + $"大小：{FmtSize(totalSize)} / {leaves.Count} 个文件\n"
                + "qB 删除失败，下轮自动补删任务及文件。", ct);
            return TransferResult.PendingDelete;
        }
        await ctx.Notify.SendAsync("qBittorrent",
            $"{QuantumText.Tag("green", "已转迅雷")} 《{t.Name}》\n原因：{reason}\n"
            + $"大小：{FmtSize(totalSize)} / {leaves.Count} 个文件，保存到 {savePath}\n"
            + $"qB 任务及文件已删除；迅雷任务ID：{(string.IsNullOrEmpty(s.XlTaskId) ? "已创建" : s.XlTaskId)}", ct);
        return TransferResult.Done;
    }

    /// <summary>单笔失败记阶梯冷却（15→30→60→120→240 分钟封顶，避免迅雷长期异常时高频重试刷推送）。</summary>
    private static void Cool(QuantumTaskContext ctx, TrackState s, DateTime now, string name, string message)
    {
        s.Fails++;
        var minutes = (int)Math.Min(15L << Math.Min(s.Fails - 1, 4), 240L);
        s.FailUntil = now + TimeSpan.FromMinutes(minutes);
        ctx.Log($"[{name}] {message}；第 {s.Fails} 次失败，{minutes} 分钟后重试（保留 qB）。");
    }

    /// <summary>递归抽取迅雷 resource/list 响应里的文件叶子（is_dir 的走 dir.resources 下钻）。</summary>
    private static List<JToken> ExtractLeaves(string body)
    {
        var leaves = new List<JToken>();
        try
        {
            var resources = JObject.Parse(body)?["list"]?["resources"];
            Walk(resources, leaves);
        }
        catch (Exception)
        {
            // 解析异常按无清单处理，由调用方记失败
        }
        return leaves;
    }

    private static void Walk(JToken token, List<JToken> leaves)
    {
        if (token == null)
        {
            return;
        }
        foreach (var r in token)
        {
            var children = r.Value<bool?>("is_dir") == true ? r["dir"]?["resources"] : null;
            if (children != null)
            {
                Walk(children, leaves);
            }
            else
            {
                leaves.Add(r);
            }
        }
    }

    // ---------------------------------------------------------------- qB 侧

    private static async Task<List<QbTorrent>> GetTorrentsAsync(QuantumTaskContext ctx, string baseUrl, string cookie,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v2/torrents/info");
            if (!string.IsNullOrEmpty(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
            using var response = await ctx.Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                ctx.Log($"qB 拉取种子列表失败：HTTP {(int)response.StatusCode} {body.Trim()}");
                return null;
            }
            var list = JsonConvert.DeserializeObject<List<QbTorrent>>(body) ?? new List<QbTorrent>();
            return list.Where(t => !string.IsNullOrEmpty(t.Hash)).ToList();
        }
        catch (Exception e)
        {
            ctx.Log($"qB 拉取种子列表异常：{Kind(e)} {e.Message}");
            return null;
        }
    }

    /// <summary>qB 自带删除：torrents/delete + deleteFiles=true（种子与文件一起删）。</summary>
    private static async Task<bool> DeleteTorrentAsync(QuantumTaskContext ctx, string baseUrl, string cookie,
        string hash, string name, CancellationToken ct)
    {
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["hashes"] = hash,
                ["deleteFiles"] = "true",
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v2/torrents/delete") { Content = form };
            if (!string.IsNullOrEmpty(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
            using var response = await ctx.Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            ctx.Log($"qB 删除种子 [{name}]：HTTP {(int)response.StatusCode} {body.Trim()}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            ctx.Log($"qB 删除种子 [{name}] 异常：{Kind(e)} {e.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- qB 登录（与 qBittorrentAdd.cs 同口径的内联副本）

    private static string Var(QuantumTaskContext ctx, string name)
        => ctx.Variables.TryGetValue(name, out var value) ? value : null;

    /// <summary>取 qB 会话 Cookie：优先复用环境变量 qbSID 缓存（12 小时内先校验再复用），失效重登并回写。</summary>
    private static async Task<string> GetCookieAsync(QuantumTaskContext ctx, string baseUrl, CancellationToken ct)
    {
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
    private static async Task<string> LoginAsync(QuantumTaskContext ctx, string baseUrl, string user, string password,
        CancellationToken ct)
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

    /// <summary>从 Set-Cookie 响应头提取 SID 值（不含 SID= 前缀；不会误匹配 SID_30s）。</summary>
    private static string ExtractSidValue(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return string.Empty;
        }
        foreach (var header in values)
        {
            var match = Regex.Match(header, @"SID=([^; ]+)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        return string.Empty;
    }

    // ---------------------------------------------------------------- 迅雷侧（社区逆向接口）

    /// <summary>迅雷会话：cgi 基址 + 可选 Authorization + pan_auth + 设备ID。</summary>
    private sealed class XlSession
    {
        public string Cgi;
        public string Auth;
        public string PanToken;
        public string DeviceId;
    }

    /// <summary>建立迅雷会话：首页提 pan_auth（uiauth 函数，兜底 JWT 形态）→ device/info/watch 取设备ID。</summary>
    private static async Task<XlSession> OpenXlSessionAsync(QuantumTaskContext ctx, string xlUrl, CancellationToken ct)
    {
        var session = new XlSession
        {
            Cgi = xlUrl + "/webman/3rdparty/pan-xunlei-com/index.cgi",
            Auth = Var(ctx, "xlToken"),
        };

        using (var request = new HttpRequestMessage(HttpMethod.Get, session.Cgi + "/"))
        {
            request.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            if (!string.IsNullOrEmpty(session.Auth))
            {
                request.Headers.TryAddWithoutValidation("Authorization", session.Auth);
            }
            using var response = await ctx.Http.SendAsync(request, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            var match = Regex.Match(html, @"function uiauth\(value\)\{ return ""([^""]+)"" \}");
            if (!match.Success)
            {
                match = Regex.Match(html, @"[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+");
            }
            if (!match.Success)
            {
                ctx.Log($"迅雷首页未解析到 pan_auth（HTTP {(int)response.StatusCode}）：请核对 xlURL 是否为迅雷直连地址、迅雷是否已完成账号绑定。");
                return null;
            }
            session.PanToken = match.Groups[1].Value;
        }

        var (status, body) = await XlSendAsync(ctx, session, HttpMethod.Post, "/device/info/watch", "{}", ct);
        if (status != 200)
        {
            ctx.Log($"迅雷取设备ID失败：HTTP {status} {TrimBody(body)}");
            return null;
        }
        var target = JObject.Parse(body).Value<string>("target") ?? "";
        var idx = target.IndexOf('#');
        if (idx < 0 || idx + 1 >= target.Length)
        {
            ctx.Log($"迅雷设备信息响应缺少 target#设备ID：{TrimBody(body)}");
            return null;
        }
        session.DeviceId = target[(idx + 1)..];
        ctx.Log($"迅雷会话就绪（pan_auth 已取，设备ID {session.DeviceId[..Math.Min(8, session.DeviceId.Length)]}…）。");
        return session;
    }

    /// <summary>迅雷接口统一发送：pan_auth 走查询参数与请求头双通道（与官方前端一致）。</summary>
    private static async Task<(int Status, string Body)> XlSendAsync(QuantumTaskContext ctx, XlSession s,
        HttpMethod method, string path, string json, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method,
                $"{s.Cgi}{path}?pan_auth={Uri.EscapeDataString(s.PanToken)}&device_space=");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("pan-auth", s.PanToken);
            request.Headers.TryAddWithoutValidation("device-space", "");
            if (!string.IsNullOrEmpty(s.Auth))
            {
                request.Headers.TryAddWithoutValidation("Authorization", s.Auth);
            }
            if (json != null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            using var response = await ctx.Http.SendAsync(request, ct);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e)
        {
            ctx.Log($"迅雷请求 {path} 异常：{Kind(e)} {e.Message}");
            return (-1, e.Message);
        }
    }

    // ---------------------------------------------------------------- 状态与杂项

    private sealed class TrackState
    {
        public string Id;             // 上轮快照行 Id（写回时删除用）
        public string Hash = "";
        public string Name = "";
        public string Status = "tracking"; // tracking=计时中 / submitted=已提交迅雷待补删 qB
        public DateTime LastSeen;
        public long LastBytes;
        public DateTime? LowSince;
        public DateTime? DeadSince;
        public DateTime? FailUntil;
        public string XlTaskId = "";
        public DateTime? SubmittedAt;
        public int Fails;
    }

    /// <summary>qB /api/v2/torrents/info 条目（只取本任务用到的字段）。</summary>
    private sealed class QbTorrent
    {
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("state")] public string State { get; set; }
        [JsonProperty("progress")] public double Progress { get; set; }
        [JsonProperty("total_downloaded")] public long TotalDownloaded { get; set; }
        [JsonProperty("num_complete")] public long NumComplete { get; set; }
        [JsonProperty("magnet_uri")] public string MagnetUri { get; set; }
        [JsonProperty("added_on")] public long AddedOn { get; set; }
    }

    private static async Task<Dictionary<string, TrackState>> LoadStatesAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var map = new Dictionary<string, TrackState>();
        foreach (var r in await ctx.CustomData.QueryAsync(CustomDataType, ct: ct))
        {
            var hash = r.Data1 ?? "";
            if (string.IsNullOrEmpty(hash) || map.ContainsKey(hash))
            {
                continue;
            }
            map[hash] = new TrackState
            {
                Id = r.Id,
                Hash = hash,
                Name = r.Data2 ?? "",
                Status = string.IsNullOrEmpty(r.Data3) ? "tracking" : r.Data3,
                LastSeen = ParseTime(r.Data4),
                LastBytes = long.TryParse(r.Data5, out var bytes) ? bytes : 0,
                LowSince = ParseTimeNullable(r.Data6),
                DeadSince = ParseTimeNullable(r.Data7),
                FailUntil = ParseTimeNullable(r.Data8),
                XlTaskId = r.Data9 ?? "",
                SubmittedAt = ParseTimeNullable(r.Data10),
                Fails = int.TryParse(r.Data11, out var fails) ? fails : 0,
            };
        }
        return map;
    }

    private static QuantumCustomDataValue ToRow(TrackState s) => new()
    {
        Type = CustomDataType,
        Data1 = s.Hash,
        Data2 = s.Name,
        Data3 = s.Status,
        Data4 = s.LastSeen.ToString(TimeFormat),
        Data5 = s.LastBytes.ToString(CultureInfo.InvariantCulture),
        Data6 = Fmt(s.LowSince),
        Data7 = Fmt(s.DeadSince),
        Data8 = Fmt(s.FailUntil),
        Data9 = s.XlTaskId,
        Data10 = Fmt(s.SubmittedAt),
        Data11 = s.Fails.ToString(CultureInfo.InvariantCulture),
    };

    private static string Fmt(DateTime? time) => time?.ToString(TimeFormat) ?? "";

    private static DateTime ParseTime(string text)
        => DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : default;

    private static DateTime? ParseTimeNullable(string text)
        => string.IsNullOrEmpty(text) ? null : ParseTime(text);

    private static string TrimBody(string body)
        => body == null ? "" : (body.Length > 300 ? body[..300] + "…" : body);

    private static string FmtSize(long bytes)
    {
        double value = bytes;
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[i];
    }

    private static string RequireVar(QuantumTaskContext ctx, string name)
    {
        var value = Var(ctx, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new Exception($"缺少环境变量 {name}，请先在环境变量页配置");
        }
        return value.Trim();
    }

    private static int OptInt(QuantumTaskContext ctx, string name, int fallback)
    {
        var raw = Var(ctx, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        if (int.TryParse(raw.Trim(), out var v) && v > 0)
        {
            return v;
        }
        ctx.Log($"环境变量 {name}={raw} 不是有效正整数，按默认 {fallback} 处理。");
        return fallback;
    }

    /// <summary>异常类别名：等价 qBittorrentAdd 的 error.name 口径，不触碰门禁拦截的反射成员。</summary>
    private static string Kind(Exception e) => e switch
    {
        HttpRequestException => nameof(HttpRequestException),
        TaskCanceledException => nameof(TaskCanceledException),
        OperationCanceledException => nameof(OperationCanceledException),
        _ => nameof(Exception)
    };
}
