using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;

namespace Quantum.Application;

/// <summary>
/// App 站内通知查询服务：通知的落库与推送统一由 <see cref="AppPushService.SendNotificationAsync"/>
/// 编排（在线 WS 直推 → 离线厂商推送，无论在线与否均落 t_app_notification），
/// 这里负责通知中心列表、未读数与已读标记。
/// </summary>
public class AppNotificationService
{
    private readonly IQuantumDbContext _dbContext;

    public AppNotificationService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 通知列表（可只看未读）
    /// </summary>
    public async Task<PageResult<AppNotificationModel>> ListAsync(int page, int pageSize, bool unreadOnly)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _dbContext.AppNotifications.AsNoTracking();
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }
        // §2-7：total 必须在过滤之后统计——旧实现在 unreadOnly 之前 Count，未读页会带出全部通知的总数、页数失真
        var total = await query.CountAsync();
        var data = await query.OrderByDescending(n => n.CreatedAt)
            .ThenBy(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PageResult<AppNotificationModel>
        {
            Data = data,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 未读通知数
    /// </summary>
    public async Task<long> UnreadCountAsync()
    {
        return await _dbContext.AppNotifications.AsNoTracking()
            .Where(n => n.ReadAt == null)
            .LongCountAsync();
    }

    /// <summary>
    /// 标记已读（单条或全部）
    /// </summary>
    public async Task<bool> MarkReadAsync(string id)
    {
        var query = _dbContext.AppNotifications.Where(n => n.ReadAt == null);
        if (!string.IsNullOrEmpty(id))
        {
            query = query.Where(n => n.Id == id);
        }
        await query.ExecuteUpdateAsync(n => n.SetProperty(t => t.ReadAt, DateTime.Now));
        return true;
    }
}
