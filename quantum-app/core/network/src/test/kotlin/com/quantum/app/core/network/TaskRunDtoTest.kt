package com.quantum.app.core.network

import com.quantum.app.core.common.ApiException
import com.quantum.app.core.network.api.EnvelopeDto
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.PageResultDto
import com.quantum.app.core.network.dto.TaskRunDetailDto
import com.quantum.app.core.network.dto.TaskRunRowDto
import kotlinx.serialization.json.Json
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * 执行记录 DTO 解码（一期 G2）：服务端 Newtonsoft PascalCase + 枚举名输出为字符串，
 * 逐字段显式 @SerialName 的映射在这里钉死——漏标或拼错只会静默取默认值，运行时看不出问题，
 * 所以必须用真实形状的 JSON 断言，而不是等真机上看空白列表。
 * Json 配置与 NetworkModule 提供的一致（ignoreUnknownKeys/explicitNulls/coerceInputValues）。
 */
class TaskRunDtoTest {

    private val json = Json {
        ignoreUnknownKeys = true
        explicitNulls = false
        coerceInputValues = true
    }

    @Test
    fun pageDecodesPascalCaseRows() {
        val env = json.decodeFromString<EnvelopeDto<PageResultDto<TaskRunRowDto>>>(
            """
            {
              "Code": 200,
              "Message": "Success",
              "Data": {
                "Data": [
                  {
                    "Id": "run-1",
                    "RootRunId": "run-1",
                    "Attempt": 2,
                    "TaskId": "t-1",
                    "TaskName": "好价监控",
                    "ScriptFile": "quantum/haojia.cs",
                    "TriggerSource": "Cron",
                    "Status": "Failed",
                    "FailureCode": "ScriptException",
                    "SafeSummary": "脚本第 42 行抛出异常",
                    "StartedAtUtc": "2026-09-26 01:02:03",
                    "FinishedAtUtc": "2026-09-26 01:02:05",
                    "NextAttemptAtUtc": null,
                    "CreatedAtUtc": "2026-09-26 01:02:00",
                    "IsRetry": true,
                    "CancelReason": null,
                    "ElapsedMs": 2000
                  }
                ],
                "TotalCount": 1,
                "Page": 1,
                "PageSize": 20
              }
            }
            """.trimIndent()
        )
        val page = env.unwrap()
        assertEquals(1, page.totalCount)
        val row = page.data.single()
        assertEquals("run-1", row.id)
        assertEquals("run-1", row.rootRunId)
        assertEquals(2, row.attempt)
        assertEquals("t-1", row.taskId)
        assertEquals("好价监控", row.taskName)
        assertEquals("Cron", row.triggerSource)
        assertEquals("Failed", row.status)
        assertEquals("ScriptException", row.failureCode)
        assertEquals("脚本第 42 行抛出异常", row.safeSummary)
        assertEquals("2026-09-26 01:02:03", row.startedAtUtc)
        assertEquals("2026-09-26 01:02:05", row.finishedAtUtc)
        assertNull(row.nextAttemptAtUtc)
        assertEquals("2026-09-26 01:02:00", row.createdAtUtc)
        assertTrue(row.isRetry)
        assertNull(row.cancelReason)
        assertEquals(2000L, row.elapsedMs)
    }

    @Test
    fun pendingRowWithoutOptionalValuesDecodesToNulls() {
        // Pending 行没有起止时间，服务端 ElapsedMs 算不出来给 null：不能解码成 0（会被显示成「0 毫秒」）
        val row = json.decodeFromString<TaskRunRowDto>(
            """{"Id":"run-2","Attempt":1,"Status":"Pending","TriggerSource":"Manual","ElapsedMs":null}"""
        )
        assertEquals("run-2", row.id)
        assertNull(row.elapsedMs)
        assertNull(row.startedAtUtc)
        assertNull(row.taskName)
        assertFalse(row.isRetry)
        assertEquals(1, row.attempt)
    }

    @Test
    fun detailDecodesAttemptsAndLogAvailability() {
        val env = json.decodeFromString<EnvelopeDto<TaskRunDetailDto>>(
            """
            {
              "Code": 200,
              "Message": "Success",
              "Data": {
                "Run": {"Id": "run-2", "Attempt": 1, "Status": "Canceled", "CancelReason": "脚本已变更，取消自动重试"},
                "Attempts": [
                  {"Id": "run-2", "Attempt": 1, "Status": "Canceled"},
                  {"Id": "run-3", "Attempt": 2, "Status": "Succeeded"}
                ],
                "LogAvailable": true,
                "LogId": "log-9"
              }
            }
            """.trimIndent()
        )
        val detail = env.unwrap()
        assertEquals("run-2", detail.run?.id)
        assertEquals("脚本已变更，取消自动重试", detail.run?.cancelReason)
        assertEquals(listOf(1, 2), detail.attempts.map { it.attempt })
        assertTrue(detail.logAvailable)
        assertEquals("log-9", detail.logId)
    }

    @Test
    fun detailWithoutLogShowsUnavailableAndNullLogId() {
        // 日志已清理/尚未落库：只有 LogAvailable=false + LogId=null，接口不回任何文件路径
        val detail = json.decodeFromString<TaskRunDetailDto>(
            """{"Run":{"Id":"run-4","Attempt":1,"Status":"Interrupted"},"Attempts":[],"LogAvailable":false,"LogId":null}"""
        )
        assertFalse(detail.logAvailable)
        assertNull(detail.logId)
        assertTrue(detail.attempts.isEmpty())
    }

    @Test
    fun missingOrForbiddenRunComesBackAsBusinessError() {
        // 服务端把「不存在」与「越权」统一成一条文案（不供探测 RunId），App 侧只有一条错误分支
        val env = json.decodeFromString<EnvelopeDto<TaskRunDetailDto>>(
            """{"Code":500,"Message":"执行记录不存在","Data":null}"""
        )
        val e = assertFailsWith<ApiException> { env.unwrap() }
        assertEquals("执行记录不存在", e.message)
    }
}
