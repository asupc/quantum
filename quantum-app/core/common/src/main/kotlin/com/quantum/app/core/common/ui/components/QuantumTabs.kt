package com.quantum.app.core.common.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSize
import com.quantum.app.core.common.ui.theme.QuantumSpacing

/** 选项卡项：label 必填，count 用于带数量的筛选（如「任务 12」）。 */
data class QuantumTab(val label: String, val count: Int? = null)

/**
 * 统一选项卡（替代各页手拼的 FilterChip）：
 * - `scrollable = false`（默认，2~4 项）：等宽分段控件，选中滑块用**弹簧过渡**滑动；
 * - `scrollable = true`（项多、需横滑，如日志类型 8 项）：每项自带选中底色胶囊。
 */
@Composable
fun QuantumSegmentedTabs(
    tabs: List<QuantumTab>,
    selectedIndex: Int,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier,
    scrollable: Boolean = false
) {
    if (tabs.isEmpty()) {
        return
    }
    if (scrollable) {
        Row(
            modifier = modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = QuantumPage.Padding),
            horizontalArrangement = Arrangement.spacedBy(QuantumSpacing.Sm)
        ) {
            tabs.forEachIndexed { index, tab ->
                val selected = index == selectedIndex
                val background by animateColorAsState(
                    targetValue = if (selected) {
                        MaterialTheme.colorScheme.primary.copy(alpha = 0.16f)
                    } else {
                        MaterialTheme.colorScheme.surfaceContainerHigh
                    },
                    animationSpec = tween(200),
                    label = "tabBackground"
                )
                val foreground by animateColorAsState(
                    targetValue = if (selected) {
                        MaterialTheme.colorScheme.primary
                    } else {
                        MaterialTheme.colorScheme.onSurfaceVariant
                    },
                    animationSpec = tween(200),
                    label = "tabForeground"
                )
                val shape = RoundedCornerShape(QuantumRadius.Pill)
                Row(
                    modifier = Modifier
                        .height(QuantumSize.ControlHeightSm)
                        .clip(shape)
                        .background(background)
                        .then(
                            if (selected) {
                                Modifier.border(
                                    1.dp,
                                    MaterialTheme.colorScheme.primary.copy(alpha = 0.35f),
                                    shape
                                )
                            } else {
                                Modifier
                            }
                        )
                        .clickable { onSelect(index) }
                        .padding(horizontal = QuantumSpacing.Md),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(tab.label, color = foreground, style = MaterialTheme.typography.labelLarge)
                    if (tab.count != null) {
                        Text(
                            text = " ${tab.count}",
                            color = foreground.copy(alpha = 0.7f),
                            style = MaterialTheme.typography.labelSmall
                        )
                    }
                }
            }
        }
        return
    }

    val progress by animateFloatAsState(
        targetValue = selectedIndex.coerceIn(0, tabs.lastIndex).toFloat(),
        animationSpec = spring(
            dampingRatio = 0.75f,
            stiffness = Spring.StiffnessMediumLow
        ),
        label = "tabIndicator"
    )
    BoxWithConstraints(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = QuantumPage.Padding)
    ) {
        // 轨道内缩 Xs，滑块宽度按可用宽度等分（否则滑块会随项数偏右）
        val indicatorWidth = (maxWidth - QuantumSpacing.Xs * 2) / tabs.size
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(QuantumSize.ControlHeightSm + QuantumSpacing.Sm)
                .clip(RoundedCornerShape(QuantumRadius.Pill))
                .background(MaterialTheme.colorScheme.surfaceContainerHigh)
                .padding(QuantumSpacing.Xs)
        ) {
            Box(
                modifier = Modifier
                    .offset(x = indicatorWidth * progress)
                    .width(indicatorWidth)
                    .fillMaxHeight()
                    .clip(RoundedCornerShape(QuantumRadius.Pill))
                    .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.18f))
            )
            Row(modifier = Modifier.fillMaxSize()) {
                tabs.forEachIndexed { index, tab ->
                    val foreground by animateColorAsState(
                        targetValue = if (index == selectedIndex) {
                            MaterialTheme.colorScheme.primary
                        } else {
                            MaterialTheme.colorScheme.onSurfaceVariant
                        },
                        animationSpec = tween(200),
                        label = "tabForeground"
                    )
                    Box(
                        modifier = Modifier
                            .weight(1f)
                            .fillMaxHeight()
                            .clip(RoundedCornerShape(QuantumRadius.Pill))
                            .clickable { onSelect(index) },
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            text = tab.label,
                            color = foreground,
                            style = MaterialTheme.typography.labelLarge,
                            maxLines = 1
                        )
                    }
                }
            }
        }
    }
}
