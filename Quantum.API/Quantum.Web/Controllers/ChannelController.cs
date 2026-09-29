using Microsoft.AspNetCore.Mvc;
using Quantum.Application.Channels;
using Quantum.Entities.DTOs;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>平台消息通道管理：登录账号可配置，外部聊天身份不获得登录能力。</summary>
[CustomAuthorizationFilter]
[LoggedInUser]
public sealed class ChannelController : BaseController
{
    private readonly ChannelManagementService _management;
    private readonly WeixinQrLoginService _qr;
    private readonly FeishuQrLoginService _feishuQr;

    public ChannelController(ChannelManagementService management, WeixinQrLoginService qr,
        FeishuQrLoginService feishuQr)
    {
        _management = management;
        _qr = qr;
        _feishuQr = feishuQr;
    }

    [HttpGet("status")]
    public Task<List<ChannelStatusDto>> Status() => _management.StatusAsync();

    [HttpGet("{platform}/delivery")]
    public Task<List<ChannelDeliveryDto>> Delivery([FromRoute] string platform)
        => _management.RecentDeliveryAsync(platform);

    [HttpPost("{platform}/delivery/{id}/retry")]
    [ActionLogFilter("人工重试平台回复")]
    public Task<ChannelDeliveryDto> RetryDelivery([FromRoute] string platform, [FromRoute] string id,
        [FromBody] ChannelConfirmDto model)
        => _management.RetryDeliveryAsync(platform, id, model?.Confirm ?? false);

    /// <summary>手动发送自检消息：目标固定取该平台最近一条有效原路回复路由，接口不接收任何收件人参数。</summary>
    [HttpPost("{platform}/test-send")]
    [ActionLogFilter("发送通道测试消息")]
    public Task<ChannelDeliveryDto> TestSend([FromRoute] string platform) => _management.TestSendAsync(platform);

    [HttpPost("qq/config")]
    [ActionLogFilter("配置QQ机器人")]
    public Task<ChannelStatusDto> ConfigureQq([FromBody] QqChannelSaveDto model)
        => _management.ConfigureQqAsync(model);

    [HttpPost("weixin/qr/start")]
    [ActionLogFilter("发起微信扫码绑定")]
    public Task<WeixinQrStartDto> StartWeixinQr([FromBody] WeixinQrStartRequestDto model, CancellationToken ct)
        => _qr.StartAsync(model, ct);

    [HttpPost("weixin/qr/status")]
    public Task<WeixinQrStatusDto> PollWeixinQr([FromBody] WeixinQrPollDto model, CancellationToken ct)
        => _qr.PollAsync(model?.SessionId, model?.VerifyCode, ct);

    [HttpPost("feishu/config")]
    [ActionLogFilter("配置飞书应用")]
    public Task<ChannelStatusDto> ConfigureFeishu([FromBody] FeishuChannelSaveDto model)
        => _management.ConfigureFeishuAsync(model);

    [HttpPost("feishu/qr/start")]
    [ActionLogFilter("发起飞书扫码绑定")]
    public Task<FeishuQrStartDto> StartFeishuQr([FromBody] FeishuQrStartRequestDto model, CancellationToken ct)
        => _feishuQr.StartAsync(model?.ConfirmRebind ?? false, ct);

    [HttpPost("feishu/qr/status")]
    public Task<FeishuQrStatusDto> PollFeishuQr([FromBody] FeishuQrPollDto model, CancellationToken ct)
        => _feishuQr.PollAsync(model?.SessionId, ct);

    [HttpPost("{platform}/unbind")]
    [ActionLogFilter("解绑消息通道")]
    public Task<ChannelStatusDto> Unbind([FromRoute] string platform, [FromBody] ChannelConfirmDto model)
        => _management.UnbindAsync(platform, model?.Confirm ?? false);
}
