package com.quantum.app.core.media

/**
 * 播放时长展示格式化：毫秒 → m:ss / h:mm:ss（非法/未知时长显示 --:--）。
 * 全屏播放器与悬浮控制面板共用，纯 JVM 可单测。
 */
object MediaFormat {
    fun duration(ms: Long): String {
        if (ms <= 0) {
            return "--:--"
        }
        val totalSeconds = ms / 1000
        val h = totalSeconds / 3600
        val m = (totalSeconds % 3600) / 60
        val s = totalSeconds % 60
        return if (h > 0) {
            "%d:%02d:%02d".format(h, m, s)
        } else {
            "%d:%02d".format(m, s)
        }
    }
}
