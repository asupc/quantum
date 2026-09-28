using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>通道账户：各平台至多一条；凭据为独立密钥加密的密文，禁用为默认态。</summary>
[Table("t_channel_account")]
public class ChannelAccountModel : BaseModel
{
    [Column(TypeName = "nvarchar(16)")]
    [Required]
    public string Platform { get; set; }

    [Column(TypeName = "nvarchar(128)")]
    [Required]
    public string BotId { get; set; }

    public bool Enabled { get; set; }
    public string CredentialCiphertext { get; set; }
    public int BindingVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // QQ 一次性挑战：只存摘要，候选 OpenID 等待 Web Manager 二次确认。
    [Column(TypeName = "nvarchar(64)")]
    public string ChallengeHash { get; set; }
    public DateTime? ChallengeExpiresAtUtc { get; set; }
    public int ChallengeAttempts { get; set; }
    [Column(TypeName = "nvarchar(128)")]
    public string CandidatePeerId { get; set; }
    public DateTime? CandidateAtUtc { get; set; }
    [Column(TypeName = "nvarchar(128)")]
    public string LastErrorCode { get; set; }
}

/// <summary>每平台唯一的已验证私聊绑定；版本用于阻止旧回复路由/待发消息跨换绑送达。</summary>
[Table("t_channel_binding")]
public class ChannelBindingModel : BaseModel
{
    [Column(TypeName = "nvarchar(16)")]
    [Required]
    public string Platform { get; set; }
    [Column(TypeName = "nvarchar(64)")]
    [Required]
    public string AccountId { get; set; }
    [Column(TypeName = "nvarchar(128)")]
    [Required]
    public string PeerId { get; set; }
    public int Version { get; set; }
    public bool Active { get; set; }
    public DateTime VerifiedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAtUtc { get; set; }
}

/// <summary>入站去重与执行状态；拒绝事件不存消息正文，执行中崩溃需人工核对 Unknown。</summary>
[Table("t_channel_inbox")]
public class ChannelInboxModel : BaseModel
{
    [Column(TypeName = "nvarchar(64)")]
    [Required]
    public string AccountId { get; set; }
    [Column(TypeName = "nvarchar(32)")]
    [Required]
    public string EventType { get; set; }
    [Column(TypeName = "nvarchar(191)")]
    [Required]
    public string MessageId { get; set; }
    [Column(TypeName = "nvarchar(160)")]
    [Required]
    public string MessageIndex { get; set; }
    [Column(TypeName = "nvarchar(128)")]
    public string PeerId { get; set; }
    public string Content { get; set; }
    [Column(TypeName = "nvarchar(24)")]
    [Required]
    public string Status { get; set; }
    [Column(TypeName = "nvarchar(80)")]
    public string RejectReason { get; set; }
    [Column(TypeName = "nvarchar(64)")]
    public string ReplyRouteId { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClaimedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int Attempt { get; set; }
}

/// <summary>网络读取检查点：QQ Session/s 与微信加密游标按账户分开。</summary>
[Table("t_channel_cursor")]
public class ChannelCursorModel : BaseModel
{
    [Column(TypeName = "nvarchar(64)")]
    [Required]
    public string AccountId { get; set; }
    [Column(TypeName = "nvarchar(128)")]
    public string QqSessionId { get; set; }
    public long? QqSequence { get; set; }
    public string WeixinCursorCiphertext { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>只读内部回复路由；外部 API/任务源码不接收平台凭据或上下文 token。</summary>
[Table("t_channel_reply_route")]
public class ChannelReplyRouteModel : BaseModel
{
    [Column(TypeName = "nvarchar(64)")]
    [Required]
    public string AccountId { get; set; }
    [Column(TypeName = "nvarchar(128)")]
    [Required]
    public string PeerId { get; set; }
    public int BindingVersion { get; set; }
    [Column(TypeName = "nvarchar(191)")]
    [Required]
    public string ReplyMessageId { get; set; }
    public int NextMessageSequence { get; set; } = 1;
    public string ContextTokenCiphertext { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>原路回复投递账本；平台“接口受理”与终端已读严格区分。</summary>
[Table("t_channel_outbox")]
public class ChannelOutboxModel : BaseModel
{
    [Column(TypeName = "nvarchar(64)")]
    [Required]
    public string AccountId { get; set; }
    [Column(TypeName = "nvarchar(64)")]
    public string ReplyRouteId { get; set; }
    [Column(TypeName = "nvarchar(128)")]
    [Required]
    public string PeerId { get; set; }
    public int BindingVersion { get; set; }
    public int MessageSequence { get; set; }
    [Column(TypeName = "nvarchar(32)")]
    [Required]
    public string Purpose { get; set; }
    [Required]
    public string Content { get; set; }
    [Column(TypeName = "nvarchar(24)")]
    [Required]
    public string Status { get; set; }
    [Column(TypeName = "nvarchar(64)")]
    [Required]
    public string ClientId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public int Attempts { get; set; }
    [Column(TypeName = "nvarchar(128)")]
    public string LastErrorCode { get; set; }
    [Column(TypeName = "nvarchar(191)")]
    public string PlatformMessageId { get; set; }
}

/// <summary>由 Manager 显式允许的纯文本快捷回复；不赋予聊天用户任务/脚本/系统命令权限。</summary>
[Table("t_channel_allowed_command")]
public class ChannelAllowedCommandModel : BaseModel
{
    [Column(TypeName = "nvarchar(64)")]
    [Required]
    public string AccountId { get; set; }
    [Column(TypeName = "nvarchar(64)")]
    [Required]
    public string CommandId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
