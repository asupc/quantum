using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 外部推送接入凭据（G-Push）：第三方以 <c>Authorization: PushKey {Id}.{Secret}</c> 调用受限富文本推送 API。
///
/// 隔离口径：本凭据**不是** JWT，不携带任何身份/角色声明，绝不能抵达任务/日志/管理端点；
/// 旧 Open AppKey、普通用户令牌、Manager JWT 同样没有推送权限。明文密钥只在创建/轮换时回显一次，
/// 库里只存 SHA-256 摘要。
/// </summary>
[Table("t_external_push_credential")]
public class ExternalPushCredentialModel : BaseModel
{
    /// <summary>对外可见的接入方标识（放在 Authorization 头里，非秘密）</summary>
    public string DisplayName { get; set; }

    /// <summary>是否启用（吊销即置 false 并写 RevokedAtUtc）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>明文密钥的 SHA-256 摘要（大写十六进制，64 字符）；绝不存明文</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string SecretHash { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAtUtc { get; set; }

    /// <summary>到期时间（可空 = 长期有效，由管理员手工吊销）</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>每分钟配额（默认 60）</summary>
    public int RateLimitPerMinute { get; set; } = 60;

    /// <summary>每日配额（默认 1000）</summary>
    public int DailyQuota { get; set; } = 1000;

    /// <summary>备注（接入方/用途）</summary>
    public string Remark { get; set; }

    /// <summary>累计受理次数（仅计数，不含内容）</summary>
    public long TotalSent { get; set; }
}

/// <summary>
/// 外部推送幂等记录（G-Push）：唯一键 <c>(CredentialId, IdempotencyKey)</c>。
///
/// 过期口径（审核项 R-07）：本行**实际删除之前**，同键请求始终按原请求去重；
/// 清理任务删除后才允许键复用。这样唯一索引与「过期」判定不会互相打架。
/// </summary>
[Table("t_external_push_request")]
public class ExternalPushRequestModel : BaseModel
{
    [Column(TypeName = "nvarchar(64)")]
    public string CredentialId { get; set; }

    /// <summary>客户端提供的幂等键（8~128 个 ASCII 可打印字符、无空白）</summary>
    public string IdempotencyKey { get; set; }

    /// <summary>请求摘要 = SHA256(归一化 Title + SessionTitle + Content)，同键不同内容据此拒绝</summary>
    [Column(TypeName = "nvarchar(64)")]
    public string RequestHash { get; set; }

    [Column(TypeName = "nvarchar(64)")]
    public string NotificationId { get; set; }

    public string MsgId { get; set; }

    /// <summary>最终落到的会话键（external: 命名空间，不含明文标题）</summary>
    public string SessionKey { get; set; }

    /// <summary>请求体字节数（仅体积，不记内容）</summary>
    public int PayloadBytes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
