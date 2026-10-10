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

    // ==================================================================== F11 分隔符碰撞（会话名可含 , : %）

    @Test
    fun keyContainingDelimiters_RoundTrips_WithoutCorruptingOthers() {
        // 会话名合法含 "," / ":"（服务端 NormalizeSessionName 只 Trim，未禁用分隔符）：
        // 裸拼串会把 "task,a:5" 拆成伪条目、把 task 的水位串到 "a"，写路径必须转义
        val map = mapOf("task,a" to 5L, "b:c" to 6L, "d%e" to 7L, "plain" to 8L, "" to 9L)

        val decoded = SessionWaterlineCodec.decode(SessionWaterlineCodec.encode(map))

        assertEquals("含逗号的会话键必须完整往返，不得拆成伪条目", 5L, decoded["task,a"])
        assertEquals("含冒号的会话键必须完整往返", 6L, decoded["b:c"])
        assertEquals("含百分号的会话键必须完整往返（% 不可二次解码）", 7L, decoded["d%e"])
        assertEquals(8L, decoded["plain"])
        assertEquals("默认会话占位键不受影响", 9L, decoded[""])
        assertEquals("不得产生任何伪会话条目：$decoded", 5, decoded.size)
    }

    @Test
    fun keyContainingDelimiters_AdvanceAndPrune_TargetOnlyOwnEntry() {
        // 推进含逗号键：只写自己的水位，绝不污染任务/默认会话
        val afterAdvance = SessionWaterlineCodec.advance("_:800,task-a:620", "task,a", 700L)
        val decoded = SessionWaterlineCodec.decode(afterAdvance)
        assertEquals("task-a 不得被 task,a 的写入串到", 620L, decoded["task-a"])
        assertEquals(800L, decoded[""])
        assertEquals(700L, decoded["task,a"])

        // 摘除含逗号键：只摘自己，其余原样
        val afterPrune = SessionWaterlineCodec.prune(afterAdvance, "task,a")
        val pruned = SessionWaterlineCodec.decode(afterPrune)
        assertTrue(!pruned.containsKey("task,a"))
        assertEquals(620L, pruned["task-a"])
        assertEquals(800L, pruned[""])
    }

    @Test
    fun legacyUnescapedValue_StillDecodes() {
        // 存量旧格式（未转义、键内无分隔符）与前代畸形条目混存：解析口径不变
        val decoded = SessionWaterlineCodec.decode("_:800,:900,,task-b:621,task-c:notanumber")

        assertEquals(800L, decoded[""])
        assertEquals(621L, decoded["task-b"])
        assertEquals("空键（:900）/空段/非数字项照旧丢弃", 2, decoded.size)
    }

    // ==================================================================== F11b 旧格式键含 %XX 必须逐字节保真

    @Test
    fun legacyKeyContainingPercentSequence_DecodesByteIdentically() {
        // 旧版本转义改造前写出的串：会话名「50%20」的键原样落盘为 "50%20"。
        // 读路径若对任何合法 %XX 都还原，会被静默改名为 "50 "（丢该会话水位、并生成幽灵条目）。
        val decoded = SessionWaterlineCodec.decode("50%20:700,AA%2CBB:5")

        assertEquals("旧格式键必须逐字节读回，不得还原 %XX", 700L, decoded["50%20"])
        assertEquals(5L, decoded["AA%2CBB"])
        assertTrue("不得产出被解码后的幽灵键：$decoded", !decoded.containsKey("50 ") && !decoded.containsKey("AA,BB"))
        assertEquals(2, decoded.size)
    }

    @Test
    fun legacyKeyContainingPercentSequence_AdvanceAndPrune_DoNotRenameOrCorrupt() {
        // 写回路径同样不得把旧键「顺手转义/还原」——推进别的会话后旧键原文与水位都还在
        val afterAdvance = SessionWaterlineCodec.advance("50%20:700", "other", 800L)
        assertEquals("50%20:700,other:800", afterAdvance)
        assertEquals("推进后旧键水位不得丢失", 700L, SessionWaterlineCodec.decode(afterAdvance)["50%20"])

        // 推进旧键自身：键名不变（不因一次写入被改名成转义形态）
        assertEquals("50%20:900", SessionWaterlineCodec.advance("50%20:700", "50%20", 900L))

        // 摘除别的会话：旧键原文完整保留
        assertEquals("AA%2CBB:5", SessionWaterlineCodec.prune("AA%2CBB:5,other:9", "other"))
    }

    @Test
    fun escapedFormat_RoundTripsKeysWithDelimitersAndPercent() {
        // 新格式（旧格式表达不了时启用）往返必须精确：含分隔符 / 含 % / 含 %XX 字面量
        listOf("a,b", "x:y", "p%q", "p%2Cq").forEach { key ->
            val decoded = SessionWaterlineCodec.decode(SessionWaterlineCodec.encode(mapOf(key to 42L)))
            assertEquals("键 $key 必须精确往返", 42L, decoded[key])
            assertEquals("键 $key 不得产出伪条目：$decoded", 1, decoded.size)
        }
    }

    @Test
    fun storageKey_SeesPercentEncodedAndLiteralFormsAsDistinct() {
        // 转义后的 "a%2Cb" 与含逗号的 "a,b" 是两个不同会话，落盘键必须不同（不可撞车）
        assertTrue(
            "转义键不得与字面目击键相同",
            SessionWaterlineCodec.storageKey("a%2Cb") != SessionWaterlineCodec.storageKey("a,b")
        )
        // 字面会话名 "_" 与默认会话占位键同样必须区分
        assertTrue(
            "字面 _ 键不得与默认会话占位键撞车",
            SessionWaterlineCodec.storageKey("_") != SessionWaterlineCodec.storageKey("")
        )
    }

    @Test
    fun legacyAndEscapedForms_CoexistWithoutLosingEitherEntry() {
        // 旧格式条目与新格式条目同串混存（升级期必然形态）：两边都读得回来
        val v2 = SessionWaterlineCodec.encode(mapOf("task,a" to 5L))
        val decoded = SessionWaterlineCodec.decode("_:800,$v2")

        assertEquals(800L, decoded[""])
        assertEquals(5L, decoded["task,a"])
        assertEquals(2, decoded.size)
    }
}
