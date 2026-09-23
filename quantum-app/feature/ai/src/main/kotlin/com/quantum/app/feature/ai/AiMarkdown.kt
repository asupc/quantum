package com.quantum.app.feature.ai

/**
 * Markdown-lite 解析（纯 Kotlin 可测）。对齐 Web renderMarkdownLite 的 ``` 围栏 + 行内 `code` 语义；
 * URL 链接与长文折叠是 App 端超出 Web 的新增（Web 版两者皆无）。
 */
object AiMarkdown {

    /** 折叠阈值（App 新增；Web 无折叠机制）。 */
    const val FOLD_LINE_LIMIT = 40

    sealed interface Block {
        /** 围栏代码块（language = 首行语言标记，已剥）。 */
        data class Code(val language: String, val text: String) : Block

        /** 段落（行内已切 span；段落内单换行保留为换行 span）。 */
        data class Paragraph(val spans: List<Span>) : Block
    }

    sealed interface Span {
        data class Plain(val text: String) : Span
        data class InlineCode(val text: String) : Span
        data class Url(val url: String, val text: String) : Span
        object NewLine : Span
    }

    private val URL_REGEX = Regex("""https?://[^\s`<>()（）【】\[\]{}'"，。；、]+""")

    fun parse(text: String): List<Block> {
        if (text.isBlank()) return emptyList()
        val blocks = ArrayList<Block>()
        // 按 ``` 拆块：偶数段为正文、奇数段为代码（未闭合围栏时最后的奇数段仍按代码处理）
        val parts = text.split("```")
        for ((index, part) in parts.withIndex()) {
            if (index % 2 == 1) {
                val stripped = part.removePrefix("\n")
                val firstLineEnd = stripped.indexOf('\n')
                val language = if (firstLineEnd > 0) stripped.substring(0, firstLineEnd).trim() else ""
                val body = if (firstLineEnd > 0) stripped.substring(firstLineEnd + 1) else stripped
                blocks += Block.Code(language, body.trimEnd('\n'))
            } else if (part.isNotBlank()) {
                // 段落按空行（\n{2,}）切
                part.split(Regex("\n{2,}")).forEach { paragraph ->
                    val trimmed = paragraph.trim('\n')
                    if (trimmed.isNotBlank()) {
                        blocks += Block.Paragraph(parseInline(trimmed))
                    }
                }
            }
        }
        return blocks
    }

    /** 行内解析：`code` 切 Monospace、URL 可点；段落内单换行保留（NewLine span）。 */
    internal fun parseInline(text: String): List<Span> {
        val spans = ArrayList<Span>()
        for ((lineIndex, line) in text.split('\n').withIndex()) {
            if (lineIndex > 0) spans += Span.NewLine
            var cursor = 0
            while (cursor < line.length) {
                val codeStart = line.indexOf('`', cursor)
                val urlMatch = URL_REGEX.find(line, cursor)
                // 取 code 反引号与 URL 中先出现者（groups[0].range 是 MatchGroup 成员，跨版本稳定）
                val nextCode = if (codeStart >= 0) codeStart else Int.MAX_VALUE
                val urlRange = urlMatch?.groups?.get(0)?.range
                val nextUrl = urlRange?.first ?: Int.MAX_VALUE
                when {
                    nextCode == Int.MAX_VALUE && nextUrl == Int.MAX_VALUE -> {
                        spans += Span.Plain(line.substring(cursor))
                        cursor = line.length
                    }
                    nextCode < nextUrl -> {
                        val codeEnd = line.indexOf('`', codeStart + 1)
                        if (codeEnd > codeStart) {
                            if (codeStart > cursor) spans += Span.Plain(line.substring(cursor, codeStart))
                            spans += Span.InlineCode(line.substring(codeStart + 1, codeEnd))
                            cursor = codeEnd + 1
                        } else {
                            // 未闭合反引号：当普通文本
                            spans += Span.Plain(line.substring(cursor))
                            cursor = line.length
                        }
                    }
                    else -> {
                        val url = urlMatch!!.value
                        if (urlRange!!.first > cursor) spans += Span.Plain(line.substring(cursor, urlRange.first))
                        spans += Span.Url(url, url)
                        cursor = urlRange.last + 1
                    }
                }
            }
        }
        return spans
    }

    /** 估算渲染行数（折叠判定用）：代码块按其行数，段落按视觉行近似（每段 ≥1 行）。 */
    fun estimateLines(blocks: List<Block>): Int = blocks.sumOf { block ->
        when (block) {
            is Block.Code -> block.text.split('\n').count { it.isNotBlank() }.coerceAtLeast(1) + 1
            is Block.Paragraph -> 1
        }
    }
}
