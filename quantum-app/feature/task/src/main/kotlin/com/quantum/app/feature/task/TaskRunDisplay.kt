package com.quantum.app.feature.task

import com.quantum.app.core.network.dto.TaskRunRowDto
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * 执行记录展示口径（纯函数，JVM 直测，不依赖 Compose）：
 * 与 Web 端 `quantum-web/src/utils/taskRun.js` 保持同一套文案与色档，避免双端各说一套。
 * 色档（[RunTone]）只描述**执行终态**，绝不代表 HTTP 请求是否成功——颜色一律在 Compose 侧按档解析。
 */
enum class RunTone { Ok, Danger, Warn, Accent, Muted }

/** 状态展示元数据：未知状态原样显示（不猜测终态），文案缺失时也不给「成功」。 */
data class RunStatusMeta(val label: String, val tone: RunTone)

object TaskRunDisplay {

    /** 服务端时间格式（全局 DateFormatString；UTC 值不带时区标记）。 */
    const val SERVER_PATTERN = "yyyy-MM-dd HH:mm:ss"

    /** 列表/详情展示格式（与 Web 端 formatUtcToLocal 同为本地时间可读串）。 */
    const val DISPLAY_PATTERN = "yyyy-MM-dd HH:mm:ss"

    private val STATUS_META = mapOf(
        "Succeeded" to RunStatusMeta("成功", RunTone.Ok),
        "Failed" to RunStatusMeta("执行异常", RunTone.Danger),
        "Rejected" to RunStatusMeta("已拒绝执行", RunTone.Danger),
        "Canceled" to RunStatusMeta("已取消", RunTone.Warn),
        "Interrupted" to RunStatusMeta("中断/未知", RunTone.Warn),
        "Running" to RunStatusMeta("执行中", RunTone.Accent),
        "Pending" to RunStatusMeta("待执行", RunTone.Muted)
    )

    /** 状态筛选页签（顺序即 App 页签顺序；value = 后端枚举名，null = 全部）。 */
    val STATUS_FILTERS: List<Pair<String?, String>> = listOf(
        null to "全部",
        "Pending" to "待执行",
        "Running" to "执行中",
        "Succeeded" to "成功",
        "Failed" to "执行异常",
        "Rejected" to "已拒绝执行",
        "Canceled" to "已取消",
        "Interrupted" to "中断/未知"
    )

    private val TRIGGER_LABEL = mapOf(
        "Manual" to "手动",
        "Cron" to "定时",
        "Command" to "指令",
        "OpenTrigger" to "外触",
        "Shadow" to "AI 试运行",
        "Retry" to "自动重试"
    )

    /** 状态 → 中文文案与色档；服务端新增枚举值时回落为「原文 + Muted」，不硬编成成功或失败。 */
    fun statusMeta(status: String?): RunStatusMeta =
        STATUS_META[status] ?: RunStatusMeta(status?.takeIf { it.isNotBlank() } ?: "未知", RunTone.Muted)

    /** 触发源 → 中文；未知值原样显示，缺失给「-」（不给「未知」占位，避免被当成一种触发方式）。 */
    fun triggerLabel(source: String?): String =
        TRIGGER_LABEL[source] ?: source?.takeIf { it.isNotBlank() } ?: "-"

    /**
     * 耗时：毫秒级保留两位小数，超过 1 分钟按「分+秒」；起止时间缺失（Pending/Running）给「-」，
     * **不给 0**——把「未知」画成「0 毫秒」是最容易误导人的写法。
     */
    fun formatElapsed(ms: Long?): String {
        val value = ms ?: return "-"
        if (value < 0) return "-"
        return when {
            value < 1_000 -> "$value 毫秒"
            value < 60_000 -> String.format(Locale.US, "%.2f 秒", value / 1000.0)
            // 秒位取整（不四舍五入到分）：否则 119_999ms 会被写成「1 分 60 秒」
            else -> "${value / 60_000} 分 ${(value % 60_000) / 1_000} 秒"
        }
    }

    /**
     * 安全摘要兜底：没有摘要时只说终态，不猜测原因（服务端已脱敏截断，App 侧只做展示）。
     */
    fun summaryOf(status: String?, safeSummary: String?): String =
        safeSummary?.trim()?.takeIf { it.isNotEmpty() } ?: statusMeta(status).label

    /** 时间轴排序：按 Attempt 升序（服务端已如此返回，客户端再兜一次，避免链序变动时时间轴错乱）。 */
    fun sortedAttempts(rows: List<TaskRunRowDto>): List<TaskRunRowDto> = rows.sortedBy { it.attempt }

    /** 「第 N 次尝试」徽标：仅 Attempt>1（即重试行）才给，首次尝试不占版面。 */
    fun attemptBadge(attempt: Int): String? = if (attempt > 1) "第 $attempt 次尝试" else null

    /**
     * UTC 串 → 设备时区可读串。后端持久化 UTC 而 Newtonsoft 输出不带时区标记，
     * 直接当本地串显示会整体偏 8 小时（Beijing 设备），故按 UTC 解读再转本地；
     * 解析不出来时原样返回（不把「读不懂」画成「-」，与 Web 端 formatUtcToLocal 同口径）。
     */
    fun formatUtcToLocal(
        raw: String?,
        zone: ZoneId = ZoneId.systemDefault(),
        pattern: String = DISPLAY_PATTERN
    ): String {
        val text = raw?.trim()?.takeIf { it.isNotEmpty() } ?: return "-"
        val instant = parseAsUtc(text) ?: return text
        return DateTimeFormatter.ofPattern(pattern).withZone(zone).format(instant)
    }

    /** 缺失时间的占位：列表里「开始时间」取不到时按受理时间兜底（两者通常同值）。 */
    fun startedAtOrCreated(startedAtUtc: String?, createdAtUtc: String?): String? =
        startedAtUtc?.takeIf { it.isNotBlank() } ?: createdAtUtc

    private fun parseAsUtc(text: String): Instant? {
        // "yyyy-MM-dd HH:mm:ss"（后端全局 DateFormatString，无时区标记）补 Z 按 UTC 解读；
        // 已带 Z / z 的 ISO 串统一成大写 Z；带显式偏移的串交给 OffsetDateTime
        val normalized = text.replace(' ', 'T')
        val iso = if (normalized.endsWith("Z") || normalized.endsWith("z")) {
            normalized.dropLast(1) + "Z"
        } else {
            "${normalized}Z"
        }
        return runCatching { Instant.parse(iso) }.getOrNull()
            ?: runCatching { java.time.OffsetDateTime.parse(text).toInstant() }.getOrNull()
    }
}
