using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 消息通知（管理员专用：单用户 App 会话消息入口，普通用户不开放群发能力）
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class NotifiyController : BaseController
{
    readonly NotifyService _notifyService;

    public NotifiyController(NotifyService notifyService)
    {
        _notifyService = notifyService;
    }

    /// <summary>
    /// 发送消息
    /// </summary>
    [HttpPost]
    public bool Send([FromBody] SendNotifyDTO sendNotify)
    {
        return _notifyService.Send(sendNotify);
    }
}
