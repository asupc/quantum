using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 第三方受限富文本消息推送（G-Push）。鉴权只认 <c>Authorization: PushKey {id}.{secret}</c>，
/// 不接受 JWT/匿名/Open AppKey；本端点不能执行指令、脚本或任何管理操作。
/// 响应恒 HTTP 200，成功 Data 表示**已持久化**（不代表设备已收到），第三方必须判断信封 Code。
/// </summary>
[PushKeyAuth]
public class ExternalPushController : BaseController
{
    readonly ExternalPushService _service;

    public ExternalPushController(ExternalPushService service)
    {
        _service = service;
    }

    /// <summary>幂等头（8~128 个 ASCII 可打印字符且无空白）</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>推送一条受限富文本通知到当前管理员设备，并为其指定独立会话标题。</summary>
    [HttpPost("messages")]
    public async Task<ExternalPushMessageResult> SendMessage([FromBody] ExternalPushMessageRequest request)
    {
        if (request == null)
        {
            throw new Utils.BusinessException("请求体不能为空");
        }

        var credential = HttpContext.Items[PushKeyAuthAttribute.CredentialItemKey] as ExternalPushCredentialModel;
        if (credential == null)
        {
            // 理论不可达（鉴权过滤器已拦），保留兜底：绝不在没有凭据身份的情况下投递
            throw new Utils.BusinessException("推送凭据无效");
        }

        var bytes = ExternalPushService.ByteLength(request);
        var outcome = await _service.SendAsync(credential, request.Title, request.SessionTitle, request.Content,
            Request.Headers[IdempotencyHeader].ToString(), bytes);

        return new ExternalPushMessageResult
        {
            NotificationId = outcome.NotificationId,
            MsgId = outcome.MsgId,
            SessionKey = outcome.SessionKey,
            Duplicate = outcome.Duplicate
        };
    }
}
