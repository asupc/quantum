package com.quantum.app.core.media

/** 双击快进/快退分区（bilibili 风格）：左 1/3 快退、右 1/3 快进、中间播放/暂停。 */
enum class SeekZone { BACKWARD, CENTER, FORWARD }

/** 手势快进的纯逻辑：分区判定与目标进度计算（全屏播放器用，可单测）。 */
object MediaSeek {
    fun zoneOf(x: Float, width: Float): SeekZone = when {
        width <= 0f -> SeekZone.CENTER
        x < width / 3f -> SeekZone.BACKWARD
        x > width * 2f / 3f -> SeekZone.FORWARD
        else -> SeekZone.CENTER
    }

    /** 目标进度（钳制到 [0, duration]）；duration 未知（<=0）返回 null 表示不可 seek。 */
    fun targetPositionOf(currentMs: Long, durationMs: Long, deltaMs: Long): Long? {
        if (durationMs <= 0) {
            return null
        }
        return (currentMs + deltaMs).coerceIn(0L, durationMs)
    }
}
