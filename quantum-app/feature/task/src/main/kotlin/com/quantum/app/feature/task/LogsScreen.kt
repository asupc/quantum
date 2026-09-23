package com.quantum.app.feature.task

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.ReceiptLong
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.common.ui.components.QuantumBackHeader
import com.quantum.app.core.common.ui.components.QuantumButton
import com.quantum.app.core.common.ui.components.QuantumButtonSize
import com.quantum.app.core.common.ui.components.QuantumButtonVariant
import com.quantum.app.core.common.ui.components.QuantumEmptyState
import com.quantum.app.core.common.ui.components.QuantumListRow
import com.quantum.app.core.common.ui.components.QuantumLoadingList
import com.quantum.app.core.common.ui.components.QuantumRowDivider
import com.quantum.app.core.common.ui.components.QuantumSegmentedTabs
import com.quantum.app.core.common.ui.components.QuantumTab
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.LogDto
import com.quantum.app.core.network.dto.LogStatisticsDto
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/** 日志中心/任务日志（分型按角色：非管理员仅任务日志/指令触发，服务端强制收敛）。 */
@HiltViewModel
class LogsViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    private val _logs = MutableStateFlow<List<LogDto>>(emptyList())
    val logs: StateFlow<List<LogDto>> = _logs.asStateFlow()

    private val _statistics = MutableStateFlow<LogStatisticsDto?>(null)
    val statistics: StateFlow<LogStatisticsDto?> = _statistics.asStateFlow()

    private val _logType = MutableStateFlow<Int?>(null)
    val logType: StateFlow<Int?> = _logType.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    /** 首次加载指示：用于骨架屏（避免列表区一片空白）。 */
    private val _loading = MutableStateFlow(true)
    val loading: StateFlow<Boolean> = _loading.asStateFlow()

    /** 任务日志模式的标题（按 taskId 载入后填任务名）。 */
    private val _title = MutableStateFlow("任务日志")
    val title: StateFlow<String> = _title.asStateFlow()

    /** 按任务查日志：GET api/Logs?Key=任务名（服务端 Key 匹配 Title/Remark/Operator）。 */
    fun loadByTaskName(taskName: String) {
        load(key = taskName, logType = 1)
    }

    /**
     * 任务日志页入口（路由 task/{id}/log）：先取任务名再按名过滤任务类日志（LogType=1）。
     * 通知点按直达这里，不再挂第二个 TaskScreen（那会让列表整页重载）。
     */
    fun loadByTaskId(taskId: String) {
        _logType.value = 1
        viewModelScope.launch {
            _loading.value = true
            // api/Task/{id} 查无任务时服务端返回 Code=200 + Data=null，unwrap 的 data as T
            // 会把 null 漏成非空类型（onSuccess 回调在 runCatching 之外，回调内抛异常无人
            // 捕获），必须空安全接收；任务取不到（已删除等）时退化为任务类日志全量
            val task: com.quantum.app.core.network.dto.TaskDto? =
                runCatching { adminApi.taskDetail(taskId).unwrap() }.getOrNull()
            val name = task?.name
            if (name != null) {
                _title.value = name
                load(key = name, logType = 1)
            } else {
                _toast.value = "任务不存在或已删除，已显示全部任务日志"
                load(key = null, logType = 1)
            }
            _loading.value = false
        }
    }

    fun load(key: String? = null, logType: Int? = _logType.value) {
        _logType.value = logType
        viewModelScope.launch {
            _loading.value = true
            runCatching {
                adminApi.logs(pageIndex = 1, pageSize = 50, key = key, logType = logType).unwrap()
            }.onSuccess { _logs.value = it.data }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
            _loading.value = false
        }
    }

    fun loadStatistics() {
        viewModelScope.launch {
            runCatching { adminApi.logStatistics(days = 7).unwrap() }
                .onSuccess { _statistics.value = it }
                .onFailure { e ->
                    // 非管理员访问统计被拒（Code=401 语义），静默隐藏入口
                    _statistics.value = null
                    _toast.value = (e as? ApiException)?.message
                }
        }
    }

    fun consumeToast() {
        _toast.value = null
    }
}

/** 日志详情页 VM（路由 manage/logs/{logId}，独立页面替代原详情弹层——弹层放不下长文也没法复制）。 */
@HiltViewModel
class LogDetailViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    private val _details = MutableStateFlow<String?>(null)
    val details: StateFlow<String?> = _details.asStateFlow()

    private val _loading = MutableStateFlow(true)
    val loading: StateFlow<Boolean> = _loading.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    fun load(logId: String) {
        viewModelScope.launch {
            _loading.value = true
            runCatching { adminApi.logDetails(logId).unwrap() }
                .onSuccess { _details.value = it }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
            _loading.value = false
        }
    }

    /** 复制成功反馈（「复制全文」按钮用）。 */
    fun copied() {
        _toast.value = "已复制到剪贴板"
    }

    fun consumeToast() {
        _toast.value = null
    }
}

/**
 * 日志中心页 / 任务日志页：
 * - 无 taskId（manage/logs）：类型选项卡 + 统一列表行 + 加载/空/数据三态；行点开详情页（可复制）；
 * - 带 taskId（task/{id}/log，通知深链与任务行「日志」按钮）：只显示该任务的任务类日志。
 * 两种模式均为子页：顶部返回头、底部导航隐藏。
 */
@Composable
fun LogsScreen(
    viewModel: LogsViewModel = hiltViewModel(),
    taskId: String? = null,
    onBack: (() -> Unit)? = null,
    onOpenDetails: (String) -> Unit = {}
) {
    val logs by viewModel.logs.collectAsState()
    val logType by viewModel.logType.collectAsState()
    val loading by viewModel.loading.collectAsState()
    val toast by viewModel.toast.collectAsState()
    val taskTitle by viewModel.title.collectAsState()

    LaunchedEffect(taskId) {
        if (taskId != null) {
            viewModel.loadByTaskId(taskId)
        } else {
            viewModel.load()
            viewModel.loadStatistics()
        }
    }

    // 类型值对齐后端 LogType 枚举（Enums.cs）：8=AI助手 9=AI试运行
    val logTypes = remember { listOf(null, 1, 5, 3, 2, 4, 6, 7, 8, 9) }
    val tabs = remember {
        listOf("全部", "任务", "指令", "操作", "通知", "登录", "外触", "系统", "AI助手", "AI试运行")
            .map { QuantumTab(it) }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        if (taskId != null) {
            // 任务日志头：返回 + 任务名
            QuantumBackHeader(
                title = taskTitle,
                subtitle = "仅显示该任务的任务类日志",
                onBack = { onBack?.invoke() }
            )
        } else {
            QuantumBackHeader(title = "日志中心", subtitle = "系统与审计日志", onBack = { onBack?.invoke() })
            QuantumSegmentedTabs(
                tabs = tabs,
                selectedIndex = logTypes.indexOf(logType).coerceAtLeast(0),
                onSelect = { index -> viewModel.load(logType = logTypes[index]) },
                scrollable = true,
                modifier = Modifier.padding(vertical = QuantumPage.CardGap)
            )
        }

        toast?.let {
            Text(
                it,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(horizontal = QuantumPage.Padding)
            )
        }

        when {
            loading && logs.isEmpty() -> QuantumLoadingList(rows = 5, modifier = Modifier.weight(1f))
            logs.isEmpty() -> QuantumEmptyState(
                icon = Icons.Filled.ReceiptLong,
                title = "暂无日志",
                description = if (taskId != null) {
                    "该任务还没有执行日志记录"
                } else {
                    "当前类型下没有记录，切换类型或稍后再看"
                },
                modifier = Modifier.weight(1f)
            )
            else -> LazyColumn(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .padding(horizontal = QuantumPage.Padding, vertical = QuantumPage.GroupGap)
                    .clip(RoundedCornerShape(QuantumRadius.Md))
                    .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(QuantumRadius.Md))
                    .background(MaterialTheme.colorScheme.surfaceContainer)
            ) {
                itemsIndexed(logs, key = { _, log -> log.id }) { index, log ->
                    QuantumListRow(
                        title = "${log.title ?: "日志"}${if (log.success) "" else "（失败）"}",
                        subtitle = "${log.createTime} · ${log.operator ?: "-"}",
                        leadingIcon = if (log.success) Icons.Filled.CheckCircle else Icons.Filled.Warning,
                        leadingTint = if (log.success) {
                            MaterialTheme.colorScheme.tertiary
                        } else {
                            MaterialTheme.colorScheme.error
                        },
                        showChevron = true,
                        onClick = { onOpenDetails(log.id) }
                    )
                    if (index != logs.lastIndex) {
                        QuantumRowDivider()
                    }
                }
            }
        }
    }
}

/**
 * 日志详情页（2026-09-20 由弹层改独立页面）：返回头 + 全文滚动 + 选择复制 + 「复制全文」一键。
 */
@Composable
fun LogDetailScreen(
    logId: String,
    onBack: () -> Unit,
    viewModel: LogDetailViewModel = hiltViewModel()
) {
    val details by viewModel.details.collectAsState()
    val loading by viewModel.loading.collectAsState()
    val toast by viewModel.toast.collectAsState()
    val clipboard = LocalClipboardManager.current

    LaunchedEffect(logId) { viewModel.load(logId) }

    Column(modifier = Modifier.fillMaxSize()) {
        QuantumBackHeader(
            title = "日志详情",
            onBack = onBack,
            actions = {
                details?.let {
                    QuantumButton(
                        text = "复制全文",
                        icon = Icons.Filled.ContentCopy,
                        variant = QuantumButtonVariant.Tonal,
                        size = QuantumButtonSize.Sm,
                        onClick = {
                            clipboard.setText(AnnotatedString(it))
                            viewModel.copied()
                        }
                    )
                }
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

        when {
            loading && details == null -> QuantumLoadingList(rows = 5, modifier = Modifier.weight(1f))
            details == null -> QuantumEmptyState(
                icon = Icons.Filled.ReceiptLong,
                title = "详情加载失败",
                description = "稍后重试或查看任务日志",
                modifier = Modifier.weight(1f)
            )
            else -> Column(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = QuantumPage.Padding, vertical = QuantumSpacing.Sm)
            ) {
                SelectionContainer {
                    Text(
                        details.orEmpty(),
                        style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(QuantumRadius.Md))
                            .background(MaterialTheme.colorScheme.surfaceContainer)
                            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(QuantumRadius.Md))
                            .padding(QuantumSpacing.Md)
                    )
                }
                Spacer(modifier = Modifier.height(QuantumPage.GroupGap))
            }
        }
    }
}
