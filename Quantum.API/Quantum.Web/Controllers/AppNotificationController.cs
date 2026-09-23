using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// App 站内通知：列表/未读数/已读标记（通知落库与推送由 AppPushService.SendNotificationAsync 编排）。
/// 单管理员体系：管理员专属。
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class AppNotificationController : BaseController
{
    private readonly AppNotificationService _notificationService;

    public AppNotificationController(AppNotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    /// <summary>
    /// 我的通知列表
    /// </summary>
    /// <param name="page">页码</param>
    /// <param name="pageSize">页大小</param>
    /// <param name="unreadOnly">仅未读</param>
    /// <returns></returns>
    [HttpGet("list")]
    public Task<PageResult<AppNotificationModel>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] bool unreadOnly = false)
    {
        return _notificationService.ListAsync(page, pageSize, unreadOnly);
    }

    /// <summary>
    /// 未读通知数
    /// </summary>
    [HttpGet("unread-count")]
    public Task<long> UnreadCount()
    {
        return _notificationService.UnreadCountAsync();
    }

    /// <summary>
    /// 标记已读（不传 id 则全部已读）
    /// </summary>
    [HttpPost("read")]
    public Task<bool> MarkRead([FromQuery] string id)
    {
        return _notificationService.MarkReadAsync(id);
    }
}
