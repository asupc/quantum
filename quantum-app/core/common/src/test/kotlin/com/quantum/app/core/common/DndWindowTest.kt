package com.quantum.app.core.common

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalTime

/** 免打扰时段判定：普通区间 / 跨零点 / 边界 / 脏数据 fail-open。 */
class DndWindowTest {

    private fun at(hour: Int, minute: Int) = LocalTime.of(hour, minute)

    @Test
    fun plainWindow_SuppressesInsideOnly() {
        assertTrue(DndWindow.isActive(at(22, 30), "22:00", "23:00"))
        assertFalse(DndWindow.isActive(at(21, 59), "22:00", "23:00"))
        // 结束时刻半开：到点即恢复通知
        assertFalse(DndWindow.isActive(at(23, 0), "22:00", "23:00"))
    }

    @Test
    fun crossMidnightWindow_SuppressesOnBothSides() {
        assertTrue(DndWindow.isActive(at(23, 30), "23:00", "07:00"))
        assertTrue(DndWindow.isActive(at(0, 30), "23:00", "07:00"))
        assertTrue(DndWindow.isActive(at(6, 59), "23:00", "07:00"))
        assertFalse(DndWindow.isActive(at(7, 0), "23:00", "07:00"))
        assertFalse(DndWindow.isActive(at(12, 0), "23:00", "07:00"))
    }

    @Test
    fun missingOrMalformedTime_NeverSuppresses() {
        assertFalse(DndWindow.isActive(at(23, 30), null, "07:00"))
        assertFalse(DndWindow.isActive(at(23, 30), "23:00", null))
        assertFalse(DndWindow.isActive(at(23, 30), "", ""))
        // 脏数据（服务端旧校验只查形态，25:99 进过库）不得把通知永久静音
        assertFalse(DndWindow.isActive(at(23, 30), "25:99", "07:00"))
        assertFalse(DndWindow.isActive(at(23, 30), "abc", "07:00"))
    }

    @Test
    fun parse_AcceptsSingleDigitHour_RejectsOutOfRange() {
        assertEquals(LocalTime.of(9, 5), DndWindow.parse("9:05"))
        assertEquals(LocalTime.of(9, 5), DndWindow.parse(" 09:05 "))
        assertNull(DndWindow.parse("09:60"))
        assertNull(DndWindow.parse("24:00"))
        assertNull(DndWindow.parse("0905"))
        assertNull(DndWindow.parse(null))
    }

    @Test
    fun format_PadsToTwoDigits() {
        assertEquals("07:05", DndWindow.format(LocalTime.of(7, 5)))
        assertEquals("23:00", DndWindow.format(LocalTime.of(23, 0)))
    }
}
