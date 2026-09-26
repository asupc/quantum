package com.quantum.app.feature.task

import com.quantum.app.core.network.dto.TaskRunRowDto
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * 执行记录展示口径单测（JVM 直测，不碰 Compose）：
 * 状态→中文文案与色档、触发源文案、耗时格式化、UTC→本地时间、摘要兜底、尝试徽标、时间轴排序。
 * 时区一律显式传入（不依赖测试机默认时区），断言才可复现。
 */
class TaskRunDisplayTest {

    private val shanghai = ZoneId.of("Asia/Shanghai")

    // ---- 状态 → 中文文案与色档 ----

    @Test
    fun knownStatuses_MapToChineseLabelAndTone() {
        assertMeta("Succeeded", "成功", RunTone.Ok)
        assertMeta("Failed", "执行异常", RunTone.Danger)
        assertMeta("Rejected", "已拒绝执行", RunTone.Danger)
        assertMeta("Canceled", "已取消", RunTone.Warn)
        assertMeta("Interrupted", "中断/未知", RunTone.Warn)
        assertMeta("Running", "执行中", RunTone.Accent)
        assertMeta("Pending", "待执行", RunTone.Muted)
    }

    @Test
    fun unknownStatus_ShowsRawText_NotGuessedAsSuccess() {
        // 后端新增枚举值时不得被 App 硬编成「成功」或「执行异常」
        assertMeta("Queued", "Queued", RunTone.Muted)
        assertMeta(null, "未知", RunTone.Muted)
        assertMeta("  ", "未知", RunTone.Muted)
    }

    @Test
    fun statusFilters_CoverEveryBackendStatus() {
        // 筛选页签必须与后端 TaskRunStatus 一一对应（漏一个就有筛不出来的终态）
        val backendStatuses = listOf(
            "Pending", "Running", "Succeeded", "Failed", "Rejected", "Canceled", "Interrupted"
        )
        val filterValues = TaskRunDisplay.STATUS_FILTERS.map { it.first }.filterNotNull()
        assertEquals(backendStatuses.sorted(), filterValues.sorted())
        assertEquals("全部", TaskRunDisplay.STATUS_FILTERS.first().second)
    }

    @Test
    fun triggerLabel_MapsAndFallsBack() {
        assertEquals("手动", TaskRunDisplay.triggerLabel("Manual"))
        assertEquals("定时", TaskRunDisplay.triggerLabel("Cron"))
        assertEquals("指令", TaskRunDisplay.triggerLabel("Command"))
        assertEquals("外触", TaskRunDisplay.triggerLabel("OpenTrigger"))
        assertEquals("AI 试运行", TaskRunDisplay.triggerLabel("Shadow"))
        assertEquals("自动重试", TaskRunDisplay.triggerLabel("Retry"))
        // 未知值原样显示；缺失给「-」而不是「未知」（免得被当成一种触发方式）
        assertEquals("Webhook", TaskRunDisplay.triggerLabel("Webhook"))
        assertEquals("-", TaskRunDisplay.triggerLabel(null))
        assertEquals("-", TaskRunDisplay.triggerLabel(""))
    }

    // ---- 耗时格式化 ----

    @Test
    fun elapsed_MissingOrNegativeIsDashNotZero() {
        // Pending/Running 服务端算不出耗时：显示「-」，画成「0 毫秒」等于宣称瞬时完成
        assertEquals("-", TaskRunDisplay.formatElapsed(null))
        assertEquals("-", TaskRunDisplay.formatElapsed(-1))
    }

    @Test
    fun elapsed_BelowOneSecondKeepsMillis() {
        assertEquals("0 毫秒", TaskRunDisplay.formatElapsed(0))
        assertEquals("1 毫秒", TaskRunDisplay.formatElapsed(1))
        assertEquals("999 毫秒", TaskRunDisplay.formatElapsed(999))
    }

    @Test
    fun elapsed_SecondsKeepTwoDecimals() {
        assertEquals("1.00 秒", TaskRunDisplay.formatElapsed(1000))
        assertEquals("1.50 秒", TaskRunDisplay.formatElapsed(1500))
        assertEquals("12.30 秒", TaskRunDisplay.formatElapsed(12300))
    }

    @Test
    fun elapsed_AboveOneMinuteSplitsMinutesAndSeconds() {
        assertEquals("1 分 0 秒", TaskRunDisplay.formatElapsed(60_000))
        assertEquals("1 分 30 秒", TaskRunDisplay.formatElapsed(90_000))
        // 秒位取整而非四舍五入：119_999ms 不能写成「1 分 60 秒」
        assertEquals("1 分 59 秒", TaskRunDisplay.formatElapsed(119_999))
        assertEquals("60 分 0 秒", TaskRunDisplay.formatElapsed(3_600_000))
    }

    // ---- UTC 串 → 本地可读串 ----

    @Test
    fun utcString_IsReadAsUtcNotLocalTime() {
        // 后端持久化 UTC，经 Newtonsoft "yyyy-MM-dd HH:mm:ss" 输出且不带时区标记：
        // 必须按 UTC 解读再转本地（北京 +8），直接当本地串显示会整体偏 8 小时
        assertEquals("2026-09-26 08:00:00", TaskRunDisplay.formatUtcToLocal("2026-09-26 00:00:00", shanghai))
        assertEquals("2026-09-26 00:00:00", TaskRunDisplay.formatUtcToLocal("2026-09-26 00:00:00", ZoneId.of("UTC")))
    }

    @Test
    fun utcString_AcceptsIsoFormsWithZoneMarkerAndFraction() {
        assertEquals("2026-09-26 08:00:00", TaskRunDisplay.formatUtcToLocal("2026-09-26T00:00:00Z", shanghai))
        assertEquals("2026-09-26 08:00:00", TaskRunDisplay.formatUtcToLocal("2026-09-26 00:00:00.123Z", shanghai))
        // 带显式偏移的串按其自身偏移换算（不补 Z）
        assertEquals("2026-09-26 08:00:00", TaskRunDisplay.formatUtcToLocal("2026-09-26T08:00:00+08:00", shanghai))
    }

    @Test
    fun utcString_SupportsCompactListPattern() {
        assertEquals(
            "09-26 08:00:00",
            TaskRunDisplay.formatUtcToLocal("2026-09-26 00:00:00", shanghai, pattern = "MM-dd HH:mm:ss")
        )
    }

    @Test
    fun utcString_MissingIsDash_UnparseableStaysRaw() {
        assertEquals("-", TaskRunDisplay.formatUtcToLocal(null, shanghai))
        assertEquals("-", TaskRunDisplay.formatUtcToLocal("   ", shanghai))
        // 读不懂的串原样返回：把「未知」画成「-」会让人以为时间缺失
        assertEquals("执行时刻待定", TaskRunDisplay.formatUtcToLocal("执行时刻待定", shanghai))
    }

    @Test
    fun startedAtOrCreated_FallsBackToAcceptedTime() {
        assertEquals("2026-09-26 00:00:00", TaskRunDisplay.startedAtOrCreated("2026-09-26 00:00:00", "x"))
        assertEquals("x", TaskRunDisplay.startedAtOrCreated(null, "x"))
        assertEquals("x", TaskRunDisplay.startedAtOrCreated("  ", "x"))
        assertNull(TaskRunDisplay.startedAtOrCreated(null, null))
    }

    // ---- 摘要 / 尝试徽标 / 时间轴 ----

    @Test
    fun summary_FallsBackToStatusLabelWithoutGuessingCause() {
        assertEquals("门禁拒绝", TaskRunDisplay.summaryOf("Rejected", " 门禁拒绝 "))
        // 没有摘要时只说终态，不猜测原因
        assertEquals("执行异常", TaskRunDisplay.summaryOf("Failed", null))
        assertEquals("执行异常", TaskRunDisplay.summaryOf("Failed", "   "))
        assertEquals("Queued", TaskRunDisplay.summaryOf("Queued", null))
    }

    @Test
    fun attemptBadge_OnlyHighlightedForRetries() {
        assertNull(TaskRunDisplay.attemptBadge(1))
        assertEquals("第 2 次尝试", TaskRunDisplay.attemptBadge(2))
        assertEquals("第 4 次尝试", TaskRunDisplay.attemptBadge(4))
    }

    @Test
    fun timeline_SortedByAttemptAscending() {
        val rows = listOf(row(id = "b", attempt = 3), row(id = "a", attempt = 1), row(id = "c", attempt = 2))
        assertEquals(listOf(1, 2, 3), TaskRunDisplay.sortedAttempts(rows).map { it.attempt })
        assertEquals(emptyList<Int>(), TaskRunDisplay.sortedAttempts(emptyList()).map { it.attempt })
    }

    private fun assertMeta(status: String?, expectedLabel: String, expectedTone: RunTone) {
        val meta = TaskRunDisplay.statusMeta(status)
        assertEquals(expectedLabel, meta.label)
        assertEquals(expectedTone, meta.tone)
    }

    private fun row(id: String, attempt: Int, status: String = "Failed"): TaskRunRowDto =
        TaskRunRowDto(id = id, attempt = attempt, taskId = "t1", taskName = "示例任务", status = status)
}
