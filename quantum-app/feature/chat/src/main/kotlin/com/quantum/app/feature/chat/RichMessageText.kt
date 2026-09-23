package com.quantum.app.feature.chat

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.InlineTextContent
import androidx.compose.foundation.text.appendInlineContent
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.ExperimentalTextApi
import androidx.compose.ui.text.Placeholder
import androidx.compose.ui.text.PlaceholderVerticalAlign
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import com.quantum.app.core.common.RichColor
import com.quantum.app.core.common.RichSpan
import com.quantum.app.core.common.RichTextParser
import com.quantum.app.core.common.ui.theme.QuantumPalette
import com.quantum.app.core.common.ui.theme.QuantumRadius

/** 标记色 → 调色板（固定映射，不收任意色值；Amber 为警示/进行中）。选项块色名复用同一映射。 */
internal fun RichColor.palette(): Color = when (this) {
    RichColor.RED -> QuantumPalette.Rose
    RichColor.GREEN -> QuantumPalette.Emerald
    RichColor.ORANGE -> QuantumPalette.Amber
    RichColor.BLUE -> QuantumPalette.Cyan
    RichColor.PURPLE -> QuantumPalette.Indigo
    RichColor.GRAY -> QuantumPalette.TextMuted
}

/** 消息正文中的 URL 识别（ASCII URL 字符集，天然不含中文与中文标点）。 */
private val URL_REGEX = Regex("""https?://[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+""")

/** 超过该行数的消息折叠为「展开全文」（晨报/长日志防刷屏）。 */
private const val FOLD_LINES = 10

/**
 * 富文本消息正文：解析 {{颜色|文字}} / {{tag:颜色|文字}} 标记渲染彩色文字与胶囊标签，
 * {{link:文字|URL}} 命名链接只显示文字、点击跳转；普通文本段自动识别 URL 为可点链接
 * （系统浏览器打开）；未知/残缺标记按原文字面显示。
 * 超长文本（> [FOLD_LINES] 行）折叠，点「展开全文」切换。
 */
@OptIn(ExperimentalTextApi::class)
@Composable
internal fun RichMessageText(
    text: String,
    style: TextStyle,
    modifier: Modifier = Modifier,
    color: Color = Color.Unspecified
) {
    val spans = remember(text) { RichTextParser.parse(text) }
    var expanded by rememberSaveable(text) { mutableStateOf(false) }
    val needsFold = remember(spans) { spanLineCount(spans) > FOLD_LINES }
    val density = LocalDensity.current
    val textMeasurer = rememberTextMeasurer()
    val pillStyle = MaterialTheme.typography.labelSmall
    val uriHandler = LocalUriHandler.current
    val linkColor = MaterialTheme.colorScheme.primary

    // §4-7 整块记忆化：胶囊尺寸实测 + buildAnnotatedString + URL 正则扫描，
    // 只在内容（spans）/折叠态（expanded）/样式或 density 变化时重算，
    // 避免每次重组（含父级滚动/其他消息落库引发的连锁重组）重复跑 measure 与正则。
    val (annotated, inlineContents) = remember(
        spans, expanded, needsFold, pillStyle, linkColor, density, uriHandler
    ) {
        val shown = if (needsFold && !expanded) truncateSpans(spans, FOLD_LINES) else spans
        // 胶囊标签尺寸按文字实测宽度精确占位（Placeholder 宽高为 sp，实测 px 经 density 换算）
        val contents = shown.mapIndexedNotNull { index, span ->
            val styled = span as? RichSpan.Styled ?: return@mapIndexedNotNull null
            if (!styled.pill) return@mapIndexedNotNull null
            val id = "rich_pill_$index"
            val layout = textMeasurer.measure(styled.text, pillStyle)
            with(density) {
                // 6*2 横内边距 + 5dp 色点 + 4dp 间距 + 2dp 边框；2*2 竖内边距 + 2dp 边框
                val width = (layout.size.width.toDp() + 23.dp).coerceAtLeast(28.dp).toSp()
                val height = (layout.size.height.toDp() + 6.dp).coerceAtLeast(20.dp).toSp()
                id to InlineTextContent(
                    Placeholder(width = width, height = height, placeholderVerticalAlign = PlaceholderVerticalAlign.TextCenter)
                ) {
                    PillTag(text = styled.text, color = styled.color.palette())
                }
            }
        }.toMap()
        val build = buildAnnotatedString {
            shown.forEachIndexed { index, span ->
                when (span) {
                    is RichSpan.Plain -> appendWithUrls(span.text, linkColor) { uriHandler.openUri(it) }
                    is RichSpan.Link -> withLink(
                        androidx.compose.ui.text.LinkAnnotation.Url(
                            span.url,
                            androidx.compose.ui.text.TextLinkStyles(
                                SpanStyle(color = linkColor, textDecoration = TextDecoration.Underline)
                            )
                        ) { uriHandler.openUri(span.url) }
                    ) {
                        // 只显示命名文字（如「详情」），链接地址不占版面
                        append(span.text)
                    }
                    is RichSpan.Styled -> {
                        if (span.pill) {
                            appendInlineContent("rich_pill_$index", "[${span.text}]")
                        } else {
                            withStyle(SpanStyle(color = span.color.palette(), fontWeight = FontWeight.SemiBold)) {
                                appendWithUrls(span.text, linkColor) { uriHandler.openUri(it) }
                            }
                        }
                    }
                }
            }
        }
        build to contents
    }

    Column(modifier = modifier) {
        SelectionContainer {
            Text(annotated, style = style, color = color, inlineContent = inlineContents)
        }
        if (needsFold) {
            TextButton(
                onClick = { expanded = !expanded },
                contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 0.dp),
                modifier = Modifier.height(30.dp)
            ) {
                Text(if (expanded) "收起" else "展开全文")
            }
        }
    }
}

/** 追加文本并识别其中的 URL 为可点链接（链接外的文字原样）。 */
private fun androidx.compose.ui.text.AnnotatedString.Builder.appendWithUrls(
    text: String,
    linkColor: Color,
    openUrl: (String) -> Unit
) {
    var last = 0
    for (match in URL_REGEX.findAll(text)) {
        if (match.range.first > last) {
            append(text.substring(last, match.range.first))
        }
        val url = match.value
        withLink(
            androidx.compose.ui.text.LinkAnnotation.Url(
                url,
                androidx.compose.ui.text.TextLinkStyles(
                    SpanStyle(color = linkColor, textDecoration = TextDecoration.Underline)
                )
            ) { openUrl(url) }
        ) {
            append(url)
        }
        last = match.range.last + 1
    }
    if (last < text.length) {
        append(text.substring(last))
    }
}

/** 胶囊标签：圆角底色 + 色点 + 着色文字（视觉对齐 QuantumStatusBadge）。 */
@Composable
private fun PillTag(text: String, color: Color) {
    val shape = RoundedCornerShape(QuantumRadius.Pill)
    Row(
        modifier = Modifier
            .clip(shape)
            .background(color.copy(alpha = 0.12f))
            .border(1.dp, color.copy(alpha = 0.35f), shape)
            .padding(horizontal = 6.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .size(5.dp)
                .clip(CircleShape)
                .background(color)
        )
        Spacer(modifier = Modifier.width(4.dp))
        Text(
            text,
            style = MaterialTheme.typography.labelSmall,
            color = color,
            fontWeight = FontWeight.Medium,
            maxLines = 1
        )
    }
}

/** 统计片段序列的总行数（按换行符计数）。 */
private fun spanLineCount(spans: List<RichSpan>): Int =
    spans.sumOf { it.text().count { c -> c == '\n' } } + 1

private fun RichSpan.text(): String = when (this) {
    is RichSpan.Plain -> text
    is RichSpan.Styled -> text
    is RichSpan.Link -> text
}

/**
 * 按片段截断到 maxLines 行（在片段边界内截，绝不切开标记语法），并以省略号收尾。
 */
private fun truncateSpans(spans: List<RichSpan>, maxLines: Int): List<RichSpan> {
    val out = mutableListOf<RichSpan>()
    var lines = 1
    for (span in spans) {
        val text = span.text()
        val breaks = text.count { it == '\n' }
        if (lines + breaks <= maxLines) {
            out += span
            lines += breaks
            continue
        }
        // 本片段内截断：保留前 keep 行整行，其后丢弃
        val keep = maxLines - lines + 1
        var pos = 0
        repeat(keep - 1) {
            val nl = text.indexOf('\n', pos)
            if (nl >= 0) {
                pos = nl + 1
            }
        }
        val cut = text.indexOf('\n', pos).let { if (it < 0) text.length else it }
        val kept = text.substring(0, cut)
        if (kept.isNotEmpty()) {
            out += when (span) {
                is RichSpan.Plain -> RichSpan.Plain(kept)
                is RichSpan.Styled -> span.copy(text = kept)
                is RichSpan.Link -> span.copy(text = kept)
            }
        }
        out += RichSpan.Plain("…")
        return out
    }
    return out
}
