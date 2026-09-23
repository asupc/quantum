package com.quantum.app.core.common.ui.components

import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSize
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filter

/**
 * 大标题收缩进度（0f=完全展开，1f=收缩到紧凑高度）：
 * 由 LazyColumn 的首项滚动偏移驱动，用于 iOS 式"标题随滚动收缩"。
 */
@Composable
fun rememberCollapseFraction(state: LazyListState, range: Int = 64): Float {
    val fraction by remember(state) {
        derivedStateOf {
            if (state.firstVisibleItemIndex > 0) {
                1f
            } else {
                (state.firstVisibleItemScrollOffset / range.toFloat()).coerceIn(0f, 1f)
            }
        }
    }
    return fraction
}

/**
 * 可收缩大标题（放在 LazyColumn 的首项）：22sp → 16sp，副标题随收缩淡出。
 * 常驻的右侧动作按钮不受收缩影响，保证任何滚动位置都能点。
 */
@Composable
fun QuantumCollapsibleTitle(
    title: String,
    fraction: Float,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    actions: (@Composable RowScope.() -> Unit)? = null
) {
    val size = 22f - 6f * fraction
    val subtitleAlpha = (1f - fraction * 1.6f).coerceIn(0f, 1f)
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(
                start = QuantumPage.Padding,
                end = QuantumPage.Padding,
                top = QuantumPage.TopPadding - (QuantumPage.TopPadding - 4.dp) * fraction,
                bottom = QuantumSpacing.Sm - (QuantumSpacing.Sm - 2.dp) * fraction
            ),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                fontSize = size.sp,
                lineHeight = (size + 6f).sp,
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface
            )
            if (subtitle != null && subtitleAlpha > 0.02f) {
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = subtitleAlpha),
                    modifier = Modifier.padding(top = 2.dp)
                )
            }
        }
        actions?.let {
            Row(verticalAlignment = Alignment.CenterVertically) { it() }
        }
    }
}

/** 粘性分组标题：放进 LazyColumn 的 stickyHeader。 */
@Composable
fun QuantumStickyGroupHeader(
    title: String,
    modifier: Modifier = Modifier
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.background)
            .padding(
                start = QuantumPage.Padding,
                end = QuantumPage.Padding,
                top = QuantumSpacing.Sm,
                bottom = QuantumSpacing.Xs + 2.dp
            ),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .size(width = 3.dp, height = 14.dp)
                .clip(RoundedCornerShape(QuantumRadius.Pill))
                .background(MaterialTheme.colorScheme.primary)
        )
        Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
        Text(
            text = title,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/**
 * 下拉刷新容器：Material3 `PullToRefreshBox` + 主题色指示器（主色=量子青，天然品牌色）。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun QuantumPullRefresh(
    refreshing: Boolean,
    onRefresh: () -> Unit,
    modifier: Modifier = Modifier,
    content: @Composable BoxScope.() -> Unit
) {
    PullToRefreshBox(
        isRefreshing = refreshing,
        onRefresh = onRefresh,
        modifier = modifier,
        content = content
    )
}

/**
 * 触底加载更多：监听 LazyColumn 滚到接近底部时回调（内部做 3 行阈值与去重）。
 * 与 [QuantumLoadMoreFooter] 配套：滚动触发 + 底部状态展示。
 */
@Composable
fun QuantumLoadMoreTrigger(
    state: LazyListState,
    enabled: Boolean,
    onLoadMore: () -> Unit
) {
    LaunchedEffect(state, enabled) {
        snapshotFlow {
            val last = state.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            last to state.layoutInfo.totalItemsCount
        }
            .distinctUntilChanged()
            .filter { (last, total) -> enabled && total > 0 && last >= total - 3 }
            .collect { onLoadMore() }
    }
}

/** 触底加载更多的底部状态：加载中 spinner / 已到底文案 / 失败重试。 */
@Composable
fun QuantumLoadMoreFooter(
    loading: Boolean,
    endReached: Boolean,
    modifier: Modifier = Modifier,
    errorText: String? = null,
    onRetry: (() -> Unit)? = null
) {
    Box(
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = QuantumSpacing.Lg),
        contentAlignment = Alignment.Center
    ) {
        when {
            loading -> Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(
                    modifier = Modifier.size(14.dp),
                    strokeWidth = 2.dp,
                    color = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                Text(
                    "正在加载…",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            errorText != null -> QuantumButton(
                text = errorText,
                onClick = { onRetry?.invoke() },
                variant = QuantumButtonVariant.Ghost,
                size = QuantumButtonSize.Sm
            )
            endReached -> Text(
                "已经到底了",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f)
            )
        }
    }
}

/**
 * 首屏概览卡：KPI + 角标 + 趋势 + 迷你柱状图（数据不足时传空 bars 即只显示 KPI）。
 */
@Composable
fun QuantumHeroCard(
    label: String,
    kpi: String,
    modifier: Modifier = Modifier,
    kpiUnit: String? = null,
    badge: String? = null,
    trend: String? = null,
    trendPositive: Boolean = true,
    bars: List<Float> = emptyList()
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = QuantumPage.Padding)
            .clip(RoundedCornerShape(QuantumRadius.Lg))
            .background(
                androidx.compose.ui.graphics.Brush.verticalGradient(
                    listOf(
                        MaterialTheme.colorScheme.surfaceContainerHigh,
                        MaterialTheme.colorScheme.surfaceContainer
                    )
                )
            )
            .padding(QuantumSpacing.Md + 2.dp)
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Row(
            modifier = Modifier.fillMaxWidth().padding(top = 2.dp),
            verticalAlignment = Alignment.Bottom
        ) {
            Text(
                text = kpi,
                fontSize = 26.sp,
                style = MaterialTheme.typography.headlineSmall,
                color = MaterialTheme.colorScheme.onSurface
            )
            if (kpiUnit != null) {
                Text(
                    text = kpiUnit,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(start = 4.dp, bottom = 3.dp)
                )
            }
            if (badge != null) {
                Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                Text(
                    text = badge,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier
                        .padding(bottom = 4.dp)
                        .clip(RoundedCornerShape(QuantumRadius.Pill))
                        .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.14f))
                        .padding(horizontal = 6.dp, vertical = 1.dp)
                )
            }
            Spacer(modifier = Modifier.weight(1f))
            if (trend != null) {
                Text(
                    text = trend,
                    style = MaterialTheme.typography.labelSmall,
                    color = if (trendPositive) {
                        MaterialTheme.colorScheme.tertiary
                    } else {
                        MaterialTheme.colorScheme.error
                    },
                    modifier = Modifier.padding(bottom = 4.dp)
                )
            }
        }
        if (bars.isNotEmpty()) {
            Row(
                modifier = Modifier.fillMaxWidth().height(34.dp).padding(top = QuantumSpacing.Sm),
                verticalAlignment = Alignment.Bottom,
                horizontalArrangement = Arrangement.spacedBy(QuantumSpacing.Xs)
            ) {
                val max = bars.max().coerceAtLeast(0.01f)
                bars.forEach { value ->
                    Box(
                        modifier = Modifier
                            .weight(1f)
                            .height((34f * (value / max)).dp)
                            .clip(RoundedCornerShape(3.dp))
                            .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.55f))
                    )
                }
            }
        }
    }
}

/** 页面级操作按钮（标题右侧图标按钮，44dp 触达）。 */
@Composable
fun QuantumIconAction(
    icon: ImageVector,
    contentDescription: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    Box(
        modifier = modifier
            .size(QuantumSize.TouchMin)
            .clip(RoundedCornerShape(QuantumRadius.Sm))
            .background(MaterialTheme.colorScheme.surfaceContainerHigh)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Icon(
            imageVector = icon,
            contentDescription = contentDescription,
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(QuantumSize.IconMd)
        )
    }
}

/**
 * 子页返回头（管理模块子页/详情页统一形态）：返回箭头 + 标题(+副标题) + 右侧动作区。
 * 这些页面底部导航随之隐藏（见 AppRoot bottomBar 可见性），返回只靠此头。
 */
@Composable
fun QuantumBackHeader(
    title: String,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    actions: (@Composable RowScope.() -> Unit)? = null
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = QuantumPage.Padding, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        QuantumIconAction(
            icon = Icons.AutoMirrored.Filled.ArrowBack,
            contentDescription = "返回",
            onClick = onBack
        )
        Spacer(modifier = Modifier.width(8.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
            subtitle?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
        actions?.invoke(this)
    }
}
