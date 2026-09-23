package com.quantum.app.core.common

/**
 * 消息富文本标记（脚本经 QuantumText 帮助类或手写标记嵌入消息正文）：
 * - `{{颜色|文字}}` 彩色粗体文字；`{{tag:颜色|文字}}` 胶囊标签（圆角底色+色点）；
 * - `{{link:文字|URL}}` 命名链接——气泡内只显示文字、点击跳 URL（URL 须 http(s) 开头）；
 * - 颜色仅固定枚举小写（与服务端 QuantumText 约定一致），不收任意色值。
 */
enum class RichColor { RED, GREEN, ORANGE, BLUE, PURPLE, GRAY }

/** 解析后的消息文本片段序列。 */
sealed interface RichSpan {
    /** 普通文本（渲染层可再做 URL 识别）。 */
    data class Plain(val text: String) : RichSpan

    /** 着色片段：pill=true 渲染为胶囊标签，否则为彩色粗体文字。 */
    data class Styled(val text: String, val color: RichColor, val pill: Boolean) : RichSpan

    /** 命名链接片段：只显示 [text]，点击经系统浏览器打开 [url]（新闻「详情」类场景，不占链接地址版面）。 */
    data class Link(val text: String, val url: String) : RichSpan
}

/**
 * 富文本标记解析器（纯逻辑、无 Android 依赖）：
 * - 支持 `{{颜色|文字}}` / `{{tag:颜色|文字}}` / `{{link:文字|URL}}`（命名链接，URL 非 http(s) 开头按字面）；
 * - 未知颜色、残缺未闭合、跨行、空文本一律按原文字面输出——脚本作者能在客户端直接看见笔误，
 *   旧版本 App 也只是显示原文不炸；
 * - 文本段禁花括号，天然拒绝嵌套（外层非法时内层合法标记仍会被识别，行为确定）；
 * - 单条消息标记数量上限 [MAX_MARKS]（彩色/胶囊/链接合计），超出部分按字面渲染，防超长消息渲染开销失控。
 */
object RichTextParser {

    const val MAX_MARKS = 50

    private val colorNames = RichColor.entries.associateBy { it.name.lowercase() }

    /** 文本段禁花括号与换行：拒绝嵌套与跨行；颜色段为小写字母，合法性由枚举表把关。
     *  右花括号必须转义：桌面 JVM 对未配对的 } 宽容（单测全绿），Android ICU 正则直接
     *  PatternSyntaxException——曾致会话页打开即闪退（类初始化失败）。
     *  交替第二分支为命名链接 {{link:文字|URL}}：两段均禁花括号/竖线/换行；「link」带冒号
     *  使第一分支的颜色名注定匹配失败，两分支不会互相抢占同一处命中。 */
    private val tokenRegex = Regex("""\{\{(tag:)?([a-z]+)\|([^{}\n\r]+)\}\}|\{\{link:([^{}|\n\r]+)\|([^{}|\n\r]+)\}\}""")

    /** 含合法标记的快速判定（旧消息免解析直接走纯文本渲染）。 */
    private val anyMarkRegex by lazy {
        Regex("""\{\{(tag:)?(${colorNames.keys.joinToString("|")})\|""")
    }

    fun hasMarkup(raw: String): Boolean =
        raw.contains("{{") && (anyMarkRegex.containsMatchIn(raw) || raw.contains("{{link:"))

    fun parse(raw: String): List<RichSpan> {
        if (!hasMarkup(raw)) {
            return listOf(RichSpan.Plain(raw))
        }
        val spans = mutableListOf<RichSpan>()
        var last = 0
        var marks = 0
        for (match in tokenRegex.findAll(raw)) {
            val groups = match.groupValues
            val linkText = groups[4]
            val linkUrl = groups[5]
            if (linkText.isNotEmpty() || linkUrl.isNotEmpty()) {
                // 命名链接：URL 须 http(s) 开头（拒绝 javascript:/intent: 等注入），否则按字面
                if (!linkUrl.startsWith("http", ignoreCase = true) || marks >= MAX_MARKS) {
                    continue
                }
                if (match.range.first > last) {
                    spans += RichSpan.Plain(raw.substring(last, match.range.first))
                }
                spans += RichSpan.Link(text = linkText, url = linkUrl)
                marks++
                last = match.range.last + 1
                continue
            }
            val color = colorNames[groups[2]]
            // 颜色不在枚举 / 超上限：本次命中按字面落入普通文本（不推进 last）
            if (color == null || marks >= MAX_MARKS) {
                continue
            }
            if (match.range.first > last) {
                spans += RichSpan.Plain(raw.substring(last, match.range.first))
            }
            spans += RichSpan.Styled(
                text = groups[3],
                color = color,
                pill = groups[1].isNotEmpty()
            )
            marks++
            last = match.range.last + 1
        }
        if (last < raw.length) {
            spans += RichSpan.Plain(raw.substring(last))
        }
        return spans.ifEmpty { listOf(RichSpan.Plain(raw)) }
    }
}
