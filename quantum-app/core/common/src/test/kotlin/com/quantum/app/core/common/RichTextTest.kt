package com.quantum.app.core.common

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class RichTextTest {

    @Test
    fun `纯文本无标记原样返回`() {
        assertEquals(listOf(RichSpan.Plain("hello 量子")), RichTextParser.parse("hello 量子"))
    }

    @Test
    fun `彩色文字标记`() {
        assertEquals(
            listOf(RichSpan.Styled("警告", RichColor.RED, pill = false)),
            RichTextParser.parse("{{red|警告}}")
        )
    }

    @Test
    fun `胶囊标签标记`() {
        assertEquals(
            listOf(RichSpan.Styled("已监听", RichColor.GREEN, pill = true)),
            RichTextParser.parse("{{tag:green|已监听}}")
        )
    }

    @Test
    fun `六色全部可识别`() {
        val names = mapOf(
            "red" to RichColor.RED, "green" to RichColor.GREEN, "orange" to RichColor.ORANGE,
            "blue" to RichColor.BLUE, "purple" to RichColor.PURPLE, "gray" to RichColor.GRAY
        )
        names.forEach { (name, color) ->
            assertEquals(
                listOf(RichSpan.Styled("x", color, pill = false)),
                RichTextParser.parse("{{$name|x}}")
            )
        }
    }

    @Test
    fun `混合文本切分正确`() {
        assertEquals(
            listOf(
                RichSpan.Plain("结果 "),
                RichSpan.Styled("失败", RichColor.RED, pill = false),
                RichSpan.Plain("，详见日志")
            ),
            RichTextParser.parse("结果 {{red|失败}}，详见日志")
        )
    }

    @Test
    fun `未知颜色按字面显示`() {
        val raw = "{{pink|文字}}"
        assertEquals(listOf(RichSpan.Plain(raw)), RichTextParser.parse(raw))
    }

    @Test
    fun `颜色名大小写敏感`() {
        val raw = "{{RED|x}}"
        assertEquals(listOf(RichSpan.Plain(raw)), RichTextParser.parse(raw))
    }

    @Test
    fun `未闭合按字面显示`() {
        val raw = "{{red|文字"
        assertEquals(listOf(RichSpan.Plain(raw)), RichTextParser.parse(raw))
    }

    @Test
    fun `空文本按字面显示`() {
        val raw = "{{red|}}"
        assertEquals(listOf(RichSpan.Plain(raw)), RichTextParser.parse(raw))
    }

    @Test
    fun `跨行标记不匹配按字面显示`() {
        val raw = "{{red|多行\n文字}}"
        assertEquals(listOf(RichSpan.Plain(raw)), RichTextParser.parse(raw))
    }

    @Test
    fun `嵌套花括号外层不匹配内层合法标记仍生效`() {
        // 外层 {{red| 后紧跟 { 被文本段规则拒绝；内层 {{blue|x}} 是合法标记
        assertEquals(
            listOf(
                RichSpan.Plain("{{red|"),
                RichSpan.Styled("x", RichColor.BLUE, pill = false),
                RichSpan.Plain("}}")
            ),
            RichTextParser.parse("{{red|{{blue|x}}}}")
        )
    }

    @Test
    fun `超过上限的标记按字面显示`() {
        val raw = (1..51).joinToString("") { "{{red|t}}" }
        val spans = RichTextParser.parse(raw)
        val styled = spans.count { it is RichSpan.Styled }
        assertEquals(RichTextParser.MAX_MARKS, styled)
        // 第 51 个标记落回普通文本
        assertTrue(spans.last() is RichSpan.Plain)
    }

    @Test
    fun `命名链接标记`() {
        assertEquals(
            listOf(RichSpan.Link("详情", "https://www.zhihu.com/question/123")),
            RichTextParser.parse("{{link:详情|https://www.zhihu.com/question/123}}")
        )
    }

    @Test
    fun `命名链接与彩色文字混排`() {
        assertEquals(
            listOf(
                RichSpan.Plain("1. 标题 "),
                RichSpan.Link("详情", "https://a.b/c"),
                RichSpan.Plain(" "),
                RichSpan.Styled("新", RichColor.ORANGE, pill = false)
            ),
            RichTextParser.parse("1. 标题 {{link:详情|https://a.b/c}} {{orange|新}}")
        )
    }

    @Test
    fun `命名链接 http 大小写不敏感`() {
        assertEquals(
            listOf(RichSpan.Link("x", "HTTP://a.b")),
            RichTextParser.parse("{{link:x|HTTP://a.b}}")
        )
    }

    @Test
    fun `命名链接非 http 协议按字面显示`() {
        val raw = "{{link:点我|javascript:alert(1)}}"
        assertEquals(listOf(RichSpan.Plain(raw)), RichTextParser.parse(raw))
    }

    @Test
    fun `命名链接未闭合按字面显示`() {
        val raw = "{{link:详情|https://a.b"
        assertEquals(listOf(RichSpan.Plain(raw)), RichTextParser.parse(raw))
    }

    @Test
    fun `link 不带冒号按颜色名解析为未知按字面显示`() {
        val raw = "{{link|详情}}"
        assertEquals(listOf(RichSpan.Plain(raw)), RichTextParser.parse(raw))
    }

    @Test
    fun `hasMarkup 快速判定`() {
        assertTrue(RichTextParser.hasMarkup("a {{red|b}} c"))
        assertTrue(RichTextParser.hasMarkup("{{tag:orange|进行中}}"))
        assertTrue(RichTextParser.hasMarkup("{{link:详情|https://a.b}}"))
        assertFalse(RichTextParser.hasMarkup("纯文本"))
        assertFalse(RichTextParser.hasMarkup("{{unknown|x}}"))
        assertFalse(RichTextParser.hasMarkup("普通 { 花括号"))
    }
}
