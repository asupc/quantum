package com.quantum.app.feature.task

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Block
import androidx.compose.material.icons.filled.Bolt
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.HourglassEmpty
import androidx.compose.material.icons.filled.PauseCircleOutline
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.common.ui.components.QuantumBackHeader
import com.quantum.app.core.common.ui.components.QuantumEmptyState
import com.quantum.app.core.common.ui.components.QuantumIconAction
import com.quantum.app.core.common.ui.components.QuantumListCard
import com.quantum.app.core.common.ui.components.QuantumListRow
import com.quantum.app.core.common.ui.components.QuantumLoadMoreFooter
import com.quantum.app.core.common.ui.components.QuantumLoadMoreTrigger
import com.quantum.app.core.common.ui.components.QuantumLoadingList
import com.quantum.app.core.common.ui.components.QuantumPullRefresh
import com.quantum.app.core.common.ui.components.QuantumSegmentedTabs
import com.quantum.app.core.common.ui.components.QuantumTab
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.TaskRunRowDto
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * 执行记录列表（一期 G2 App 只读）：与 TaskViewModel 同一套分页/刷新/错误态写法
 * （触底加载 + 下拉刷新 + 首屏骨架 + 失败重试），只是数据源换成 GET api/TaskRun。
 *
 * 权限口径：**客户端不做任何显隐判断**——非 Manager 令牌拿不到 Manager 任务的执行记录，
 * 由服务端按 JWT 正向 Manager claim 过滤；空列表可能就是「服务端收敛后的空」。
 * 只读：手动重新执行（POST api/TaskRun/{runId}/retry）是 [ManagerOnly] 写操作，App 首期不提供，
 * 失败策略编辑同样只在 Web。
 */
@HiltViewModel
class TaskRunsViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    data class RunPage(val items: List<TaskRunRowDto> = emptyList(), val total: Int = 0)

    companion object {
        /** 服务端分页大小（触底加载按此翻页；服务端 clamp 1..100）。 */
        const val PAGE_SIZE = 20

        /** 回溯天数，与服务端运行记录保留口径一致。 */
        const val DAYS = 90
    }

    /** 当前过滤的任务（null = 全部执行记录）；由 [load] 从路由参数落定。 */
    private val _taskId = MutableStateFlow<String?>(null)
    val taskId: StateFlow<String?> = _taskId.asStateFlow()

    private val _page = MutableStateFlow(RunPage())
    val page: StateFlow<RunPage> = _page.asStateFlow()

    private val _status = MutableStateFlow<String?>(null)
    val status: StateFlow<String?> = _status.asStateFlow()

    private val _pageIndex = MutableStateFlow(1)
    private val _loading = MutableStateFlow(false)
    val loading: StateFlow<Boolean> = _loading.asStateFlow()

    /** 下拉刷新中（与首屏 loading 分开：刷新时列表已有数据，不应换成骨架屏）。 */
    private val _refreshing = MutableStateFlow(false)
    val refreshing: StateFlow<Boolean> = _refreshing.asStateFlow()

    private val _endReached = MutableStateFlow(false)
    val endReached: StateFlow<Boolean> = _endReached.asStateFlow()

    /** 列表加载失败文案：空列表时渲染错误态 + 重试（区别于「真的没有数据」）。 */
    private val _error = MutableStateFlow<String?>(null)
    val error: StateFlow<String?> = _error.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    /**
     * 载入第一页：taskId/status 任缺省沿用当前值（路由参数进来在这里落定）。
     * 服务端「找不到/越权」不会让列表报错，只会给出过滤后的空集，故空态文案不写「无权限」。
     */
    fun load(
        taskId: String? = _taskId.value,
        status: String? = _status.value,
        pageIndex: Int = 1,
        refreshing: Boolean = false
    ) {
        _taskId.value = taskId
        _status.value = status
        _pageIndex.value = pageIndex
        if (refreshing) {
            _refreshing.value = true
        } else {
            _loading.value = true
        }
        viewModelScope.launch {
            runCatching {
                adminApi.taskRuns(
                    taskId = taskId,
                    status = status,
                    page = pageIndex,
                    pageSize = PAGE_SIZE,
                    days = DAYS
                ).unwrap()
            }.onSuccess {
                _page.value = RunPage(it.data, it.totalCount)
                _endReached.value = it.data.size >= it.totalCount
                _error.value = null
            }.onFailure { e ->
                val message = (e as? ApiException)?.message ?: e.message
                _error.value = message
                _toast.value = message
            }.also {
                _loading.value = false
                _refreshing.value = false
            }
        }
    }

    /** 下拉刷新：回到第一页，保留当前任务与状态筛选。 */
    fun refresh() = load(pageIndex = 1, refreshing = true)

    /** 状态筛选（枚举名字符串，null=全部）：换筛选条件即重拉第一页。 */
    fun selectStatus(status: String?) = load(status = status, pageIndex = 1)

    /**
     * 触底加载下一页：**追加**而非替换，按 id 去重（服务端数据变动时不出现重复行）。
     */
    fun loadMore() {
        if (_loading.value || _refreshing.value || _endReached.value) {
            return
        }
        val next = _pageIndex.value + 1
        _loading.value = true
        viewModelScope.launch {
            runCatching {
                adminApi.taskRuns(
                    taskId = _taskId.value,
                    status = _status.value,
                    page = next,
                    pageSize = PAGE_SIZE,
                    days = DAYS
                ).unwrap()
            }.onSuccess {
                _pageIndex.value = next
                val existing = _page.value.items.map { row -> row.id }.toSet()
                val merged = _page.value.items + it.data.filterNot { row -> row.id in existing }
                _page.value = RunPage(merged, it.totalCount)
                _endReached.value = merged.size >= it.totalCount || it.data.isEmpty()
            }.onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
                .also { _loading.value = false }
        }
    }

    fun consumeToast() {
        _toast.value = null
    }
}

/**
 * 最近执行列表页（子页形态，与任务日志页同款：顶部返回头、底部导航由 Shell 隐藏）。
 * - `taskId != null`（路由 task/{taskId}/runs）：只看该任务，标题取行内任务名快照；
 * - `taskId == null`（路由 manage/runs）：全平台最近 [TaskRunsViewModel.DAYS] 天的执行记录。
 *
 * 行不显示脚本路径（服务端快照里有 ScriptFile，DTO 刻意不解码）；点开进执行详情。
 */
@Composable
fun TaskRunsScreen(
    taskId: String? = null,
    onBack: () -> Unit,
    onOpenRun: (runId: String, taskId: String?) -> Unit = { _, _ -> },
    viewModel: TaskRunsViewModel = hiltViewModel()
) {
    val page by viewModel.page.collectAsState()
    val currentTaskId by viewModel.taskId.collectAsState()
    val status by viewModel.status.collectAsState()
    val loading by viewModel.loading.collectAsState()
    val refreshing by viewModel.refreshing.collectAsState()
    val endReached by viewModel.endReached.collectAsState()
    val error by viewModel.error.collectAsState()
    val toast by viewModel.toast.collectAsState()

    val listState = rememberLazyListState()
    QuantumLoadMoreTrigger(
        state = listState,
        enabled = page.items.isNotEmpty() && !endReached && !loading && !refreshing,
        onLoadMore = { viewModel.loadMore() }
    )

    LaunchedEffect(taskId) { viewModel.load(taskId = taskId) }

    val filters = remember { TaskRunDisplay.STATUS_FILTERS }
    val tabs = remember(filters) { filters.map { QuantumTab(it.second) } }
    val selectedIndex = remember(status) { filters.indexOfFirst { it.first == status }.coerceAtLeast(0) }
    // 任务名取行内快照（服务端的 TaskNameSnapshot，任务删除后仍正确），不再多拉一次任务详情
    val headerTaskName = page.items.firstOrNull()?.taskName?.takeIf { it.isNotBlank() }

    Column(modifier = Modifier.fillMaxSize()) {
        QuantumBackHeader(
            title = if (currentTaskId != null) headerTaskName ?: "最近执行" else "执行记录",
            subtitle = when {
                currentTaskId != null && page.total > 0 -> "共 ${page.total} 条 · 只读"
                currentTaskId != null -> "该任务最近 ${TaskRunsViewModel.DAYS} 天的执行记录 · 只读"
                else -> "最近 ${TaskRunsViewModel.DAYS} 天的执行记录 · 只读"
            },
            onBack = onBack,
            actions = {
                QuantumIconAction(
                    icon = Icons.Filled.Refresh,
                    contentDescription = "刷新",
                    onClick = { viewModel.refresh() }
                )
            }
        )

        toast?.let {
            Text(
                it,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(horizontal = QuantumPage.Padding)
            )
        }

        QuantumSegmentedTabs(
            tabs = tabs,
            selectedIndex = selectedIndex,
            onSelect = { index -> viewModel.selectStatus(filters[index].first) },
            scrollable = true,
            modifier = Modifier.padding(vertical = QuantumPage.CardGap)
        )

        QuantumPullRefresh(
            refreshing = refreshing,
            onRefresh = { viewModel.refresh() },
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth()
        ) {
            when {
                loading && page.items.isEmpty() -> QuantumLoadingList(rows = 5)

                page.items.isEmpty() && error != null -> QuantumEmptyState(
                    icon = Icons.Filled.Warning,
                    title = "加载失败",
                    description = error,
                    actionText = "重试",
                    onAction = { viewModel.refresh() },
                    modifier = Modifier.fillMaxSize()
                )

                page.items.isEmpty() -> QuantumEmptyState(
                    icon = Icons.Filled.HourglassEmpty,
                    title = "暂无执行记录",
                    description = if (currentTaskId != null) {
                        "该任务还没有被触发过，或记录已超出 ${TaskRunsViewModel.DAYS} 天窗口"
                    } else {
                        "最近 ${TaskRunsViewModel.DAYS} 天没有执行记录"
                    },
                    actionText = "重新加载",
                    onAction = { viewModel.refresh() },
                    modifier = Modifier.fillMaxSize()
                )

                else -> LazyColumn(
                    state = listState,
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(horizontal = QuantumPage.Padding),
                    verticalArrangement = Arrangement.spacedBy(QuantumPage.CardGap)
                ) {
                    items(page.items, key = { row -> row.id }) { row ->
                        TaskRunRowCard(
                            row = row,
                            showTaskName = currentTaskId == null,
                            onClick = { onOpenRun(row.id, row.taskId ?: currentTaskId) }
                        )
                    }
                    item(key = "loadmore") {
                        QuantumLoadMoreFooter(loading = loading, endReached = endReached)
                    }
                }
            }
        }
    }
}

/**
 * 执行记录行：状态胶囊（+ 重试行突出「第 N 次尝试」）、触发源/耗时/开始时间、安全摘要单独一行。
 * 耗时缺失（Pending/Running）显示「-」而不是 0；时间一律 UTC→本地（见 TaskRunDisplay）。
 */
@Composable
private fun TaskRunRowCard(row: TaskRunRowDto, showTaskName: Boolean, onClick: () -> Unit) {
    val scheme = MaterialTheme.colorScheme
    val summary = row.safeSummary?.trim()?.takeIf { it.isNotEmpty() }
    QuantumListCard(
        modifier = Modifier.padding(vertical = QuantumSpacing.Xs)
    ) {
        QuantumListRow(
            title = if (showTaskName) {
                row.taskName?.takeIf { it.isNotBlank() } ?: "未关联任务"
            } else {
                TaskRunDisplay.triggerLabel(row.triggerSource)
            },
            subtitle = listOfNotNull(
                if (showTaskName) TaskRunDisplay.triggerLabel(row.triggerSource) else null,
                "耗时 ${TaskRunDisplay.formatElapsed(row.elapsedMs)}",
                TaskRunDisplay.formatUtcToLocal(
                    TaskRunDisplay.startedAtOrCreated(row.startedAtUtc, row.createdAtUtc),
                    pattern = "MM-dd HH:mm:ss"
                )
            ).joinToString(" · "),
            leadingIcon = runStatusIcon(row.status),
            leadingTint = runToneColor(TaskRunDisplay.statusMeta(row.status).tone),
            showChevron = true,
            onClick = onClick,
            trailing = {
                Column(horizontalAlignment = Alignment.End) {
                    RunStatusChip(row.status)
                    TaskRunDisplay.attemptBadge(row.attempt)?.let {
                        Text(
                            it,
                            style = MaterialTheme.typography.labelSmall,
                            color = scheme.primary,
                            modifier = Modifier.padding(top = QuantumSpacing.Xs)
                        )
                    }
                }
            }
        )
        if (summary != null) {
            Text(
                summary,
                style = MaterialTheme.typography.bodySmall,
                color = scheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier
                    .padding(
                        start = QuantumSpacing.Md + 2.dp,
                        end = QuantumSpacing.Md + 2.dp,
                        bottom = QuantumSpacing.Md
                    )
                    .alpha(0.9f)
            )
        }
    }
}

/**
 * 状态胶囊（沿用 core:common 的胶囊形态，扩到五个色档）：
 * QuantumStatusBadge 只有「亮/灭」两态，撑不起 成功/异常/拒绝/取消/中断 的语义区分。
 */
@Composable
fun RunStatusChip(status: String?, modifier: Modifier = Modifier) {
    val meta = TaskRunDisplay.statusMeta(status)
    val color = runToneColor(meta.tone)
    Surface(
        modifier = modifier,
        shape = RoundedCornerShape(QuantumRadius.Pill),
        color = color.copy(alpha = 0.12f),
        border = BorderStroke(1.dp, color.copy(alpha = 0.35f))
    ) {
        Row(
            modifier = Modifier.padding(horizontal = QuantumSpacing.Sm, vertical = 3.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                modifier = Modifier
                    .size(6.dp)
                    .clip(CircleShape)
                    .background(color)
            )
            Spacer(modifier = Modifier.width(5.dp))
            Text(
                text = meta.label,
                style = MaterialTheme.typography.labelSmall,
                fontWeight = FontWeight.Medium,
                color = color
            )
        }
    }
}

/** 色档 → 主题色：与 Web 端 RunStatusMeta 的 ok/danger/warn/accent/muted 一一对应。 */
@Composable
fun runToneColor(tone: RunTone): Color = when (tone) {
    RunTone.Ok -> MaterialTheme.colorScheme.tertiary
    RunTone.Danger -> MaterialTheme.colorScheme.error
    // 警示档取调色板 Amber（端内 warn 语义色，与富文本 orange 标记同源）
    RunTone.Warn -> com.quantum.app.core.common.ui.theme.QuantumPalette.Amber
    RunTone.Accent -> MaterialTheme.colorScheme.primary
    RunTone.Muted -> MaterialTheme.colorScheme.onSurfaceVariant
}

/** 状态图标：终态用形状区分，颜色只按色档给（不靠颜色反推结果）。 */
@Composable
private fun runStatusIcon(status: String?): ImageVector =
    when (TaskRunDisplay.statusMeta(status).tone) {
        RunTone.Ok -> Icons.Filled.CheckCircle
        RunTone.Danger -> Icons.Filled.Warning
        RunTone.Warn -> Icons.Filled.PauseCircleOutline
        RunTone.Accent -> Icons.Filled.Bolt
        RunTone.Muted -> if (status == "Rejected") Icons.Filled.Block else Icons.Filled.Schedule
    }
