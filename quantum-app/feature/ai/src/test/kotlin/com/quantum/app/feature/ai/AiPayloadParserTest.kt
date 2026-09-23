package com.quantum.app.feature.ai

import com.quantum.app.core.network.dto.AiDefaultModelDto
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** 双层 JSON 载荷解析与 UI 状态映射（对齐后端 Payload 契约，JSON 用字面量避免测试内再声明 @Serializable）。 */
class AiPayloadParserTest {

    private fun proposalJson(status: String = "Pending", testStatus: String = "None", appliedVersionId: String? = null): String {
        val version = appliedVersionId?.let { "\"$it\"" } ?: "null"
        return """
            {"Id":"p1","FileName":"demo.cs","Summary":"修复空引用","Status":"$status",
             "TestStatus":"$testStatus","TestRunLogId":null,"BaseHash":"b","NewHash":"n",
             "Diagnostics":null,"AppliedVersionId":$version,"CreateTime":"2026-09-21 10:00:00"}
        """.trimIndent()
    }

    @Test
    fun proposalPayload_doubleLayerJson_parses() {
        val payload = AiPayloadParser.parseProposal(proposalJson())
        assertNotNull(payload)
        assertEquals("p1", payload!!.id)
        assertEquals("demo.cs", payload.fileName)
        assertEquals("Pending", payload.status)
    }

    @Test
    fun badJson_returnsNull_notCrash() {
        assertNull(AiPayloadParser.parseProposal("不是JSON{"))
        assertNull(AiPayloadParser.parseProposal(null))
        assertNull(AiPayloadParser.parseTestResult("{\"Broken\":"))
    }

    @Test
    fun testResultPayload_parsesWithLogId() {
        val text = """
            {"ProposalId":"p1","Status":"Passed","DurationMs":1500,"HasException":false,
             "LogId":"log-9","Message":"试运行通过","LogTail":"done"}
        """.trimIndent()
        val payload = AiPayloadParser.parseTestResult(text)
        assertNotNull(payload)
        assertEquals("log-9", payload!!.logId)
        assertEquals(1500, payload.durationMs)
    }

    @Test
    fun diagnostics_parses() {
        val text = """{"Blocked":[{"Line":3,"Code":"G001","Message":"禁止进程启动"}],"Errors":[],"Warnings":[]}"""
        val diag = AiPayloadParser.parseDiagnostics(text)
        assertEquals(1, diag!!.blocked.size)
        assertEquals(3, diag.blocked[0].line)
        assertNull(AiPayloadParser.parseDiagnostics("xx"))
    }

    @Test
    fun statusLabel_mapping() {
        assertEquals("待确认", AiPayloadParser.proposalStatusLabel("Pending"))
        assertEquals("已应用", AiPayloadParser.proposalStatusLabel("Applied"))
        assertEquals("已忽略", AiPayloadParser.proposalStatusLabel("Discarded"))
        assertEquals("已过期", AiPayloadParser.proposalStatusLabel("Stale"))
        assertEquals("未知", AiPayloadParser.proposalStatusLabel(null))

        assertEquals("未试运行", AiPayloadParser.testStatusLabel("None"))
        assertEquals("试运行中", AiPayloadParser.testStatusLabel("Running"))
        assertEquals("试运行通过", AiPayloadParser.testStatusLabel("Passed"))
        assertEquals("试运行失败", AiPayloadParser.testStatusLabel("Failed"))
        assertEquals("超时", AiPayloadParser.testStatusLabel("Timeout"))
    }

    @Test
    fun onlyPendingIsActionable() {
        assertTrue(AiPayloadParser.proposalActionable("Pending"))
        assertFalse(AiPayloadParser.proposalActionable("Applied"))
        assertFalse(AiPayloadParser.proposalActionable("Discarded"))
        assertFalse(AiPayloadParser.proposalActionable("Stale"))
    }

    @Test
    fun appliedVersion_showsShortId() {
        assertEquals("abcdef12", AiPayloadParser.versionShort("abcdef1234567890"))
        assertNull(AiPayloadParser.versionShort(null))
        assertNull(AiPayloadParser.versionShort(""))
    }

    @Test
    fun defaultModelSubtitle_unconfigured_givesGuide() {
        val unconfigured = AiDefaultModelDto(providerName = null, modelId = null)
        assertEquals("未配置默认模型，请先在 Web 端配置供应商与模型", AiPayloadParser.defaultModelSubtitle(unconfigured))
        assertEquals("未配置默认模型，请先在 Web 端配置供应商与模型", AiPayloadParser.defaultModelSubtitle(null))
    }

    @Test
    fun defaultModelSubtitle_toolsTriState() {
        // SupportsTools 是 int 三态：1=工具模式 / 2=JSON 模式 / 0=未探测（非布尔）
        val tools = AiDefaultModelDto(
            providerName = "中转", modelId = "glm-5.3", displayName = "GLM",
            contextWindow = 128000, supportsTools = 1
        )
        assertEquals("中转 / GLM · 128k 上下文 · 工具模式", AiPayloadParser.defaultModelSubtitle(tools))
        val jsonMode = tools.copy(supportsTools = 2)
        assertEquals("中转 / GLM · 128k 上下文 · JSON 模式", AiPayloadParser.defaultModelSubtitle(jsonMode))
        val unknown = tools.copy(supportsTools = 0)
        assertEquals("中转 / GLM · 128k 上下文 · 工具模式(未探测)", AiPayloadParser.defaultModelSubtitle(unknown))
        val noCtx = tools.copy(contextWindow = 0)
        assertEquals("中转 / GLM · 工具模式", AiPayloadParser.defaultModelSubtitle(noCtx))
    }
}
