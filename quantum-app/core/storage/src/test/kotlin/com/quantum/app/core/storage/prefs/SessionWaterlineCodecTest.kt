package com.quantum.app.core.storage.prefs

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 会话已读水位编解码：默认会话空串键与 "_" 占位键必须对称往返。
 * 历史缺陷：编码侧漏归一空键 → 写任一任务会话水位时把默认会话条目写成 ":seq"，
 * 下轮解码被丢弃 → 默认会话全部消息误判未读（角标 99+）。
 */
class SessionWaterlineCodecTest {

    @Test
    fun roundTrip_PreservesDefaultSessionEntry() {
        val map = mapOf("" to 800L, "task-a" to 620L)

        val decoded = SessionWaterlineCodec.decode(SessionWaterlineCodec.encode(map))

        assertEquals(800L, decoded[""])
        assertEquals(620L, decoded["task-a"])
    }

    @Test
    fun encode_UsesPlaceholderForEmptyKey_NeverBareColon() {
        val encoded = SessionWaterlineCodec.encode(mapOf("" to 800L))

        assertEquals("_:800", encoded)
        // 空键条目（":800"）在下轮解码会被当畸形丢弃——编码绝不能产出任何以冒号开头的条目
        assertTrue("不应有空键条目：$encoded", !encoded.startsWith(":") && !encoded.contains(",:"))
    }

    @Test
    fun decode_RestoresPlaceholderToEmptyKey_AndSkipsMalformed() {
        val decoded = SessionWaterlineCodec.decode("_:800,task-b:621,:900,,task-c:notanumber")

        assertEquals(800L, decoded[""])
        assertEquals(621L, decoded["task-b"])
        assertEquals(2, decoded.size)
    }

    @Test
    fun decode_NullOrEmpty_ReturnsEmptyMap() {
        assertTrue(SessionWaterlineCodec.decode(null).isEmpty())
        assertTrue(SessionWaterlineCodec.decode("").isEmpty())
    }

    /**
     * 回归：看过任一任务会话再回列表，默认会话角标变 99+。
     * 写路径顺手 `remove("")` 清理「空键残留」时删掉的正是解码后的默认会话条目。
     */
    @Test
    fun advance_TaskSession_KeepsDefaultSessionEntry() {
        val after = SessionWaterlineCodec.advance("_:800", "task-a", 620L)

        val decoded = SessionWaterlineCodec.decode(after)
        assertEquals(800L, decoded[""])
        assertEquals(620L, decoded["task-a"])
    }

    @Test
    fun advance_DefaultSession_KeepsTaskSessionEntries() {
        val after = SessionWaterlineCodec.advance("_:800,task-a:620", "", 900L)

        val decoded = SessionWaterlineCodec.decode(after)
        assertEquals(900L, decoded[""])
        assertEquals(620L, decoded["task-a"])
    }

    @Test
    fun advance_NotMovingForward_ReturnsNull() {
        assertNull(SessionWaterlineCodec.advance("_:800,task-a:620", "task-a", 620L))
        assertNull(SessionWaterlineCodec.advance("_:800", "", 700L))
        assertNull(SessionWaterlineCodec.advance(null, "task-a", 0L))
    }

    @Test
    fun advance_DropsLegacyMalformedEntries_WithoutLosingLiveOnes() {
        // 旧格式 ":900"（空键）与空段由解码丢弃；推进后落盘串不再含畸形条目
        val after = SessionWaterlineCodec.advance("_:800,:900,,task-b:621", "task-a", 700L)

        assertTrue("不应再产出空键条目：$after", !after!!.startsWith(":") && !after.contains(",:"))
        val decoded = SessionWaterlineCodec.decode(after)
        assertEquals(800L, decoded[""])
        assertEquals(621L, decoded["task-b"])
        assertEquals(700L, decoded["task-a"])
    }

    // ==================================================================== §4-13 清空/删除会话时摘除水位键

    @Test
    fun prune_TaskSession_KeepsOthersIncludingDefault() {
        val after = SessionWaterlineCodec.prune("_:800,task-a:620", "task-a")

        val decoded = SessionWaterlineCodec.decode(after)
        assertEquals("默认会话条目必须保留", 800L, decoded[""])
        assertTrue("task-a 应被摘除", !decoded.containsKey("task-a"))
    }

    @Test
    fun prune_DefaultSession_RemovesPlaceholderEntry_KeepsTasks() {
        val after = SessionWaterlineCodec.prune("_:800,task-a:620", "")

        val decoded = SessionWaterlineCodec.decode(after)
        assertTrue("默认会话（空串键）应被摘除", !decoded.containsKey(""))
        assertEquals("任务会话条目必须保留", 620L, decoded["task-a"])
    }

    @Test
    fun prune_AbsentKey_ReturnsNull() {
        assertNull(SessionWaterlineCodec.prune("_:800,task-a:620", "ghost"))
        assertNull(SessionWaterlineCodec.prune(null, "task-a"))
    }

    @Test
    fun prune_LastEntry_YieldsEmptyString_DecodesToEmptyMap() {
        val after = SessionWaterlineCodec.prune("_:800", "")
        assertEquals("", after)
        assertTrue(SessionWaterlineCodec.decode(after).isEmpty())
    }
}
