package com.quantum.app.core.network.dto

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * 管理面复用端点 DTO（feature:task/docker/admin 共用）。
 */

/**
 * 任务（列表行与详情同一模型）。
 *
 * 字段必须覆盖服务端 `TaskSaveModel` 的全部可写项：编辑保存走 PUT api/Task，
 * 服务端 ApplyTo 是**逐字段无条件覆盖**，App 没带回的字段会被写成缺省值
 * （漏 CommandEnv/TextToPicture/TaskStartNotify… 就等于编辑一次清一次）。
 */
@Serializable
data class TaskDto(
    @SerialName("Id") val id: String,
    @SerialName("Name") val name: String,
    @SerialName("Command") val command: String? = null,
    @SerialName("CommandEnv") val commandEnv: String? = null,
    @SerialName("Cron") val cron: String? = null,
    @SerialName("FileName") val fileName: String? = null,
    @SerialName("TextToPicture") val textToPicture: Boolean = false,
    @SerialName("Enable") val enable: Boolean = false,
    @SerialName("EnablePush") val enablePush: Boolean = false,
    @SerialName("EnableRegex") val enableRegex: Boolean = false,
    @SerialName("EnableProxy") val enableProxy: Boolean = false,
    @SerialName("DayLimit") val dayLimit: Int = 0,
    @SerialName("PushGroup") val pushGroup: Boolean = false,
    @SerialName("Revocation") val revocation: Boolean = false,
    @SerialName("WaitTime") val waitTime: Int = 0,
    @SerialName("TaskStartNotify") val taskStartNotify: String? = null,
    @SerialName("TaskEndNotify") val taskEndNotify: String? = null,
    @SerialName("Manager") val manager: Boolean = false,
    @SerialName("Remark") val remark: String? = null,
    @SerialName("CommunicationTypes") val communicationTypes: String? = null,
    /** 会话名（相同会话名的任务在会话页合并为一个会话，空 = 任务独立会话）；
     * 缺了它编辑保存会把服务端已配会话名清成空（PUT 全字段覆盖）。 */
    @SerialName("SessionName") val sessionName: String? = null,
    @SerialName("CreateTime") val createTime: String? = null,
    @SerialName("TaskSubs") val taskSubs: List<TaskSubDto> = emptyList()
)

@Serializable
data class TaskSubDto(
    @SerialName("Id") val id: String? = null,
    @SerialName("TaskId") val taskId: String? = null,
    // 子任务为历史遗留数据，字段一律可空：解码抛异常会连累整张任务列表，
    // 而缺失字段在服务端 PrepareSubs 里本就会被过滤掉
    @SerialName("Name") val name: String? = null,
    @SerialName("Command") val command: String? = null,
    @SerialName("CommandEnv") val commandEnv: String? = null,
    @SerialName("EnableRegex") val enableRegex: Boolean = false,
    @SerialName("Revocation") val revocation: Boolean = false,
    @SerialName("WaitTime") val waitTime: Int = 0,
    @SerialName("Sort") val sort: Int = 0,
    @SerialName("Remark") val remark: String? = null
)

@Serializable
data class EnvDto(
    @SerialName("Id") val id: String? = null,
    @SerialName("Name") val name: String,
    @SerialName("Value") val value: String? = null,
    /**
     * 启用状态：**刻意不给默认值**——kotlinx 会省略「等于声明默认值」的字段，
     * 而服务端 EnvModel.Enable 的缺省是 false，省掉就变成「新增即停用 / 编辑把变量停用」。
     * 不写默认值则该字段必填、恒被序列化。
     */
    @SerialName("Enable") val enable: Boolean,
    /** 备注：编辑时原样回填，避免请求省略该字段把服务端备注清空。 */
    @SerialName("Remark") val remark: String? = null,
    @SerialName("CreateTime") val createTime: String? = null,
    @SerialName("UpdateTime") val updateTime: String? = null
)

@Serializable
data class EnvResultDto(
    @SerialName("Id") val id: String,
    @SerialName("Name") val name: String,
    @SerialName("Value") val value: String? = null,
    @SerialName("Enable") val enable: Boolean = true,
    @SerialName("Remark") val remark: String? = null,
    @SerialName("CreateTime") val createTime: String? = null,
    @SerialName("UpdateTime") val updateTime: String? = null
)

@Serializable
data class LogDto(
    @SerialName("Id") val id: String,
    @SerialName("Title") val title: String? = null,
    @SerialName("Remark") val remark: String? = null,
    @SerialName("Operator") val operator: String? = null,
    @SerialName("Success") val success: Boolean = true,
    @SerialName("LogType") val logType: Int = 1,
    @SerialName("Severity") val severity: Int = 0,
    @SerialName("Module") val module: String? = null,
    @SerialName("UserIP") val userIp: String? = null,
    @SerialName("RequestPath") val requestPath: String? = null,
    @SerialName("DirectoryName") val directoryName: String? = null,
    @SerialName("LogPath") val logPath: String? = null,
    @SerialName("CreateTime") val createTime: String
)

@Serializable
data class LogStatisticsDto(
    @SerialName("TotalCount") val totalCount: Int = 0,
    @SerialName("InfoCount") val infoCount: Int = 0,
    @SerialName("WarnCount") val warnCount: Int = 0,
    @SerialName("ErrorCount") val errorCount: Int = 0
)

@Serializable
data class CommandDto(
    @SerialName("Id") val id: String,
    @SerialName("Key") val key: String,
    @SerialName("Message") val message: String,
    @SerialName("Enable") val enable: Boolean = true,
    @SerialName("EnableRegex") val enableRegex: Boolean = false,
    @SerialName("MessageType") val messageType: Int = 1,
    @SerialName("Remark") val remark: String? = null
)

@Serializable
data class DockerContainerDto(
    @SerialName("Id") val id: String,
    @SerialName("Names") val names: List<String> = emptyList(),
    @SerialName("Image") val image: String? = null,
    @SerialName("State") val state: String? = null,
    @SerialName("Status") val status: String? = null,
    @SerialName("Command") val command: String? = null,
    /**
     * 创建时间：服务端直接序列化 Docker.DotNet 的 `DateTime Created`，
     * 经 Newtonsoft 的 "yyyy-MM-dd HH:mm:ss" 配置出来是**字符串**——
     * 早先声明成 Long 会让整个列表反序列化抛异常，容器页恒为空（"完全不可用"的真因）。
     */
    @SerialName("Created") val created: String? = null
)

@Serializable
data class DockerImageDto(
    @SerialName("Id") val id: String? = null,
    @SerialName("RepoTags") val repoTags: List<String> = emptyList(),
    @SerialName("Size") val size: Long = 0,
    /** 同 [DockerContainerDto.created]：服务端为 DateTime，序列化成字符串。 */
    @SerialName("Created") val created: String? = null
)

@Serializable
data class DockerInfoDto(
    @SerialName("Name") val name: String? = null,
    @SerialName("ServerVersion") val serverVersion: String? = null,
    @SerialName("Containers") val containers: Int = 0,
    @SerialName("ContainersRunning") val containersRunning: Int = 0,
    @SerialName("Images") val images: Int = 0,
    @SerialName("OperatingSystem") val operatingSystem: String? = null,
    @SerialName("Architecture") val architecture: String? = null,
    @SerialName("NCPU") val ncpu: Int = 0,
    @SerialName("MemTotal") val memTotal: Long = 0
)

/**
 * 容器统计（GET containers/{id}/stats，只取内存；其余字段忽略）。
 * 注意：Docker.DotNet 的 stats 模型经 Newtonsoft 序列化为 **camelCase**（memory_stats/usage/stats，
 * 2026-09-20 真机实测反推并经本地序列化实验证实），与同为该包输出的 ContainerListResponse
 * （PascalCase）不一致——两套命名并存是该库自身行为，改字段名前先实测。
 * 内存口径取 working set：cgroup v2 的 usage 含 page cache，须减 stats["inactive_file"]（缺失按 v1 直接用 usage）。
 */
@Serializable
data class DockerStatsDto(
    @SerialName("memory_stats") val memoryStats: DockerMemoryStatsDto? = null
)

@Serializable
data class DockerMemoryStatsDto(
    @SerialName("usage") val usage: Long = 0,
    @SerialName("stats") val stats: Map<String, Long>? = null
)

@Serializable
data class SystemSettingDto(
    @SerialName("UserName") val userName: String? = null,
    @SerialName("BlackQQ") val blackQQ: String? = null,
    @SerialName("AppKey") val appKey: String? = null,
    @SerialName("CommandTimeInterval") val commandTimeInterval: Int = 0,
    @SerialName("MessageQueueInterval") val messageQueueInterval: Int = 100,
    @SerialName("ServerPath") val serverPath: String? = null,
    @SerialName("IntegralProportion") val integralProportion: Int = 0,
    @SerialName("MessageInterval") val messageInterval: Int = 0,
    @SerialName("LoginNotify") val loginNotify: Boolean = false,
    @SerialName("Footer") val footer: String? = null,
    /**
     * 启动期安全三件套（与 Web 设置页对齐，2026-09-20 补）：
     * 服务端 PUT 是白名单回写且会整体覆盖这三项——此前 DTO 缺字段，
     * App 端每保存一次设置就会把三项写成 null/false，补齐后随载荷原样回带。
     */
    @SerialName("KnownProxies") val knownProxies: String? = null,
    @SerialName("AllowedOrigins") val allowedOrigins: String? = null,
    @SerialName("EnableSwagger") val enableSwagger: Boolean = false
)

@Serializable
data class UpdatePasswordRequest(
    @SerialName("OldUserName") val oldUserName: String,
    @SerialName("OldPassword") val oldPassword: String,
    @SerialName("NewUserName") val newUserName: String? = null,
    @SerialName("NewPassword") val newPassword: String
)

@Serializable
data class ScanLoginRequest(
    @SerialName("Ticket") val ticket: String
)
