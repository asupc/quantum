package com.quantum.app.feature.ai

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** 行级 LCS diff：对齐 Web tests/lineDiff.spec.js 全部用例语义（移植验收口径）。 */
class AiLineDiffTest {

    // ---- splitLines 边界 ----

    @Test
    fun splitLines_emptyString_givesEmptyList() {
        assertEquals(emptyList<String>(), AiLineDiff.splitLines(""))
    }

    @Test
    fun splitLines_singleNewline_givesSingleEmptyLine() {
        assertEquals(listOf(""), AiLineDiff.splitLines("\n"))
    }

    @Test
    fun splitLines_trailingNewline_isNotCounted() {
        assertEquals(listOf("a"), AiLineDiff.splitLines("a\n"))
        assertEquals(listOf("a", "b"), AiLineDiff.splitLines("a\nb\n"))
    }

    @Test
    fun splitLines_normalizesCrlfAndCr() {
        assertEquals(listOf("a", "b"), AiLineDiff.splitLines("a\r\nb"))
        assertEquals(listOf("a", "b"), AiLineDiff.splitLines("a\rb"))
    }

    // ---- diff 语义 ----

    @Test
    fun identicalContent_hasNoDifference() {
        val result = AiLineDiff.diffLines("a\nb\nc", "a\nb\nc")
        assertEquals(0, result.added)
        assertEquals(0, result.removed)
        assertTrue(result.rows.all { it.kind == AiLineDiff.RowKind.Same })
        assertFalse(result.truncated)
    }

    @Test
    fun trailingNewlineDifference_isIgnored() {
        val result = AiLineDiff.diffLines("a\nb", "a\nb\n")
        assertEquals(0, result.added + result.removed)
    }

    @Test
    fun addSingleLine() {
        val result = AiLineDiff.diffLines("a\nb", "a\nx\nb")
        assertEquals(1, result.added)
        assertEquals(0, result.removed)
        val add = result.rows.first { it.kind == AiLineDiff.RowKind.Add }
        assertEquals("x", add.text)
        assertEquals(2, add.newLine) // 新侧第 2 行
        assertEquals(null, add.oldLine)
    }

    @Test
    fun deleteSingleLine() {
        val result = AiLineDiff.diffLines("a\nb\nc", "a\nc")
        assertEquals(0, result.added)
        assertEquals(1, result.removed)
        val del = result.rows.first { it.kind == AiLineDiff.RowKind.Del }
        assertEquals("b", del.text)
        assertEquals(2, del.oldLine) // 旧侧第 2 行
        assertEquals(null, del.newLine)
    }

    @Test
    fun replaceLine_keepsLineNumbersTracked() {
        val result = AiLineDiff.diffLines("a\nb\nc", "a\nB\nc")
        assertEquals(1, result.added)
        assertEquals(1, result.removed)
        val del = result.rows.first { it.kind == AiLineDiff.RowKind.Del }
        val add = result.rows.first { it.kind == AiLineDiff.RowKind.Add }
        assertEquals(2, del.oldLine)
        assertEquals(2, add.newLine)
    }

    @Test
    fun insertShiftsFollowingLineNumbers() {
        val result = AiLineDiff.diffLines("l1\nl2", "l0\nl1\nl2")
        val add = result.rows.first { it.kind == AiLineDiff.RowKind.Add }
        assertEquals("l0", add.text)
        assertEquals(1, add.newLine)
        // 后续 same 行的两侧行号错位：旧 1/2 ↔ 新 2/3
        val same = result.rows.filter { it.kind == AiLineDiff.RowKind.Same }
        assertEquals(listOf(1 to 2, 2 to 3), same.map { it.oldLine!! to it.newLine!! })
    }

    @Test
    fun emptyFile_vsContent_isAllAdded() {
        val result = AiLineDiff.diffLines("", "x\ny")
        assertEquals(2, result.added)
        assertEquals(0, result.removed)
    }

    @Test
    fun content_vsEmptyFile_isAllDeleted() {
        val result = AiLineDiff.diffLines("x\ny", "")
        assertEquals(0, result.added)
        assertEquals(2, result.removed)
    }

    @Test
    fun oversizedMiddle_degeneratesToBlockReplace() {
        // 1600×1600 = 2.56M 格 > 2M 上限：整块替换 + truncated（对齐 Web spec 用例）
        val base = (1..1600).joinToString("\n") { "base-$it" }
        val new = (1..1600).joinToString("\n") { "new-$it" }
        val result = AiLineDiff.diffLines(base, new)
        assertTrue(result.truncated)
        assertEquals(1600, result.added)
        assertEquals(1600, result.removed)
    }

    @Test
    fun oversizedStillMatchesWhenPrefixSuffixStripped() {
        // 中段不超限（公共前后缀剥掉后只剩 1 行差异）不触发退化
        val base = (1..2000).joinToString("\n") { "same-$it" } + "\ntail"
        val new = (1..2000).joinToString("\n") { "same-$it" } + "\nTAIL"
        val result = AiLineDiff.diffLines(base, new)
        assertFalse(result.truncated)
        assertEquals(1, result.added)
        assertEquals(1, result.removed)
    }
}
