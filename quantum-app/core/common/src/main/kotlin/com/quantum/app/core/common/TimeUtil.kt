package com.quantum.app.core.common

import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * 服务端日期固定 "yyyy-MM-dd HH:mm:ss"（无时区、服务器本地时间），容器 TZ=Asia/Shanghai。
 * 解析/格式化一律按该固定时区，**不受设备时区影响**——派生 epoch（如令牌过期、排序、相对时间）
 * 只有锚定服务端时区才与服务端 System.currentTimeMillis 同域可比；用设备默认时区会在跨时区设备上偏掉整段时差。
 * 全仓库服务端时间串的解析/格式化唯一入口，勿再各处 new SimpleDateFormat。
 */
object TimeUtil {
    const val SERVER_PATTERN = "yyyy-MM-dd HH:mm:ss"

    /** 服务端时区（与部署容器 TZ 一致）。 */
    val SERVER_ZONE: ZoneId = ZoneId.of("Asia/Shanghai")

    // DateTimeFormatter 不可变且线程安全，作单例复用（SimpleDateFormat 非线程安全，旧写法每处各 new 一份）
    private val formatter: DateTimeFormatter =
        DateTimeFormatter.ofPattern(SERVER_PATTERN).withZone(SERVER_ZONE)

    /** 服务端时间串 → epoch 毫秒（按固定服务端时区）；解析失败返回 null。 */
    fun parse(raw: String?): Long? = raw?.trim()?.takeIf { it.isNotEmpty() }?.let { text ->
        runCatching {
            LocalDateTime.parse(text, formatter).atZone(SERVER_ZONE).toInstant().toEpochMilli()
        }.getOrNull()
    }

    /** epoch 毫秒 → 服务端时间串（按固定服务端时区）。 */
    fun format(epochMillis: Long): String =
        Instant.ofEpochMilli(epochMillis).atZone(SERVER_ZONE).format(formatter)

    /** 当前时刻的服务端风格串（本地落库/展示用）。 */
    fun nowServerStyle(): String = format(System.currentTimeMillis())

    /** 用于展示的原串（null 安全）。 */
    fun display(raw: String?): String = raw.orEmpty()
}
