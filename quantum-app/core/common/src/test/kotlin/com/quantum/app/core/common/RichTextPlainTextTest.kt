package com.quantum.app.core.common

import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * 标记剥离（G-Push 口径）：系统通知栏、OS 摘要、会话列表预览、搜索与日志这些没有富文本渲染器的位置
 * 必须拿到纯文本——既不能把 `{{red|x}}` 语法泄露给用户，也不能把富文本当成可执行内容。
 */
class RichTextPlainTextTest {

    @Test
    fun `色标与胶囊只留文字`() {
        assertEquals("紧急 温度过高", RichTextParser.plainText("{{red|紧急}} 温度过高"))
        assertEquals("已恢复 全部正常", RichTextParser.plainText("{{tag:orange|已恢复}} 全部正常"))
    }

    @Test
    fun `命名链接不保留URL`() {
        assertEquals("查看详情", RichTextParser.plainText("{{link:查看详情|https://example.com/status}}"))
    }

    @Test
    fun `残缺与未知标记按字面保留 与解析器口径一致`() {
        // 解析器对非法标记的处理是「按原文字面输出」，剥离同样不得吞字
        assertEquals("{{red|未闭合", RichTextParser.plainText("{{red|未闭合"))
        assertEquals("{{rainbow|彩}}", RichTextParser.plainText("{{rainbow|彩}}"))
    }

    @Test
    fun `普通文本原样返回`() {
        assertEquals("纯文本", RichTextParser.plainText("纯文本"))
        assertEquals("", RichTextParser.plainText(""))
        assertEquals("a\nb", RichTextParser.plainText("a\nb"))
    }

    @Test
    fun `剥离结果不再含花括号标记`() {
        val stripped = RichTextParser.plainText("{{red|a}}{{tag:blue|b}}{{link:c|https://e.com}}")
        assertEquals("abc", stripped)
        assertEquals(false, stripped.contains("{{"))
    }
}
