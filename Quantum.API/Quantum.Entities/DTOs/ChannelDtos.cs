namespace Quantum.Entities.DTOs;

/// <summary>仅管理端可访问的脱敏状态；禁止返回令牌和回复上下文。</summary>
public sealed class ChannelStatusDto
{
    public string Platform { get; set; }
    public bool Configured { get; set; }
    public bool Enabled { get; set; }
    public string BotIdMasked { get; set; }
    public string BoundPeerMasked { get; set; }
    public string CandidatePeerMasked { get; set; }
    public string CandidateFingerprint { get; set; }
    public bool HasActiveBinding { get; set; }
    public DateTime? ChallengeExpiresAtUtc { get; set; }
    public string LastErrorCode { get; set; }
    public int PendingOutboxCount { get; set; }
    /// <summary>QQ openapi 接入点的环境标识（正式/沙箱），非敏感信息，供管理端提示 IP 白名单等限制。</summary>
    public string Environment { get; set; }
}

public sealed class QqChannelSaveDto
{
    public string AppId { get; set; }
    public string AppSecret { get; set; }
    public bool ConfirmRebind { get; set; }
    /// <summary>openapi 接入点；留空按 Environment 取内置默认（正式 api.bot.qq.com / 沙箱 sandbox.api.sgroup.qq.com）。</summary>
    public string ApiBase { get; set; }
    /// <summary>Sandbox=沙箱（不受 IP 白名单限制），其他值按正式环境处理。</summary>
    public string Environment { get; set; }
}

public sealed class ChannelToggleDto
{
    public bool Enabled { get; set; }
}

public sealed class ChannelConfirmDto
{
    public bool Confirm { get; set; }
}

public sealed class QqChallengeDto
{
    public string Code { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class FeishuChannelSaveDto
{
    public string AppId { get; set; }
    public string AppSecret { get; set; }
    public bool ConfirmRebind { get; set; }
}

public sealed class FeishuChallengeDto
{
    public string Code { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class FeishuQrStartRequestDto
{
    public bool ConfirmRebind { get; set; }
}

/// <summary>QrUrl 是飞书返回的 verification_uri_complete，前端渲染成二维码由飞书 App 扫码。</summary>
public sealed class FeishuQrStartDto
{
    public string SessionId { get; set; }
    public string QrUrl { get; set; }
    public int IntervalSeconds { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class FeishuQrPollDto
{
    public string SessionId { get; set; }
}

/// <summary>Status：waiting / confirmed / expired / failed；不回传 AppSecret。</summary>
public sealed class FeishuQrStatusDto
{
    public string Status { get; set; }
    public string Error { get; set; }
    public string ChallengeCode { get; set; }
    public string CandidatePeerMasked { get; set; }
    public string CandidateFingerprint { get; set; }
}

/// <summary>二维码内容仅发给登录账号，不含确认后的 bot_token。</summary>
public sealed class WeixinQrStartDto
{
    public string SessionId { get; set; }
    public string QrContent { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class WeixinQrStatusDto
{
    public string Status { get; set; }
    public string ChallengeCode { get; set; }
    public string CandidatePeerMasked { get; set; }
    public string CandidateFingerprint { get; set; }
}

public sealed class WeixinQrPollDto
{
    public string SessionId { get; set; }
    public string VerifyCode { get; set; }
}

/// <summary>出站投递状态（接口受理 ≠ 用户已读）；不包含 peer/token/消息正文。</summary>
public sealed class ChannelDeliveryDto
{
    public string Id { get; set; }
    public string Platform { get; set; }
    public string Status { get; set; }
    public string Purpose { get; set; }
    public string ErrorCode { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
}

/// <summary>扫码前由登录账号确认兼容版本；这些字段仅是协议元数据，不冒用 OpenClaw 身份。</summary>
public sealed class WeixinQrStartRequestDto
{
    public bool ConfirmRebind { get; set; }
    public string ClientVersion { get; set; }
    public string ChannelVersion { get; set; }
}

/// <summary>安全纯文本快捷回复的管理选择器；系统命令/脚本任务不出现在本列表。</summary>
public sealed class ChannelQuickReplyDto
{
    public string Id { get; set; }
    public string Key { get; set; }
    public bool Selected { get; set; }
}

public sealed class ChannelQuickReplySaveDto
{
    public List<string> CommandIds { get; set; } = [];
}
