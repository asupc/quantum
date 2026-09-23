package com.quantum.app.core.common

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** 服务器时间原串（yyyy-MM-dd HH:mm:ss，无时区）解析/展示。 */
class TimeUtilTest {

    @Test
    fun roundTripParseAndFormat() {
        val epoch = TimeUtil.parse("2026-09-14 12:30:00") ?: error("parse failed")
        assertEquals("2026-09-14 12:30:00", TimeUtil.format(epoch))
    }

    @Test
    fun invalidInputReturnsNull() {
        assertNull(TimeUtil.parse(null))
        assertNull(TimeUtil.parse("not-a-date"))
    }

    @Test
    fun displayPassesThrough() {
        assertEquals("2026-09-14 08:00:00", TimeUtil.display("2026-09-14 08:00:00"))
        assertEquals("", TimeUtil.display(null))
    }

    /**
     * 固定服务端时区（Asia/Shanghai=UTC+8，1970 无夏令时）：解析/格式化不受测试 JVM 默认时区影响。
     * 1970-01-01 08:00 上海墙钟 = epoch 0；上海 00:00 = -8h。旧写法用设备默认时区，跨时区设备会整段偏掉。
     */
    @Test
    fun parsesAndFormatsInServerZoneNotDeviceZone() {
        assertEquals(0L, TimeUtil.parse("1970-01-01 08:00:00"))
        assertEquals(-28_800_000L, TimeUtil.parse("1970-01-01 00:00:00"))
        assertEquals("1970-01-01 08:00:00", TimeUtil.format(0L))
    }
}
