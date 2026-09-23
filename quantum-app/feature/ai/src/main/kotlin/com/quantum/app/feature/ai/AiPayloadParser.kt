package com.quantum.app.feature.ai

import com.quantum.app.core.network.dto.AiDefaultModelDto
import com.quantum.app.core.network.dto.AiMessageDto
import com.quantum.app.core.network.dto.AiProposalDiagnostics
import com.quantum.app.core.network.dto.AiProposalPayload
import com.quantum.app.core.network.dto.AiTestResultPayload
import kotlinx.serialization.json.Json

/**
 * Payload 双层 JSON 解析与 UI 状态映射（纯 Kotlin 可测）：
 * 消息 Payload 是字符串化 JSON，解析失败一律返回 null（调用方按普通文本兜底，不炸页面）。
 */
object AiPayloadParser {

    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    fun parseProposal(payload: String?): AiProposalPayload? = payload?.let {
        runCatching { json.decodeFromString<AiProposalPayload>(it) }.getOrNull()
    }

    fun parseTestResult(payload: String?): AiTestResultPayload? = payload?.let {
        runCatching { json.decodeFromString<AiTestResultPayload>(it) }.getOrNull()
    }

    /** Diagnostics 是再内一层的字符串化 JSON（可能为 null/坏串）。 */
    fun parseDiagnostics(diagnostics: String?): AiProposalDiagnostics? = diagnostics?.let {
        runCatching { json.decodeFromString<AiProposalDiagnostics>(it) }.getOrNull()
    }

    /** proposal 消息 → 提案载荷；非 proposal/解析失败返回 null。 */
    fun proposalOf(message: AiMessageDto): AiProposalPayload? =
        if (message.kind == KIND_PROPOSAL) parseProposal(message.payload) else null

    // ---- Kind / Role 常量（与后端枚举对齐） ----
    const val KIND_TEXT = "text"
    const val KIND_PROPOSAL = "proposal"
    const val KIND_TEST_RESULT = "test_result"
    const val KIND_ERROR = "error"
    const val ROLE_USER = "user"
    const val ROLE_ASSISTANT = "assistant"
    const val ROLE_STATUS = "status"

    // ---- Run 状态（Running/Succeeded/Failed/WaitingConfirm/Cancelled，完成态叫 Succeeded 无 Completed） ----
    const val RUN_RUNNING = "Running"

    // ---- 提案状态 → 中文标签（Pending/Applied/Discarded/Stale） ----
    fun proposalStatusLabel(status: String?): String = when (status) {
        "Pending" -> "待确认"
        "Applied" -> "已应用"
        "Discarded" -> "已忽略"
        "Stale" -> "已过期"
        else -> "未知"
    }

    /** 按钮组显隐判定：仅 Pending 可操作。 */
    fun proposalActionable(status: String?): Boolean = status == "Pending"

    // ---- 试运行状态 → 中文标签（None/Running/Passed/Failed/Timeout） ----
    fun testStatusLabel(status: String?): String = when (status) {
        "None" -> "未试运行"
        "Running" -> "试运行中"
        "Passed" -> "试运行通过"
        "Failed" -> "试运行失败"
        "Timeout" -> "超时"
        else -> "未知"
    }

    /** Applied 版本短号（前 8 位），无版本返回 null。 */
    fun versionShort(appliedVersionId: String?): String? =
        appliedVersionId?.takeIf { it.isNotBlank() }?.take(8)

    /** 默认模型副标题：未配置出引导；SupportsTools 按 int 三态（0 未知/1 支持/2 不支持）判定。 */
    fun defaultModelSubtitle(model: AiDefaultModelDto?): String {
        if (model == null || !model.configured) return "未配置默认模型，请先在 Web 端配置供应商与模型"
        val toolMode = when (model.supportsTools) {
            1 -> "工具模式"
            2 -> "JSON 模式"
            else -> "工具模式(未探测)"
        }
        val ctx = if (model.contextWindow > 0) " · ${model.contextWindow / 1000}k 上下文" else ""
        return "${model.providerName} / ${model.displayName ?: model.modelId}$ctx · $toolMode"
    }
}
