package com.quantum.app.feature.ai

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Markdown-lite 解析：围栏拆块（含未闭合）、行内代码、URL（App 新增）、空内容、折叠估计。 */
class AiMarkdownTest {

    @Test
    fun emptyContent_givesNoBlocks() {
        assertTrue(AiMarkdown.parse("").isEmpty())
        assertTrue(AiMarkdown.parse("   ").isEmpty())
    }

    @Test
    fun plainText_singleParagraph() {
        val blocks = AiMarkdown.parse("你好，AI 助手")
        assertEquals(1, blocks.size)
        val paragraph = blocks[0] as AiMarkdown.Block.Paragraph
        assertEquals(1, paragraph.spans.size)
        assertEquals("你好，AI 助手", (paragraph.spans[0] as AiMarkdown.Span.Plain).text)
    }

    @Test
    fun fencedCodeBlock_splitsAndStripsLanguage() {
        val text = "前置说明\n```csharp\nvar x = 1;\nvar y = 2;\n```\n后置说明"
        val blocks = AiMarkdown.parse(text)
        assertEquals(3, blocks.size)
        assertEquals("前置说明", ((blocks[0] as AiMarkdown.Block.Paragraph).spans[0] as AiMarkdown.Span.Plain).text)
        val code = blocks[1] as AiMarkdown.Block.Code
        assertEquals("csharp", code.language)
        assertEquals("var x = 1;\nvar y = 2;", code.text)
    }

    @Test
    fun unclosedFence_treatedAsCode() {
        val blocks = AiMarkdown.parse("说明\n```\nconsole.log(1)")
        assertEquals(2, blocks.size)
        val code = blocks[1] as AiMarkdown.Block.Code
        assertEquals("console.log(1)", code.text)
    }

    @Test
    fun inlineCode_becomesMonospaceSpan() {
        val spans = AiMarkdown.parseInline("调用 `read_script` 工具")
        assertEquals(3, spans.size)
        assertEquals("read_script", (spans[1] as AiMarkdown.Span.InlineCode).text)
    }

    @Test
    fun unclosedBacktick_staysPlain() {
        val spans = AiMarkdown.parseInline("单个 ` 反引号")
        assertEquals(1, spans.size)
        assertTrue(spans[0] is AiMarkdown.Span.Plain)
    }

    @Test
    fun urlBecomesLinkSpan() {
        // URL 链接是 App 新增能力（Web 版无）
        val spans = AiMarkdown.parseInline("参考 https://example.com/docs 文档")
        val url = spans.filterIsInstance<AiMarkdown.Span.Url>().single()
        assertEquals("https://example.com/docs", url.url)
    }

    @Test
    fun paragraphSplit_byBlankLine() {
        val blocks = AiMarkdown.parse("第一段\n\n第二段")
        assertEquals(2, blocks.size)
    }

    @Test
    fun singleNewlineInsideParagraph_keptAsNewLineSpan() {
        val spans = AiMarkdown.parseInline("行1\n行2")
        assertEquals(3, spans.size)
        assertTrue(spans[1] is AiMarkdown.Span.NewLine)
    }

    @Test
    fun estimateLines_growsWithCode_enablesFold() {
        val big = "```\n" + (1..60).joinToString("\n") { "line-$it" } + "\n```"
        val blocks = AiMarkdown.parse(big)
        assertTrue(AiMarkdown.estimateLines(blocks) > AiMarkdown.FOLD_LINE_LIMIT)
    }
}
