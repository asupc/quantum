package com.quantum.app.core.network.api

import com.quantum.app.core.network.dto.AppDeviceDto
import com.quantum.app.core.network.dto.ChatMessageDto
import com.quantum.app.core.network.dto.CommandAcceptedResult
import com.quantum.app.core.network.dto.CommandRequest
import com.quantum.app.core.network.dto.DeleteSessionResult
import com.quantum.app.core.network.dto.LoginRequest
import com.quantum.app.core.network.dto.LoginResult
import com.quantum.app.core.network.dto.MediaListDto
import com.quantum.app.core.network.dto.NotificationDto
import com.quantum.app.core.network.dto.NotifySettingDto
import com.quantum.app.core.network.dto.PageResultDto
import com.quantum.app.core.network.dto.RefreshRequest
import com.quantum.app.core.network.dto.ScanLoginRequest
import com.quantum.app.core.network.dto.SessionPageResult
import com.quantum.app.core.network.dto.SessionsOverviewRequest
import com.quantum.app.core.network.dto.SessionsOverviewResult
import com.quantum.app.core.network.dto.SessionsReadResult
import com.quantum.app.core.network.dto.SyncMessagesResult
import com.quantum.app.core.network.dto.UnbindDeviceRequest
import com.quantum.app.core.network.dto.UploadResultDto
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import okhttp3.MultipartBody
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.Multipart
import retrofit2.http.POST
import retrofit2.http.PUT
import retrofit2.http.Part
import retrofit2.http.Query
import retrofit2.http.Url

/**
 * 服务端统一信封：HTTP 恒 200，成败只看 Code（200/500/401）。
 * 解包统一走 [unwrap]（Session 层 helper），Retrofit 只负责反序列化。
 */
@Serializable
data class EnvelopeDto<T>(
    @SerialName("Code") val code: Int = 0,
    @SerialName("Message") val message: String? = null,
    @SerialName("Data") val data: T? = null
)

/** 信封解包：Code!=200 时抛业务/认证异常，供 Repository/ViewModel 统一捕获。
 *  Code=200 但 Data 为空视为契约违背（泛型擦除会让 `data as T` 把 null 漏出去 → 上游 NPE 崩溃），故显式抛异常。 */
fun <T> EnvelopeDto<T>.unwrap(): T = when {
    code == 200 -> data ?: throw com.quantum.app.core.common.ApiException(200, "响应数据为空")
    code == 401 -> throw com.quantum.app.core.common.AuthException(message)
    else -> throw com.quantum.app.core.common.ApiException(code, message)
}

/** 可空信封解包：仅用于「Code=200 & Data=null 属合法」的端点（如按会话键查任务不存在）。
 *  认证/业务失败仍按 unwrap 的异常语义抛出，只把「成功但空」折叠成 null 返回。 */
fun <T> EnvelopeDto<T>.unwrapOrNull(): T? = when {
    code == 200 -> data
    code == 401 -> throw com.quantum.app.core.common.AuthException(message)
    else -> throw com.quantum.app.core.common.ApiException(code, message)
}

/** App 专用端点（api/AppAuth、api/App、api/AppNotification、api/AppUpload）。 */
interface AppApi {

    // ---- AppAuth（匿名） ----
    @POST("api/AppAuth/login")
    suspend fun login(@Body body: LoginRequest): EnvelopeDto<LoginResult>

    @POST("api/AppAuth/refresh")
    suspend fun refresh(@Body body: RefreshRequest): EnvelopeDto<LoginResult>

    @POST("api/AppAuth/logout")
    suspend fun logout(@Body body: RefreshRequest): EnvelopeDto<Boolean>

    // ---- 会话消息 ----
    @GET("api/App/messages")
    suspend fun messages(
        @Query("afterSeq") afterSeq: Long,
        @Query("limit") limit: Int = 500
    ): EnvelopeDto<SyncMessagesResult>

    /**
     * 会话内分页（GET api/App/session/messages，只读）：某会话 Seq 倒序取最近一页（升序返回），
     * beforeSeq ≤ 0 表示从最新一条开始；limit 服务端 clamp 1..200。
     * 发送后 echo 兜底轮询（2.5）用它按会话增量找回「接收」行，代替全量 syncAll。
     */
    @GET("api/App/session/messages")
    suspend fun sessionMessages(
        @Query("session") session: String?,
        @Query("beforeSeq") beforeSeq: Long = 0,
        @Query("limit") limit: Int = 50
    ): EnvelopeDto<SessionPageResult>

    @POST("api/App/messages/read")
    suspend fun markRead(@Query("upToSeq") upToSeq: Long): EnvelopeDto<Long>

    /**
     * 会话列表快照（POST api/App/sessions/overview，2026-09-21 双端同步）：上报本地各会话已读水位，
     * 服务端合并钳制为权威水位（吸收落库）；响应条目 ReadSeq 供本端吸收推进（对端离线进度追赶）。
     */
    @POST("api/App/sessions/overview")
    suspend fun sessionsOverview(@Body body: SessionsOverviewRequest): EnvelopeDto<SessionsOverviewResult>

    /**
     * 会话已读水位上报（POST api/App/sessions/read）：按会话推进服务端权威水位（只进 + 钳制），
     * 替代旧全局 messages/read（跨会话误标）；实际推进的会话由服务端广播 session_read 帧给其他端。
     */
    @POST("api/App/sessions/read")
    suspend fun sessionsRead(@Body body: SessionsOverviewRequest): EnvelopeDto<SessionsReadResult>

    /**
     * 删除会话（session 空 = 默认会话；2026-09-21 日志删除逻辑调整）：只删会话实体（会话数据），
     * 消息行（业务真实产生的记录）服务端保留——消息删除入口收敛到日志中心；任务再推送时自动重建。
     * 响应 MaxSeq 恒为当前全局最大（消息不删后号段不复用，回拨 min 为 no-op，兼容保留）。
     */
    @POST("api/App/session/delete")
    suspend fun deleteSession(@Query("session") session: String?): EnvelopeDto<DeleteSessionResult>

    @GET("api/App/messages/unread-count")
    suspend fun unreadCount(): EnvelopeDto<Long>

    @GET("api/App/history")
    suspend fun history(
        @Query("keyword") keyword: String?,
        @Query("page") page: Int = 1,
        @Query("pageSize") pageSize: Int = 20
    ): EnvelopeDto<List<ChatMessageDto>>

    @POST("api/App/command")
    suspend fun command(@Body body: CommandRequest): EnvelopeDto<CommandAcceptedResult>

    // ---- 推送注册/设备 ----

    @GET("api/App/devices")
    suspend fun devices(): EnvelopeDto<List<AppDeviceDto>>

    @POST("api/App/devices/unbind")
    suspend fun unbindDevice(@Body body: UnbindDeviceRequest): EnvelopeDto<Boolean>

    // ---- 站内通知 ----
    @GET("api/AppNotification/list")
    suspend fun notifications(
        @Query("page") page: Int = 1,
        @Query("pageSize") pageSize: Int = 20,
        @Query("unreadOnly") unreadOnly: Boolean = false
    ): EnvelopeDto<PageResultDto<NotificationDto>>

    @GET("api/AppNotification/unread-count")
    suspend fun notificationUnreadCount(): EnvelopeDto<Long>

    @POST("api/AppNotification/read")
    suspend fun markNotificationRead(@Query("id") id: String? = null): EnvelopeDto<Boolean>

    // ---- 上传 ----
    @Multipart
    @POST("api/AppUpload/image")
    suspend fun uploadImage(@Part file: MultipartBody.Part): EnvelopeDto<UploadResultDto>

    @Multipart
    @POST("api/AppUpload/file")
    suspend fun uploadFile(@Part file: MultipartBody.Part): EnvelopeDto<UploadResultDto>

    // ---- 媒体库（M3：服务端下载根目录产物列表；播放地址由客户端拼 api/AppMedia/file?path=） ----
    @GET("api/AppMedia/list")
    suspend fun mediaList(
        @Query("page") page: Int = 1,
        @Query("pageSize") pageSize: Int = 50,
        @Query("keyword") keyword: String? = null,
        @Query("dir") dir: String? = null
    ): EnvelopeDto<MediaListDto>

    // ---- 通知偏好（A5.5） ----
    @GET("api/App/notify-setting")
    suspend fun notifySetting(): EnvelopeDto<NotifySettingDto>

    @PUT("api/App/notify-setting")
    suspend fun updateNotifySetting(@Body body: NotifySettingDto): EnvelopeDto<NotifySettingDto>

    // ---- 扫码登录（P2：App 侧 Manager 令牌授权） ----
    @POST("api/App/scan-login")
    suspend fun scanLogin(@Body body: ScanLoginRequest): EnvelopeDto<Boolean>
}
