using System.Collections.Concurrent;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 扫码登录管理后台（A5.5 P2）：
/// 1. Web 登录页 POST api/Login/qr-create 换 ticket（匿名，2 分钟有效）并展示二维码；
/// 2. Web 轮询 GET api/Login/qr-status?ticket=（匿名）；
/// 3. 已登录的 App 管理员（Manager 令牌）扫得 ticket 后 POST api/App/scan-login 授权；
/// 4. qr-status 检测到授权即取回 Web 管理端令牌（票据一次性，取后即焚）。
/// </summary>
public class QrLoginService
{
    /// <summary>
    /// 票据有效期（分钟）
    /// </summary>
    public const int TicketLifetimeMinutes = 2;

    public enum TicketStatus
    {
        /// <summary>等待 App 扫码</summary>
        Pending = 0,
        /// <summary>App 已授权（可取走令牌）</summary>
        Authorized = 1,
        /// <summary>过期/已取用</summary>
        Expired = 2
    }

    private class TicketState
    {
        public DateTime CreateTime { get; init; } = DateTime.Now;
        public volatile string WebToken;
    }

    private static readonly ConcurrentDictionary<string, TicketState> Tickets = new();

    /// <summary>
    /// 票据读改锁：Poll 的「查 WebToken → 取走即焚」与 Authorize 的挂载必须互斥，
    /// 否则两个并发轮询可同取一枚令牌，破坏一次性语义
    /// </summary>
    private static readonly object TicketLock = new();

    /// <summary>
    /// Web 侧创建登录票据
    /// </summary>
    public (string ticket, DateTime expireAt) Create()
    {
        Prune();
        var ticket = Guid.NewGuid().ToString("N");
        Tickets[ticket] = new TicketState();
        return (ticket, DateTime.Now.AddMinutes(TicketLifetimeMinutes));
    }

    /// <summary>
    /// Web 侧轮询：status + 一次性令牌（授权后首次查询取走并作废票据）
    /// </summary>
    public (TicketStatus status, string token) Poll(string ticket)
    {
        lock (TicketLock)
        {
            Prune();
            if (string.IsNullOrEmpty(ticket) || !Tickets.TryGetValue(ticket, out var state))
            {
                return (TicketStatus.Expired, null);
            }
            if (state.WebToken != null)
            {
                // 一次性：取走即焚
                Tickets.TryRemove(ticket, out _);
                return (TicketStatus.Authorized, state.WebToken);
            }
            return (state.CreateTime.AddMinutes(TicketLifetimeMinutes) < DateTime.Now
                ? TicketStatus.Expired
                : TicketStatus.Pending, null);
        }
    }

    /// <summary>
    /// App 侧授权：仅 Manager 令牌（控制器保证）把 Web 管理端令牌挂到票据上
    /// </summary>
    public bool Authorize(string ticket, string webToken)
    {
        lock (TicketLock)
        {
            Prune();
            if (string.IsNullOrEmpty(ticket) || !Tickets.TryGetValue(ticket, out var state))
            {
                return false;
            }
            if (state.CreateTime.AddMinutes(TicketLifetimeMinutes) < DateTime.Now)
            {
                Tickets.TryRemove(ticket, out _);
                return false;
            }
            state.WebToken = webToken;
            return true;
        }
    }

    private static void Prune()
    {
        if (Tickets.Count < 4096)
        {
            return;
        }
        foreach (var kv in Tickets.Where(n => n.Value.CreateTime.AddMinutes(TicketLifetimeMinutes) < DateTime.Now).ToList())
        {
            Tickets.TryRemove(kv.Key, out _);
        }
    }
}
