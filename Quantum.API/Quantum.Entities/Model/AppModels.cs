using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 安卓/客户端设备注册表：单管理员多设备（换机/多设备并存），按 DeviceId 唯一归属。
/// </summary>
[Table("t_app_device")]
public class AppDeviceModel : BaseModel
{
    /// <summary>
    /// 客户端生成的唯一设备号（首次安装生成，卸载重装换新）
    /// </summary>
    public string DeviceId { get; set; }

    /// <summary>
    /// 设备型号（如 Pixel 8 / MI 13）
    /// </summary>
    public string DeviceName { get; set; }

    /// <summary>
    /// 平台（android/ios/harmony）
    /// </summary>
    public string Platform { get; set; }

    /// <summary>
    /// 最近活跃时间
    /// </summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// 是否启用（用户可在 App 端解绑/禁用）
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 注册时间
    /// </summary>
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

/// <summary>
/// 刷新令牌：落库存 SHA-256 哈希，支持吊销与轮换（滑动窗口，天数见 Setting.RefreshTokenDays）。
/// </summary>
[Table("t_app_refresh_token")]
public class AppRefreshTokenModel : BaseModel
{
    /// <summary>
    /// 令牌哈希（SHA-256 Base64，原始令牌只在签发时返回一次）
    /// </summary>
    public string TokenHash { get; set; }

    /// <summary>
    /// 过期时间
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// 是否已吊销（登出/轮换/疑似泄露/改密/解绑）
    /// </summary>
    public bool Revoked { get; set; }

    /// <summary>
    /// 吊销原因（rotate/logout/unbind/password/leak，见 AppAuthService.RevokedReason* 常量）。
    /// 只有 rotate（轮换后的旧令牌重现）按疑似泄露全量吊销；
    /// 管理性吊销（解绑/改密）的令牌被客户端重试属正常时序，普通拒绝即可，不连带其他设备。
    /// 存量行为 NULL：按非轮换处理（宽容）。
    /// </summary>
    public string RevokedReason { get; set; }

    /// <summary>
    /// 签发时间
    /// </summary>
    public DateTime CreateTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 绑定设备（签发时写入）：刷新时校验一致，跨设备重放按疑似泄露处置。
    /// 存量行为 NULL（升级前的老令牌），NULL = 不绑定（宽容过渡，轮换后自然变绑定行）。
    /// </summary>
    public string DeviceId { get; set; }
}

/// <summary>
/// App 站内通知：任务完成/系统提醒等落库，App 在线 WS 直推或离线拉取。
/// </summary>
[Table("t_app_notification")]
public class AppNotificationModel : BaseModel
{
    /// <summary>
    /// 标题
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// 内容
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// 业务类型（task/system/security/...）
    /// </summary>
    public string Category { get; set; }

    /// <summary>
    /// 点按跳转路由（如 quantum://task/123，可空）
    /// </summary>
    public string Jump { get; set; }

    /// <summary>
    /// 消息id：WS 直推与厂商推送共用，客户端幂等去重（可空）
    /// </summary>
    public string MsgId { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 已读时间（null 未读）
    /// </summary>
    public DateTime? ReadAt { get; set; }
}

/// <summary>
/// 会话消息方向：发送 = 机器人 → 用户；接收 = 用户 → 机器人。
/// </summary>
public enum ChatMessageDirection
{
    发送 = 1,
    接收 = 2
}

/// <summary>
/// 会话消息投递状态：机器人下发的消息按 发送中 → 已送达 → 已读 推进；
/// 用户上报的消息直接落 已读。
/// </summary>
public enum ChatMessageStatus
{
    发送中 = 1,
    已送达 = 2,
    已读 = 3
}

/// <summary>
/// 会话消息持久化（管理员 ↔ 机器人单会话）：Seq 全局单调递增游标，支撑增量同步与历史漫游。
/// </summary>
[Table("t_chat_message")]
public class ChatMessageModel : BaseModel
{
    /// <summary>
    /// 全局递增序号（增量同步游标）
    /// </summary>
    public long Seq { get; set; }

    /// <summary>
    /// 方向（发送 = 机器人 → 用户；接收 = 用户 → 机器人）
    /// </summary>
    public ChatMessageDirection Direction { get; set; }

    /// <summary>
    /// 消息内容（文本正文 / 图片文件URL / 文件URL）
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// 内容类型（text/image/file）
    /// </summary>
    public string ContentType { get; set; } = "text";

    /// <summary>
    /// 配文：image/file 消息附带的文字说明（一条消息同时呈现媒体+文字），文本消息为 null
    /// </summary>
    public string ContentText { get; set; }

    /// <summary>
    /// 全局唯一消息id：WS 直推与厂商推送共用，客户端幂等去重
    /// </summary>
    public string MsgId { get; set; }

    /// <summary>
    /// 投递状态
    /// </summary>
    public ChatMessageStatus Status { get; set; } = ChatMessageStatus.发送中;

    /// <summary>
    /// 会话键（= 任务 Id 或任务配置的会话名）：按脚本/会话名分会话的归属标识；null/空 = 默认会话
    /// （快捷回复、system/security 通知镜像与存量消息）。入站指令不携带会话（服务端不区分来源）。
    /// </summary>
    public string SessionKey { get; set; }

    /// <summary>
    /// 结构化富交互载荷（JSON，可空）：{"poster":"封面地址","options":[{key,label,reply,color,desc}]}。
    /// 服务端不解析内容、仅长度校验后透传落库；客户端解析失败按无载荷降级为普通消息渲染。
    /// </summary>
    public string Payload { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

/// <summary>
/// 会话实体（t_chat_session，2026-09-18 会话分组归并批次）：让会话具备独立存在性——
/// 清空消息后会话保留（0 条消息、无预览、未读 0）、支持从列表单独删除；
/// 消息落库时同事务 upsert 建行/推进 LastSeq，会话列表以本表为主 LEFT JOIN 消息聚合。
/// </summary>
[Table("t_chat_session")]
public class ChatSessionModel
{
    /// <summary>
    /// 会话键，主键 varchar(255)（对齐消息列长度）；统一存空串表示默认会话
    /// （消息表默认会话存 null，落库/回填时归一）。
    /// </summary>
    [Key]
    public string SessionKey { get; set; }

    /// <summary>
    /// 会话创建时间（首条消息落库时间；改名并入既有会话时取两行较小值）。
    /// </summary>
    public DateTime CreateTime { get; set; }

    /// <summary>
    /// 冗余最后消息 Seq，落库同事务推进；0 = 空会话（仅排序用，列表预览仍实时查消息）。
    /// </summary>
    public long LastSeq { get; set; }

    /// <summary>
    /// 服务端权威已读水位（2026-09-21 双端同步批次）：App/Web 任一端阅读即按会话上报推进，
    /// 未读数判定与跨端角标同步以本列为准。**只进语义**（合并钳制 min(max(本列, 上报值), LastSeq)）；
    /// 会话删除（2026-09-21 日志删除逻辑调整）随会话行一并消失，重建建行时预置为该会话现有
    /// 消息最大 Seq（保留的历史不计未读，见 AppMessageService.AppendNoSaveAsync）。
    /// </summary>
    public long LastReadSeq { get; set; }
}

/// <summary>
/// App 端上传文件（图片/聊天附件）：下载仅要求有效管理令牌（单管理员）。
/// </summary>
[Table("t_app_file")]
public class AppFileModel : BaseModel
{
    /// <summary>
    /// 原始文件名（展示用，取 Path.GetFileName 防路径注入）
    /// </summary>
    public string FileName { get; set; }

    /// <summary>
    /// 扩展名（小写，含点；Content-Type 服务端按此固定映射，不信任上传声明）
    /// </summary>
    public string Ext { get; set; }

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// 响应 Content-Type（服务端按扩展名固定映射）
    /// </summary>
    public string ContentType { get; set; }

    /// <summary>
    /// 相对存储路径（./db/appfiles/…）
    /// </summary>
    public string Path { get; set; }

    /// <summary>
    /// 上传时间
    /// </summary>
    public DateTime CreateTime { get; set; } = DateTime.Now;
}
