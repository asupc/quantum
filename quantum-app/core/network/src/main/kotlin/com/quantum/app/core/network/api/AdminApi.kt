package com.quantum.app.core.network.api

import com.quantum.app.core.network.dto.AiChatRequest
import com.quantum.app.core.network.dto.AiChatStartResult
import com.quantum.app.core.network.dto.AiConversationDto
import com.quantum.app.core.network.dto.AiConversationSaveRequest
import com.quantum.app.core.network.dto.AiDefaultModelDto
import com.quantum.app.core.network.dto.AiMessageDto
import com.quantum.app.core.network.dto.AiProposalContentDto
import com.quantum.app.core.network.dto.AiRunDto
import com.quantum.app.core.network.dto.AiStepDto
import com.quantum.app.core.network.dto.AiTestRunResultDto
import com.quantum.app.core.network.dto.CommandDto
import com.quantum.app.core.network.dto.DockerContainerDto
import com.quantum.app.core.network.dto.DockerImageDto
import com.quantum.app.core.network.dto.DockerInfoDto
import com.quantum.app.core.network.dto.DockerStatsDto
import com.quantum.app.core.network.dto.EnvDto
import com.quantum.app.core.network.dto.EnvResultDto
import com.quantum.app.core.network.dto.LogDto
import com.quantum.app.core.network.dto.LogStatisticsDto
import com.quantum.app.core.network.dto.PageResultDto
import com.quantum.app.core.network.dto.ScriptSaveResultDto
import com.quantum.app.core.network.dto.ScriptTreeDto
import com.quantum.app.core.network.dto.SystemSettingDto
import com.quantum.app.core.network.dto.TaskDto
import com.quantum.app.core.network.dto.UpdatePasswordRequest
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import retrofit2.http.Body
import retrofit2.http.DELETE
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.PUT
import retrofit2.http.Part
import retrofit2.http.Path
import retrofit2.http.Query

/**
 * 复用管理面端点（契约见 docs/App端API契约.md §5）：
 * 权限由服务端按 JWT Manager claim / 调用者身份收敛，客户端不自行显隐。
 * 所有写操作 body 按 PascalCase DTO 传输；批量 ids 一律逗号分隔 query。
 */
interface AdminApi {

    // ---- 任务 ----
    @GET("api/Task")
    suspend fun tasks(
        @Query("PageIndex") pageIndex: Int = 1,
        @Query("PageSize") pageSize: Int = 20,
        @Query("Key") key: String? = null,
        @Query("Enable") enable: Boolean? = null
    ): EnvelopeDto<PageResultDto<TaskDto>>

    @GET("api/Task/{id}")
    suspend fun taskDetail(@Path("id") id: String): EnvelopeDto<TaskDto>

    @POST("api/Task")
    suspend fun addTask(@Body body: TaskDto): EnvelopeDto<Boolean>

    @PUT("api/Task")
    suspend fun updateTask(@Body body: TaskDto): EnvelopeDto<Boolean>

    @DELETE("api/Task")
    suspend fun deleteTasks(@Query("ids") ids: String): EnvelopeDto<Boolean>

    @POST("api/Task/exec-task")
    suspend fun execTasks(@Body ids: List<String>): EnvelopeDto<Boolean>

    @PUT("api/Task/enable")
    suspend fun enableTasks(@Body ids: List<String>): EnvelopeDto<Boolean>

    @PUT("api/Task/disable")
    suspend fun disableTasks(@Body ids: List<String>): EnvelopeDto<Boolean>

    @POST("api/Task/finish/{taskId}")
    suspend fun finishTask(@Path("taskId") taskId: String): EnvelopeDto<Boolean>

    @POST("api/Task/redo/{taskId}")
    suspend fun redoTask(@Path("taskId") taskId: String): EnvelopeDto<String>

    @POST("api/Task/add-env/{taskId}")
    suspend fun addTaskEnv(@Path("taskId") taskId: String, @Body env: EnvDto): EnvelopeDto<String>

    @GET("api/Task/scripts")
    suspend fun scriptContent(@Query("fileName") fileName: String): EnvelopeDto<String>

    @PUT("api/Task/scripts")
    suspend fun updateScript(@Body body: ScriptSaveRequest): EnvelopeDto<Boolean>

    @GET("api/Task/script-list")
    suspend fun scriptList(): EnvelopeDto<List<ScriptFileDto>>

    // ---- 日志 ----
    @GET("api/Logs")
    suspend fun logs(
        @Query("PageIndex") pageIndex: Int = 1,
        @Query("PageSize") pageSize: Int = 20,
        @Query("Key") key: String? = null,
        @Query("LogType") logType: Int? = null,
        @Query("Severity") severity: Int? = null,
        @Query("Module") module: String? = null,
        @Query("FailedOnly") failedOnly: Boolean? = null,
        @Query("StartTime") startTime: String? = null,
        @Query("EndTime") endTime: String? = null
    ): EnvelopeDto<PageResultDto<LogDto>>

    @GET("api/Logs/details/{id}")
    suspend fun logDetails(@Path("id") id: String): EnvelopeDto<String>

    @GET("api/Logs/statistics")
    suspend fun logStatistics(@Query("days") days: Int = 7): EnvelopeDto<LogStatisticsDto>

    // ---- 环境变量 ----
    @GET("api/Env")
    suspend fun envs(
        @Query("PageIndex") pageIndex: Int = 1,
        @Query("PageSize") pageSize: Int = 20,
        @Query("Key") key: String? = null
    ): EnvelopeDto<PageResultDto<EnvResultDto>>

    @POST("api/Env")
    suspend fun saveEnvs(@Body envs: List<EnvDto>): EnvelopeDto<List<String>>

    @DELETE("api/Env/deletes")
    suspend fun deleteEnvs(@Query("ids") ids: String): EnvelopeDto<Boolean>

    @PUT("api/Env/enable")
    suspend fun enableEnvs(@Query("ids") ids: String): EnvelopeDto<Boolean>

    @PUT("api/Env/disable")
    suspend fun disableEnvs(@Query("ids") ids: String): EnvelopeDto<Boolean>

    @GET("api/Env/Query")
    suspend fun queryEnvs(
        @Query("name") name: String? = null,
        @Query("key") key: String? = null
    ): EnvelopeDto<List<EnvDto>>

    // ---- 系统设置（管理员） ----
    @GET("api/SystemConfig")
    suspend fun systemSetting(): EnvelopeDto<SystemSettingDto>

    @PUT("api/SystemConfig")
    suspend fun updateSystemSetting(@Body body: SystemSettingDto): EnvelopeDto<Boolean>

    /** 收缩数据库（SQLite VACUUM / MySQL OPTIMIZE），与 Web 设置页同端点。 */
    @POST("api/SystemConfig/database-shrink")
    suspend fun databaseShrink(): EnvelopeDto<Boolean>

    @POST("api/SystemConfig/password")
    suspend fun updateWebPassword(@Body body: UpdatePasswordRequest): EnvelopeDto<Boolean>

    // ---- 快捷回复 ----
    @GET("api/Command")
    suspend fun commands(
        @Query("PageIndex") pageIndex: Int = 1,
        @Query("PageSize") pageSize: Int = 200,
        @Query("Key") key: String? = null
    ): EnvelopeDto<PageResultDto<CommandDto>>

    @POST("api/Command")
    suspend fun addCommand(@Body body: CommandDto): EnvelopeDto<Boolean>

    @PUT("api/Command")
    suspend fun updateCommand(@Body body: CommandDto): EnvelopeDto<Boolean>

    @DELETE("api/Command")
    suspend fun deleteCommands(@Query("ids") ids: String): EnvelopeDto<Boolean>

    @PUT("api/Command/enable")
    suspend fun enableCommands(@Body ids: List<String>): EnvelopeDto<Boolean>

    @PUT("api/Command/disable")
    suspend fun disableCommands(@Body ids: List<String>): EnvelopeDto<Boolean>

    // ---- Docker（管理员） ----
    @GET("api/Docker/containers")
    suspend fun dockerContainers(@Query("all") all: Boolean = true): EnvelopeDto<List<DockerContainerDto>>

    @GET("api/Docker/containers/{containerId}/logs")
    suspend fun dockerLogs(
        @Path("containerId") containerId: String,
        @Query("tail") tail: String = "200"
    ): EnvelopeDto<String>

    /** 单容器统计（OneShot）：容器页内存占用。 */
    @GET("api/Docker/containers/{containerId}/stats")
    suspend fun dockerStats(@Path("containerId") containerId: String): EnvelopeDto<DockerStatsDto>

    @POST("api/Docker/containers/{containerId}/start")
    suspend fun dockerStart(@Path("containerId") containerId: String): EnvelopeDto<Boolean>

    @POST("api/Docker/containers/{containerId}/stop")
    suspend fun dockerStop(@Path("containerId") containerId: String): EnvelopeDto<Boolean>

    @POST("api/Docker/containers/{containerId}/restart")
    suspend fun dockerRestart(@Path("containerId") containerId: String): EnvelopeDto<Boolean>

    @GET("api/Docker/images")
    suspend fun dockerImages(): EnvelopeDto<List<DockerImageDto>>

    @GET("api/Docker/info")
    suspend fun dockerInfo(): EnvelopeDto<DockerInfoDto>

    // ---- 扫码登录（Web 轮询端点 App 不用；qr-create 给 Web 用） ----
    @POST("api/Login/qr-create")
    suspend fun qrCreate(): EnvelopeDto<QrCreateDto>

    @GET("api/Login/qr-status")
    suspend fun qrStatus(@Query("ticket") ticket: String): EnvelopeDto<QrStatusDto>

    // ---- AI 助手（AiAgent 控制器，ManagerOnly，与 Web AI 页共用；轮询式：消息增量 + 运行状态） ----

    @GET("api/AiAgent/conversations")
    suspend fun aiConversations(): EnvelopeDto<List<AiConversationDto>>

    /** upsert（无 PUT 端点）：带 Id=更新且 Title 必须回传原值，否则服务端重置为「未命名会话」。 */
    @POST("api/AiAgent/conversations")
    suspend fun aiSaveConversation(@Body body: AiConversationSaveRequest): EnvelopeDto<AiConversationDto>

    @DELETE("api/AiAgent/conversations/deletes")
    suspend fun aiDeleteConversations(@Query("ids") ids: String): EnvelopeDto<Boolean>

    /** afterSeq=0 取最近 limit 条（升序）；afterSeq>0 增量（Seq 严格大于，升序）。 */
    @GET("api/AiAgent/messages")
    suspend fun aiMessages(
        @Query("conversationId") conversationId: String,
        @Query("afterSeq") afterSeq: Long = 0,
        @Query("limit") limit: Int = 200
    ): EnvelopeDto<List<AiMessageDto>>

    @POST("api/AiAgent/chat")
    suspend fun aiChat(@Body body: AiChatRequest): EnvelopeDto<AiChatStartResult>

    @GET("api/AiAgent/runs/{id}")
    suspend fun aiRun(@Path("id") id: String): EnvelopeDto<AiRunDto>

    /** 会话最近一次 run（任意状态；无 run 时 Code=200 + Data=null 合法，消费须用 unwrapOrNull）。
     *  字面量段优先于参数段，与 runs/{id} 无路由冲突；旧后端会把 "latest" 当 runId 返回 Code=500，
     *  调用方 runCatching 静默回退（表现为恢复逻辑不生效，不影响其他功能）。 */
    @GET("api/AiAgent/runs/latest")
    suspend fun aiLatestRun(@Query("conversationId") conversationId: String): EnvelopeDto<AiRunDto>

    /** cancel 已结束的 run 返回 Data=false（幂等正常态），不当错误处理。 */
    @POST("api/AiAgent/runs/{id}/cancel")
    suspend fun aiCancelRun(@Path("id") id: String): EnvelopeDto<Boolean>

    @GET("api/AiAgent/runs/{id}/steps")
    suspend fun aiRunSteps(@Path("id") id: String, @Query("afterSeq") afterSeq: Int = 0): EnvelopeDto<List<AiStepDto>>

    /** 平台能力契约（Markdown 文本），头部弹窗展示。 */
    @GET("api/AiAgent/contract")
    suspend fun aiContract(): EnvelopeDto<String>

    /** 未配置供应商时返回 200 + 空字段（不报错），按字段判空出引导态。 */
    @GET("api/AiAgent/default-model")
    suspend fun aiDefaultModel(): EnvelopeDto<AiDefaultModelDto>

    @POST("api/AiAgent/proposals/{id}/test-run")
    suspend fun aiProposalTestRun(@Path("id") id: String): EnvelopeDto<AiTestRunResultDto>

    /** 门禁/编译不过 → Success=false + 三类诊断；已应用/已忽略/基线变动 → 信封 Code=500。 */
    @POST("api/AiAgent/proposals/{id}/apply")
    suspend fun aiProposalApply(@Path("id") id: String): EnvelopeDto<ScriptSaveResultDto>

    @POST("api/AiAgent/proposals/{id}/discard")
    suspend fun aiProposalDiscard(@Path("id") id: String): EnvelopeDto<Boolean>

    @GET("api/AiAgent/proposals/{id}/content")
    suspend fun aiProposalContent(@Path("id") id: String): EnvelopeDto<AiProposalContentDto>

    /** 树形脚本清单（同一 Task/script-list 端点）：旧 [scriptList] 扁平 DTO 收不到 Children
     *  （ignoreUnknownKeys 静默丢弃只剩顶层），AI「目标脚本」选择改用本方法 + flattenCsFiles。 */
    @GET("api/Task/script-list")
    suspend fun scriptTree(): EnvelopeDto<List<ScriptTreeDto>>

    companion object {
        fun plain(value: String): okhttp3.RequestBody =
            value.toRequestBody("text/plain".toMediaType())
    }
}

@kotlinx.serialization.Serializable
data class ScriptSaveRequest(
    @kotlinx.serialization.SerialName("path") val path: String,
    @kotlinx.serialization.SerialName("content") val content: String
)

@kotlinx.serialization.Serializable
data class ScriptFileDto(
    @kotlinx.serialization.SerialName("Name") val name: String? = null,
    @kotlinx.serialization.SerialName("Path") val path: String? = null,
    @kotlinx.serialization.SerialName("IsDirectory") val isDirectory: Boolean = false
)

@kotlinx.serialization.Serializable
data class QrCreateDto(
    @kotlinx.serialization.SerialName("Ticket") val ticket: String,
    @kotlinx.serialization.SerialName("ExpireAt") val expireAt: String? = null
)

@kotlinx.serialization.Serializable
data class QrStatusDto(
    @kotlinx.serialization.SerialName("Status") val status: Int = 0,
    @kotlinx.serialization.SerialName("Token") val token: String? = null
)
