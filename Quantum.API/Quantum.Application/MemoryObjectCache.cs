using Quantum.Entities.DTOs;
using System.Collections.Concurrent;

namespace Quantum.Application;

public static class MemoryObjectCache
{
    static MemoryObjectCache()
    {
        TaskCommandSteps = new ConcurrentDictionary<string, TaskCommandStep>();
        IpLimit = new ConcurrentDictionary<string, DateTime>();
        LoginFailedCounts = new ConcurrentDictionary<string, int>();
    }

    /// <summary>
    /// App 上传限流：每用户每分钟 20 次（固定窗口；AppUploadService 专用）
    /// </summary>
    public const int AppUploadLimitPerMinute = 20;

    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count, bool Granted)> AppUploadWindows = new();

    /// <summary>
    /// 申请一次 App 上传配额：该用户本分钟内不足上限返回 true。
    /// §1-10：原工厂改写捕获变量 granted（被覆盖的乐观调用会留下错误终值），
    /// 改由值元组携带本次放行判定，取回最终发布元组的 Granted。
    /// </summary>
    public static bool TryAcquireAppUploadSlot(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }
        var now = DateTime.Now;
        var windowStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        var (_, _, granted) = AppUploadWindows.AddOrUpdate(userId,
            _ => (windowStart, 1, true),
            (_, window) =>
            {
                if (window.WindowStart != windowStart)
                {
                    return (windowStart, 1, true);
                }
                if (window.Count >= AppUploadLimitPerMinute)
                {
                    return (window.WindowStart, window.Count, false);
                }
                return (window.WindowStart, window.Count + 1, true);
            });
        return granted;
    }

    public static ConcurrentDictionary<string, TaskCommandStep> TaskCommandSteps { get; set; }

    /// <summary>
    /// 清扫过期会话步骤（§1-5）：按 ForceEndTime 判定，不再依赖「更新时间 + 固定窗口」——
    /// ForceEndTime 已随步骤配置的 WaitTime 计算，语义准确。由消息内联清理与定时巡检共用，返回清理条数。
    /// </summary>
    public static int SweepExpiredSteps()
    {
        var now = DateTime.Now;
        var removed = 0;
        foreach (var (key, step) in TaskCommandSteps)
        {
            if (step.ForceEndTime <= now && TaskCommandSteps.TryRemove(key, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    public static ConcurrentDictionary<string, DateTime> IpLimit { get; set; }

    /// <summary>
    /// AppKey 授权通道的记账账号名（该通道无用户名维度，独立记账）。
    /// </summary>
    public const string AppKeyAuthAccount = "__open_appkey__";

    /// <summary>
    /// 按「IP|账号」记录的连续登录/授权失败次数。
    /// 仅按 IP 记账时，攻击者持任意合法账号登录成功即可清空该 IP 全部失败计数，
    /// 借此绕过对其他账号（如管理员口令）的线性退避——失败按账号维度记账、IP 维度取总和，
    /// 某账号登录成功只清除它自己的失败记录。
    /// </summary>
    public static ConcurrentDictionary<string, int> LoginFailedCounts { get; set; }

    private static string FailKey(string ip, string userName)
    {
        return $"{ip}|{(userName ?? string.Empty).Trim().ToLowerInvariant().Replace('|', '_')}";
    }

    private static string IpPrefix(string ip)
    {
        return $"{ip}|";
    }

    /// <summary>
    /// 登记失败计数：线程安全自增（按 IP+账号）。
    /// </summary>
    public static int AddLoginFailed(string ip, string userName = null)
    {
        return LoginFailedCounts.AddOrUpdate(FailKey(ip, userName), 1, (_, count) => count + 1);
    }

    /// <summary>
    /// 取该 IP 的当前失败计数（该 IP 下全部账号之和，驱动线性退避）。
    /// </summary>
    public static int GetLoginFailed(string ip)
    {
        var prefix = IpPrefix(ip);
        return LoginFailedCounts.Where(n => n.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(n => n.Value);
    }

    /// <summary>
    /// 登录/授权成功后清除「该账号」在该 IP 下的失败记录；
    /// 该 IP 已无任何账号的失败记录时同步解除 IP 层限流。
    /// </summary>
    public static void ResetLoginLimit(string ip, string userName = null)
    {
        LoginFailedCounts.TryRemove(FailKey(ip, userName), out _);
        if (GetLoginFailed(ip) == 0)
        {
            IpLimit.TryRemove(ip, out _);
        }
    }

    /// <summary>
    /// 限流字典只增不减会缓慢泄漏，达到阈值时顺手清理 1 小时前的过期项。
    /// </summary>
    public static void PruneStaleLimits()
    {
        if (IpLimit.Count < 4096 && LoginFailedCounts.Count < 4096 && AppUploadWindows.Count < 4096)
        {
            return;
        }
        var cutoff = DateTime.Now.AddHours(-1);
        foreach (var kv in IpLimit)
        {
            if (kv.Value < cutoff)
            {
                IpLimit.TryRemove(kv.Key, out _);
            }
        }
        foreach (var ip in LoginFailedCounts.Keys)
        {
            if (!IpLimit.ContainsKey(ip))
            {
                LoginFailedCounts.TryRemove(ip, out _);
            }
        }
        foreach (var kv in AppUploadWindows)
        {
            if (AppUploadWindows.TryGetValue(kv.Key, out var window) && window.WindowStart < cutoff)
            {
                AppUploadWindows.TryRemove(kv.Key, out _);
            }
        }
    }
}
