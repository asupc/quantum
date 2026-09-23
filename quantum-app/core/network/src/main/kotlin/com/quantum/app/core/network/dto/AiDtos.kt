package com.quantum.app.core.network.dto

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * AI 助手 DTO（feature:ai，2026-09-21）：全部复用 Web 端 AiAgent 端点契约（ManagerOnly，HTTP 恒 200 信封）。
 *
 * 双层 JSON：消息的 Payload 是**字符串化的 JSON**——外层信封解到 Payload 字符串后，
 * 由 feature:ai 的 AiPayloadParser 二次解析（解析失败按普通文本兜底，不炸页面）。
 */

@Serializable
data class AiConversationDto(
    @SerialName("Id") val id: String = "",
    @SerialName("Title") val title: String? = null,
    @SerialName("AllowEnvValues") val allowEnvValues: Boolean = false,
    @SerialName("CreateTime") val createTime: String? = null,
    @SerialName("UpdateTime") val updateTime: String? = null,
    @SerialName("LastMessageTime") val lastMessageTime: String? = null
)

/**
 * POST api/AiAgent/conversations（upsert 语义，后端**无 PUT 端点**）：带 Id=更新。
 * Title 必须回传原值——服务端对空标题会把已有标题重置为「未命名会话」
 * （AgentService 空标题分支，与「PUT Task 清 SessionName」同款全量覆盖陷阱）。
 */
@Serializable
data class AiConversationSaveRequest(
    @SerialName("Id") val id: String? = null,
    @SerialName("Title") val title: String? = null,
    @SerialName("AllowEnvValues") val allowEnvValues: Boolean = false
)

/** 对话消息：Role ∈ user/assistant/status，Kind ∈ text/proposal/test_result/error。 */
@Serializable
data class AiMessageDto(
    @SerialName("Id") val id: String? = null,
    @SerialName("ConversationId") val conversationId: String? = null,
    @SerialName("Seq") val seq: Long = 0,
    @SerialName("Role") val role: String = "user",
    @SerialName("Kind") val kind: String = "text",
    @SerialName("Content") val content: String? = null,
    /** 字符串化 JSON（proposal/test_result 载荷），见 AiProposalPayload / AiTestResultPayload。 */
    @SerialName("Payload") val payload: String? = null,
    @SerialName("RunId") val runId: String? = null,
    @SerialName("CreateTime") val createTime: String? = null
)

@Serializable
data class AiChatRequest(
    @SerialName("ConversationId") val conversationId: String,
    @SerialName("Content") val content: String,
    @SerialName("TargetFile") val targetFile: String? = null
)

@Serializable
data class AiChatStartResult(
    @SerialName("RunId") val runId: String? = null,
    @SerialName("MessageId") val messageId: String? = null
)

/**
 * 运行状态：Status ∈ Running/Succeeded/Failed/WaitingConfirm/Cancelled——
 * 完成态叫 Succeeded（无 Completed）；WaitingConfirm=已产出提案等用户确认。
 */
@Serializable
data class AiRunDto(
    @SerialName("Id") val id: String? = null,
    @SerialName("Status") val status: String = "",
    @SerialName("Rounds") val rounds: Int = 0,
    @SerialName("Progress") val progress: String? = null,
    @SerialName("Error") val error: String? = null,
    @SerialName("PromptTokens") val promptTokens: Long = 0,
    @SerialName("CompletionTokens") val completionTokens: Long = 0,
    @SerialName("DurationMs") val durationMs: Long = 0,
    @SerialName("FinishTime") val finishTime: String? = null
)

@Serializable
data class AiStepDto(
    @SerialName("Seq") val seq: Int = 0,
    @SerialName("Kind") val kind: String? = null,
    @SerialName("Name") val name: String? = null,
    @SerialName("Arguments") val arguments: String? = null,
    @SerialName("Result") val result: String? = null,
    @SerialName("DurationMs") val durationMs: Long = 0
)

/**
 * 默认模型：未配置供应商时端点**不报错**（返回 200 + ProviderName/ModelId=null、ContextWindow=0），
 * 引导态按字段判空；SupportsTools 是 int 三态（0=Unknown/1=Supported/2=Unsupported），非布尔。
 */
@Serializable
data class AiDefaultModelDto(
    @SerialName("ProviderId") val providerId: String? = null,
    @SerialName("ProviderName") val providerName: String? = null,
    @SerialName("ModelId") val modelId: String? = null,
    @SerialName("DisplayName") val displayName: String? = null,
    @SerialName("ContextWindow") val contextWindow: Int = 0,
    @SerialName("SupportsTools") val supportsTools: Int = 0
) {
    val configured: Boolean get() = !providerName.isNullOrBlank() && !modelId.isNullOrBlank()
}

@Serializable
data class AiTestRunResultDto(
    @SerialName("Status") val status: String = "None",
    @SerialName("Message") val message: String? = null,
    @SerialName("LogTail") val logTail: String? = null,
    @SerialName("LogId") val logId: String? = null,
    @SerialName("DurationMs") val durationMs: Long = 0,
    @SerialName("HasException") val hasException: Boolean = false
)

/** 提案差异对照（GET proposals/{id}/content）：基线=当前磁盘内容，候选=提案内容。 */
@Serializable
data class AiProposalContentDto(
    @SerialName("FileName") val fileName: String? = null,
    @SerialName("Summary") val summary: String? = null,
    @SerialName("Status") val status: String? = null,
    @SerialName("TestStatus") val testStatus: String? = null,
    @SerialName("NewContent") val newContent: String? = null,
    @SerialName("BaseContent") val baseContent: String? = null
)

/** 应用提案结果：Success=false 时三类诊断非空；已应用/已忽略/基线变动走信封 Code=500（非此形态）。 */
@Serializable
data class ScriptSaveResultDto(
    @SerialName("Success") val success: Boolean = false,
    @SerialName("Blocked") val blocked: List<ScriptIssueDto> = emptyList(),
    @SerialName("Errors") val errors: List<ScriptIssueDto> = emptyList(),
    @SerialName("Warnings") val warnings: List<ScriptIssueDto> = emptyList()
)

@Serializable
data class ScriptIssueDto(
    @SerialName("Line") val line: Int = 0,
    @SerialName("Code") val code: String? = null,
    @SerialName("Message") val message: String? = null
)

/** proposal 消息 Payload 内层结构（状态变化后服务端会回写该消息的 Payload，重拉即最新）。 */
@Serializable
data class AiProposalPayload(
    @SerialName("Id") val id: String? = null,
    @SerialName("FileName") val fileName: String? = null,
    @SerialName("Summary") val summary: String? = null,
    @SerialName("Status") val status: String? = null,
    @SerialName("TestStatus") val testStatus: String? = null,
    @SerialName("TestRunLogId") val testRunLogId: String? = null,
    @SerialName("BaseHash") val baseHash: String? = null,
    @SerialName("NewHash") val newHash: String? = null,
    /** 再内一层字符串化 JSON（门禁/编译诊断），见 [AiProposalDiagnostics]。 */
    @SerialName("Diagnostics") val diagnostics: String? = null,
    @SerialName("AppliedVersionId") val appliedVersionId: String? = null,
    @SerialName("CreateTime") val createTime: String? = null
)

/** test_result 消息 Payload 内层结构。 */
@Serializable
data class AiTestResultPayload(
    @SerialName("ProposalId") val proposalId: String? = null,
    @SerialName("Status") val status: String? = null,
    @SerialName("DurationMs") val durationMs: Long = 0,
    @SerialName("HasException") val hasException: Boolean = false,
    @SerialName("LogId") val logId: String? = null,
    @SerialName("Message") val message: String? = null,
    @SerialName("LogTail") val logTail: String? = null
)

/** 提案 Diagnostics 内层结构（生成提案时的门禁/编译诊断快照）。 */
@Serializable
data class AiProposalDiagnostics(
    @SerialName("Blocked") val blocked: List<ScriptIssueDto> = emptyList(),
    @SerialName("Errors") val errors: List<ScriptIssueDto> = emptyList(),
    @SerialName("Warnings") val warnings: List<ScriptIssueDto> = emptyList()
)

/**
 * Task/script-list 树形节点（与后端 ScriptsFile 对齐：Title/Contextmenu/Children/Path 递归，
 * 目录节点 path=null、文件叶节点 path=相对路径）。「目标脚本」选择递归筛 .cs 叶节点。
 */
@Serializable
data class ScriptTreeDto(
    @SerialName("Title") val title: String? = null,
    @SerialName("Contextmenu") val contextmenu: Boolean = false,
    @SerialName("Path") val path: String? = null,
    @SerialName("Children") val children: List<ScriptTreeDto> = emptyList()
)

/** 递归收集脚本树全部 .cs 叶节点相对路径（对齐 Web flattenScripts 语义：有 children 下钻、叶节点筛后缀）。 */
fun List<ScriptTreeDto>.flattenCsFiles(): List<String> {
    val out = ArrayList<String>()
    fun walk(nodes: List<ScriptTreeDto>) {
        for (node in nodes) {
            if (node.children.isNotEmpty()) {
                walk(node.children)
            } else if (node.path?.endsWith(".cs", ignoreCase = true) == true) {
                out += node.path
            }
        }
    }
    walk(this)
    return out
}
