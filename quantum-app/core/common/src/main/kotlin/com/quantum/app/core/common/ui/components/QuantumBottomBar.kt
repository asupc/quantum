package com.quantum.app.core.common.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.windowInsetsBottomHeight
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.quantum.app.core.common.ui.theme.QuantumPalette

/** 底栏的一项（描边图标 + 文案）。showBadge=true 时在图标右上角画 QQ 式红点角标。 */
data class BottomBarItem(val label: String, val icon: ImageVector, val showBadge: Boolean = false)

/**
 * QQ 风格平铺底栏（2026-09-18 替代原 smartbook 浮动胶囊样式）：
 * - **无容器**：不要卡片/描边/投影/选中药丸，图标文字直接铺在页面底色上，通栏贴底；
 * - **图标在上、文字在下**：描边（Outlined）图标 24dp、间距 2dp、文字 11sp，
 *   选中态只变颜色不加粗（与 QQ 一致）；
 * - **配色**：选中 = 主色（图标+文字同步过渡 150ms），未选中 = 近白（QQ 暗色即白字，
 *   不是常规底栏的灰）；
 * - **红点角标**：showBadge 时图标右上角 9dp 圆点（QQ 未读样式，是否点亮由调用方传入）；
 * - 手势导航区保持透明，与页面底色连成一体。
 */
@Composable
fun QuantumBottomBar(
    items: List<BottomBarItem>,
    selectedIndex: Int,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier
) {
    if (items.isEmpty()) {
        return
    }
    Column(modifier = modifier.fillMaxWidth()) {
        Row(modifier = Modifier.fillMaxWidth().height(BAR_HEIGHT)) {
            items.forEachIndexed { index, item ->
                val selected = index == selectedIndex
                val tint by animateColorAsState(
                    targetValue = if (selected) MaterialTheme.colorScheme.primary else QuantumPalette.TextPrimary,
                    animationSpec = tween(durationMillis = 150),
                    label = "barTint"
                )
                Column(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxHeight()
                        .clickable(
                            interactionSource = remember { MutableInteractionSource() },
                            indication = null
                        ) { onSelect(index) },
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center
                ) {
                    Box {
                        Icon(
                            imageVector = item.icon,
                            contentDescription = item.label,
                            tint = tint,
                            modifier = Modifier.size(24.dp)
                        )
                        if (item.showBadge) {
                            Box(
                                modifier = Modifier
                                    .align(Alignment.TopEnd)
                                    .offset(x = 2.dp, y = (-1).dp)
                                    .size(9.dp)
                                    .background(QuantumPalette.Rose, CircleShape)
                            )
                        }
                    }
                    Spacer(modifier = Modifier.height(2.dp))
                    Text(
                        text = item.label,
                        color = tint,
                        fontSize = 11.sp,
                        maxLines = 1
                    )
                }
            }
        }
        // 手势导航区：保持透明，底栏本体与页面底色连成一片（QQ 无浮层感的关键）
        Box(modifier = Modifier.fillMaxWidth().windowInsetsBottomHeight(WindowInsets.navigationBars))
    }
}

/** 底栏内容区高度（不含手势导航 inset）。 */
private val BAR_HEIGHT = 56.dp
