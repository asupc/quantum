package com.quantum.app.core.network.dto

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * REST DTO 统一集中定义：服务端是 NewtonSoft PascalCase + 枚举按数字序列化，
 * 一律 @SerialName 显式标注，杜绝依赖命名策略。
 */

@Serializable
data class LoginRequest(
    @SerialName("UserName") val userName: String,
    @SerialName("Password") val password: String,
    @SerialName("DeviceId") val deviceId: String,
    @SerialName("DeviceName") val deviceName: String? = null,
    @SerialName("Platform") val platform: String = "android"
)

@Serializable
data class RefreshRequest(
    @SerialName("RefreshToken") val refreshToken: String,
    @SerialName("DeviceId") val deviceId: String
)

@Serializable
data class LoginResult(
    @SerialName("AccessToken") val accessToken: String,
    @SerialName("RefreshToken") val refreshToken: String,
    @SerialName("RefreshExpiresAt") val refreshExpiresAt: String
)

@Serializable
data class SyncMessagesResult(
    @SerialName("MaxSeq") val maxSeq: Long,
    @SerialName("Data") val data: List<ChatMessageDto> = emptyList()
)

/** 会话内分页结果（GET api/App/session/messages）：Seq 升序一页 + 是否还有更早（服务端多取 1 免 COUNT）。 */
@Serializable
data class SessionPageResult(
    @SerialName("HasMore") val hasMore: Boolean = false,
    @SerialName("Messages") val messages: List<ChatMessageDto> = emptyList()
)

/**
 * 删除会话结果（2026-09-21 日志删除逻辑调整）：只删会话实体、消息记录服务端保留；
 * MaxSeq 恒为当前全局最大（消息不删后号段不复用，回拨 min 为 no-op，兼容保留）。
 */
@Serializable
data class DeleteSessionResult(
    @SerialName("Deleted") val deleted: Boolean,
    @SerialName("MaxSeq") val maxSeq: Long
)

/**
 * 会话已读水位上报入参（POST api/App/sessions/overview 与 sessions/read 共用，2026-09-21 双端同步）：
 * 各会话本地已读水位（键 = 会话键，空串 = 默认会话；缺省视为 0 = 全部未读）。
 */
@Serializable
data class SessionsOverviewRequest(
    @SerialName("ReadSeqs") val readSeqs: Map<String, Long> = emptyMap()
)

/** 会话列表快照条目：总数/未读/最后一条 + 服务端权威已读水位（吸收它即完成对端离线进度追赶）。 */
@Serializable
data class SessionOverviewItemDto(
    @SerialName("SessionKey") val sessionKey: String = "",
    @SerialName("Total") val total: Long = 0,
    @SerialName("Unread") val unread: Long = 0,
    @SerialName("ReadSeq") val readSeq: Long = 0,
    @SerialName("Last") val last: ChatMessageDto? = null
)

/** 会话列表快照结果（POST api/App/sessions/overview）。 */
@Serializable
data class SessionsOverviewResult(
    @SerialName("MaxSeq") val maxSeq: Long = 0,
    @SerialName("Sessions") val sessions: List<SessionOverviewItemDto> = emptyList()
)

/** 会话已读水位上报结果：实际推进的 (会话键, 水位) 列表（与 session_read 广播帧同源）。 */
@Serializable
data class SessionsReadResult(
    @SerialName("Advanced") val advanced: List<SessionReadAdvancedDto> = emptyList()
)

@Serializable
data class SessionReadAdvancedDto(
    @SerialName("Session") val session: String = "",
    @SerialName("Seq") val seq: Long = 0
)

@Serializable
data class ChatMessageDto(
    @SerialName("Id") val id: String,
    @SerialName("Seq") val seq: Long,
    /** 1=发送(机器人→用户) / 2=接收(用户→机器人) */
    @SerialName("Direction") val direction: Int,
    @SerialName("Content") val content: String,
    @SerialName("ContentType") val contentType: String = "text",
    /** 配文：image/file 消息附带的文字说明（旧服务端/文本消息缺省 null） */
    @SerialName("ContentText") val contentText: String? = null,
    @SerialName("MsgId") val msgId: String,
    /** 1发送中/2已送达/3已读 */
    @SerialName("Status") val status: Int = 1,
    @SerialName("CreateTime") val createTime: String,
    /** 会话键（= 任务 Id）：旧服务端缺省 null → 默认会话 */
    @SerialName("SessionKey") val sessionKey: String? = null,
    /** 结构化富交互载荷（JSON 字符串，可空）：{"poster","options":[{key,label,reply,color,desc}]} */
    @SerialName("Payload") val payload: String? = null
)

@Serializable
data class CommandRequest(
    @SerialName("Content") val content: String,
    /** image/file 时 content=FileId（A5.0① 上传后封装）；缺省 text */
    @SerialName("ContentType") val contentType: String = "text",
    /** 配文：image/file 消息附带的文字说明（可空；文本消息忽略） */
    @SerialName("ContentText") val contentText: String? = null,
    /** 来源会话键（= 任务 Id，空 = 默认会话）：接收行回显与快捷回复归来源会话 */
    @SerialName("Session") val session: String? = null,
    /** 点选来源任务 Id（合并会话内点选代发的精确路由依据）；手打/重发缺省 null */
    @SerialName("TargetTaskId") val targetTaskId: String? = null
)

@Serializable
data class CommandAcceptedResult(
    @SerialName("Accepted") val accepted: Boolean,
    @SerialName("Message") val message: String? = null
)

@Serializable
data class UnbindDeviceRequest(
    @SerialName("DeviceId") val deviceId: String
)

@Serializable
data class AppDeviceDto(
    @SerialName("Id") val id: String,
    @SerialName("DeviceId") val deviceId: String,
    @SerialName("DeviceName") val deviceName: String? = null,
    @SerialName("Platform") val platform: String? = null,
    @SerialName("LastSeenAt") val lastSeenAt: String,
    @SerialName("Enabled") val enabled: Boolean = true,
    @SerialName("CreateTime") val createTime: String
)

@Serializable
data class NotificationDto(
    @SerialName("Id") val id: String,
    @SerialName("Title") val title: String,
    @SerialName("Content") val content: String,
    @SerialName("Category") val category: String = "system",
    @SerialName("Jump") val jump: String? = null,
    @SerialName("MsgId") val msgId: String? = null,
    @SerialName("CreatedAt") val createdAt: String,
    @SerialName("ReadAt") val readAt: String? = null
)

@Serializable
data class PageResultDto<T>(
    @SerialName("Data") val data: List<T> = emptyList(),
    @SerialName("TotalCount") val totalCount: Int = 0,
    @SerialName("Page") val page: Int = 0,
    @SerialName("PageSize") val pageSize: Int = 0
)

@Serializable
data class UploadResultDto(
    @SerialName("FileId") val fileId: String,
    @SerialName("FileName") val fileName: String,
    @SerialName("Size") val size: Long
)

@Serializable
data class NotifySettingDto(
    @SerialName("TaskPush") val taskPush: Boolean = true,
    @SerialName("SystemPush") val systemPush: Boolean = true,
    @SerialName("SecurityPush") val securityPush: Boolean = true,
    @SerialName("DndStart") val dndStart: String? = null,
    @SerialName("DndEnd") val dndEnd: String? = null
)

@Serializable
data class MediaItemDto(
    @SerialName("Name") val name: String,
    /** 相对下载根目录的路径（/ 分隔），播放/封面按此拼 api/AppMedia/file|cover?path= */
    @SerialName("Path") val path: String,
    @SerialName("Title") val title: String? = null,
    @SerialName("Artist") val artist: String? = null,
    @SerialName("Size") val size: Long = 0,
    @SerialName("Ext") val ext: String = "",
    @SerialName("Mtime") val mtime: String? = null
)

@Serializable
data class MediaListDto(
    @SerialName("Items") val items: List<MediaItemDto> = emptyList(),
    @SerialName("Total") val total: Int = 0,
    @SerialName("Page") val page: Int = 1,
    @SerialName("PageSize") val pageSize: Int = 50
)
