namespace Quantum.Entities.DTOs;

/// <summary>
/// 第三方受限富文本推送请求体（G-Push V1）。
/// 服务端不接受客户端指定来源、会话键、分类、跳转地址与接收用户——这些一律由平台侧决定。
/// </summary>
public class ExternalPushMessageRequest
{
    /// <summary>单条通知标题（必填，trim 后 ≤100，不含换行）。与会话标题是两个独立概念，不得互换。</summary>
    public string Title { get; set; }

    /// <summary>会话标题（可选，1~80；缺省取接入方展示名）。同接入方同标题归同一会话，改标题即新会话。</summary>
    public string SessionTitle { get; set; }

    /// <summary>正文（必填，≤4096 字符）：项目现有受限富文本标记，不是 HTML/Markdown。</summary>
    public string Content { get; set; }
}

/// <summary>推送受理结果：只表示**已持久化**，不代表设备已收到或已展示。</summary>
public class ExternalPushMessageResult
{
    public string NotificationId { get; set; }
    public string MsgId { get; set; }
    public string SessionKey { get; set; }

    /// <summary>true = 同幂等键同内容的重放，本次未重复广播</summary>
    public bool Duplicate { get; set; }
}

/// <summary>凭据创建结果：明文密钥只在此处回显一次。</summary>
public class ExternalPushCredentialCreated
{
    public string Id { get; set; }
    public string DisplayName { get; set; }

    /// <summary>明文密钥（仅创建/轮换响应出现一次，列表与日志永不含）</summary>
    public string Secret { get; set; }

    public bool Enabled { get; set; }
    public int RateLimitPerMinute { get; set; }
    public int DailyQuota { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>凭据列表项（元数据，无密钥无摘要）。</summary>
public class ExternalPushCredentialRow
{
    public string Id { get; set; }
    public string DisplayName { get; set; }
    public bool Enabled { get; set; }
    public int RateLimitPerMinute { get; set; }
    public int DailyQuota { get; set; }
    public string Remark { get; set; }
    public long TotalSent { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}
