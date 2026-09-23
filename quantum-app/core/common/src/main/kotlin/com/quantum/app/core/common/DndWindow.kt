package com.quantum.app.core.common

import java.time.LocalTime

/**
 * 免打扰时段（通知偏好 DndStart/DndEnd，格式 HH:mm）。
 *
 * 判定放在这里而不是直接写在推送中枢里，是为了可单测：这段逻辑决定「通知弹不弹」，
 * 一旦算错要么该响的不响、要么半夜被吵醒，且现场很难复现。
 */
object DndWindow {

    /** 服务端与 UI 约定的存储格式（两位时分，24 小时制）。 */
    private const val PATTERN = "%02d:%02d"

    /**
     * 宽松解析：接受 "9:00"/"09:00"（历史数据里可能有），范围非法（如 "25:99"）视为不可解析。
     * 返回 null 表示「这条时段不可用」，调用方按「不抑制」处理（fail-open：宁可多弹一条，
     * 也不要因为一条脏数据把所有通知永久静音）。
     */
    fun parse(raw: String?): LocalTime? {
        val text = raw?.trim().orEmpty()
        if (text.isEmpty()) {
            return null
        }
        val parts = text.split(':')
        if (parts.size != 2) {
            return null
        }
        val hour = parts[0].toIntOrNull() ?: return null
        val minute = parts[1].toIntOrNull() ?: return null
        if (hour !in 0..23 || minute !in 0..59) {
            return null
        }
        return LocalTime.of(hour, minute)
    }

    /** "HH:mm" 规范化（时间选择器回填用，保证落库/上报格式统一）。 */
    fun format(time: LocalTime): String = PATTERN.format(time.hour, time.minute)

    /**
     * 当前时刻是否落在免打扰区间内。
     * - 起止任一缺失/不可解析 → 不抑制；
     * - 跨零点（如 23:00–07:00）按 `now >= start || now < end` 判定；
     * - 结束时刻按**半开区间**处理：到点即解除（20:00 结束意味着 20:00 起恢复通知）。
     */
    fun isActive(now: LocalTime, start: String?, end: String?): Boolean {
        val from = parse(start) ?: return false
        val to = parse(end) ?: return false
        return if (from <= to) {
            now >= from && now < to
        } else {
            now >= from || now < to
        }
    }
}
