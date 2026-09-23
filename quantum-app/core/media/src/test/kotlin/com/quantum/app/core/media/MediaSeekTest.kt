package com.quantum.app.core.media

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** 双击快进/快退分区与目标进度计算。 */
class MediaSeekTest {

    @Test
    fun `zoneOf 左中右三区`() {
        assertEquals(SeekZone.BACKWARD, MediaSeek.zoneOf(0f, 900f))
        assertEquals(SeekZone.BACKWARD, MediaSeek.zoneOf(299f, 900f))
        // 边界点（恰为 1/3、2/3 宽度）归中间区：严格小于/大于才进左右区
        assertEquals(SeekZone.CENTER, MediaSeek.zoneOf(300f, 900f))
        assertEquals(SeekZone.CENTER, MediaSeek.zoneOf(599f, 900f))
        assertEquals(SeekZone.CENTER, MediaSeek.zoneOf(600f, 900f))
        assertEquals(SeekZone.FORWARD, MediaSeek.zoneOf(601f, 900f))
        assertEquals(SeekZone.FORWARD, MediaSeek.zoneOf(899f, 900f))
    }

    @Test
    fun `zoneOf 宽度非法按中间区处理`() {
        assertEquals(SeekZone.CENTER, MediaSeek.zoneOf(10f, 0f))
        assertEquals(SeekZone.CENTER, MediaSeek.zoneOf(10f, -5f))
    }

    @Test
    fun `targetPositionOf 钳制到时长范围`() {
        assertEquals(0L, MediaSeek.targetPositionOf(currentMs = 5_000L, durationMs = 120_000L, deltaMs = -10_000L))
        assertEquals(120_000L, MediaSeek.targetPositionOf(currentMs = 115_000L, durationMs = 120_000L, deltaMs = 10_000L))
        assertEquals(70_000L, MediaSeek.targetPositionOf(currentMs = 60_000L, durationMs = 120_000L, deltaMs = 10_000L))
    }

    @Test
    fun `targetPositionOf 时长未知返回 null`() {
        assertNull(MediaSeek.targetPositionOf(currentMs = 1_000L, durationMs = 0L, deltaMs = 10_000L))
        assertNull(MediaSeek.targetPositionOf(currentMs = 1_000L, durationMs = -1L, deltaMs = 10_000L))
    }
}
