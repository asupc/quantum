package com.quantum.app.core.common.ui.components

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.Checkbox
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSize
import com.quantum.app.core.common.ui.theme.QuantumSpacing

/** 分组标题：左侧主色竖条 + 灰色小标题 + 可选右侧动作。 */
@Composable
fun QuantumSectionHeader(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    action: (@Composable () -> Unit)? = null
) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .size(width = 3.dp, height = 14.dp)
                .clip(RoundedCornerShape(QuantumRadius.Pill))
                .background(MaterialTheme.colorScheme.primary)
        )
        Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            if (subtitle != null) {
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.8f)
                )
            }
        }
        action?.invoke()
    }
}

/**
 * 列表卡片容器：统一的卡片底/圆角/描边，行之间用 [QuantumRowDivider] 分隔。
 */
@Composable
fun QuantumListCard(
    modifier: Modifier = Modifier,
    border: BorderStroke? = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
    content: @Composable ColumnScope.() -> Unit
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.surfaceContainer,
        shape = RoundedCornerShape(QuantumRadius.Md),
        border = border
    ) {
        Column(content = content)
    }
}

/** 行分隔线：左侧与行内缩进对齐。 */
@Composable
fun QuantumRowDivider(modifier: Modifier = Modifier) {
    HorizontalDivider(
        modifier = modifier.padding(start = QuantumSpacing.Md + 2.dp),
        thickness = 1.dp,
        color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.6f)
    )
}

/**
 * 统一列表行：leading 图标（12% 同色底方块）+ 标题 + 副标题 + 右侧文本/自定义尾随 + chevron。
 * `danger = true` 时标题与图标走 error（用于注销设备、删除这类操作）。
 */
@Composable
fun QuantumListRow(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    leading: (@Composable () -> Unit)? = null,
    leadingIcon: ImageVector? = null,
    leadingTint: Color = MaterialTheme.colorScheme.primary,
    trailingText: String? = null,
    trailing: (@Composable () -> Unit)? = null,
    showChevron: Boolean = false,
    danger: Boolean = false,
    onClick: (() -> Unit)? = null
) {
    val scheme = MaterialTheme.colorScheme
    val accent = if (danger) scheme.error else leadingTint
    Row(
        modifier = modifier
            .fillMaxWidth()
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .heightIn(min = 56.dp)
            .padding(horizontal = QuantumSpacing.Md + 2.dp, vertical = QuantumSpacing.Md),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (leading != null) {
            // 自定义前置槽（复选框等）：由调用方控制尺寸与触达
            leading()
            Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
        } else if (leadingIcon != null) {
            Box(
                modifier = Modifier
                    .size(QuantumSize.LeadingBox)
                    .clip(RoundedCornerShape(QuantumRadius.Sm))
                    .background(accent.copy(alpha = 0.12f)),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = leadingIcon,
                    contentDescription = null,
                    tint = accent,
                    modifier = Modifier.size(QuantumSize.IconSm)
                )
            }
            Spacer(modifier = Modifier.width(QuantumSpacing.Md))
        }
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                style = MaterialTheme.typography.bodyLarge,
                color = if (danger) scheme.error else scheme.onSurface,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            if (subtitle != null) {
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = scheme.onSurfaceVariant,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 2.dp)
                )
            }
        }
        if (trailingText != null) {
            Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
            Text(
                text = trailingText,
                style = MaterialTheme.typography.labelSmall,
                color = scheme.onSurfaceVariant
            )
        }
        trailing?.invoke()
        if (showChevron) {
            Spacer(modifier = Modifier.width(QuantumSpacing.Xs))
            Icon(
                imageVector = Icons.AutoMirrored.Filled.KeyboardArrowRight,
                contentDescription = null,
                tint = scheme.onSurfaceVariant,
                modifier = Modifier.size(QuantumSize.IconMd)
            )
        }
    }
}

/** 设置项：整行可点切换的开关行。 */
@Composable
fun QuantumSwitchRow(
    title: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    leadingIcon: ImageVector? = null,
    enabled: Boolean = true
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clickable(enabled = enabled) { onCheckedChange(!checked) }
            .heightIn(min = 56.dp)
            .padding(horizontal = QuantumSpacing.Md + 2.dp, vertical = QuantumSpacing.Md),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (leadingIcon != null) {
            Box(
                modifier = Modifier
                    .size(QuantumSize.LeadingBox)
                    .clip(RoundedCornerShape(QuantumRadius.Sm))
                    .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.12f)),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = leadingIcon,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(QuantumSize.IconSm)
                )
            }
            Spacer(modifier = Modifier.width(QuantumSpacing.Md))
        }
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                style = MaterialTheme.typography.bodyLarge,
                color = if (enabled) {
                    MaterialTheme.colorScheme.onSurface
                } else {
                    MaterialTheme.colorScheme.onSurfaceVariant
                }
            )
            if (subtitle != null) {
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 2.dp)
                )
            }
        }
        Switch(checked = checked, onCheckedChange = onCheckedChange, enabled = enabled)
    }
}

/** 设置项：整行可点切换的勾选行（多选/强更这类）。 */
@Composable
fun QuantumCheckRow(
    title: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    enabled: Boolean = true
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clickable(enabled = enabled) { onCheckedChange(!checked) }
            .heightIn(min = 48.dp)
            .padding(horizontal = QuantumSpacing.Md + 2.dp, vertical = QuantumSpacing.Sm),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                style = MaterialTheme.typography.bodyLarge,
                color = if (enabled) {
                    MaterialTheme.colorScheme.onSurface
                } else {
                    MaterialTheme.colorScheme.onSurfaceVariant
                }
            )
            if (subtitle != null) {
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
        Checkbox(checked = checked, onCheckedChange = onCheckedChange, enabled = enabled)
    }
}

/** 空态：列表无数据时的统一引导（图标 + 标题 + 说明 + 可选动作）。 */
@Composable
fun QuantumEmptyState(
    icon: ImageVector,
    title: String,
    modifier: Modifier = Modifier,
    description: String? = null,
    actionText: String? = null,
    onAction: (() -> Unit)? = null
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = QuantumPage.Padding, vertical = 48.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Box(
            modifier = Modifier
                .size(56.dp)
                .clip(CircleShape)
                .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.08f)),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary.copy(alpha = 0.85f),
                modifier = Modifier.size(QuantumSize.IconLg)
            )
        }
        Spacer(modifier = Modifier.height(QuantumPage.CardGap))
        Text(
            text = title,
            style = MaterialTheme.typography.titleSmall,
            color = MaterialTheme.colorScheme.onSurface
        )
        if (description != null) {
            Spacer(modifier = Modifier.height(QuantumSpacing.Xs))
            Text(
                text = description,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center
            )
        }
        if (actionText != null && onAction != null) {
            Spacer(modifier = Modifier.height(QuantumSpacing.Lg))
            QuantumButton(
                text = actionText,
                onClick = onAction,
                variant = QuantumButtonVariant.Tonal,
                size = QuantumButtonSize.Sm
            )
        }
    }
}

/** 骨架屏列表：首次加载时的占位（避免"空白屏"），shimmer 由无限动画驱动。 */
@Composable
fun QuantumLoadingList(
    modifier: Modifier = Modifier,
    rows: Int = 4
) {
    val transition = rememberInfiniteTransition(label = "skeleton")
    val shift by transition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 1200, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "skeletonShift"
    )
    val base = MaterialTheme.colorScheme.surfaceContainerHigh
    val highlight = MaterialTheme.colorScheme.surfaceContainerHighest
    val brush = Brush.linearGradient(
        colors = listOf(base, highlight, base),
        start = Offset(shift * 900f - 300f, 0f),
        end = Offset(shift * 900f, 300f)
    )
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = QuantumPage.Padding, vertical = QuantumPage.CardGap),
        verticalArrangement = Arrangement.spacedBy(QuantumPage.CardGap)
    ) {
        repeat(rows) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(QuantumRadius.Md))
                    .background(MaterialTheme.colorScheme.surfaceContainer)
                    .padding(QuantumSpacing.Md),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(QuantumSize.LeadingBox)
                        .clip(RoundedCornerShape(QuantumRadius.Sm))
                        .background(brush)
                )
                Spacer(modifier = Modifier.width(QuantumSpacing.Md))
                Column(verticalArrangement = Arrangement.spacedBy(QuantumSpacing.Sm)) {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth(0.55f)
                            .height(12.dp)
                            .clip(RoundedCornerShape(QuantumRadius.Pill))
                            .background(brush)
                    )
                    Box(
                        modifier = Modifier
                            .fillMaxWidth(0.32f)
                            .height(10.dp)
                            .clip(RoundedCornerShape(QuantumRadius.Pill))
                            .background(brush)
                    )
                }
            }
        }
    }
}
