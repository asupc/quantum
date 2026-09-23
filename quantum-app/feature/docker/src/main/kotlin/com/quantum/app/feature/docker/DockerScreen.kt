package com.quantum.app.feature.docker

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
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
import com.quantum.app.core.common.ui.components.ConfirmDialog
import com.quantum.app.core.common.ui.components.QuantumBackHeader
import com.quantum.app.core.common.ui.components.QuantumButton
import com.quantum.app.core.common.ui.components.QuantumButtonSize
import com.quantum.app.core.common.ui.components.QuantumButtonVariant
import com.quantum.app.core.common.ui.components.QuantumEmptyState
import com.quantum.app.core.common.ui.components.QuantumIconAction
import com.quantum.app.core.common.ui.components.QuantumListCard
import com.quantum.app.core.common.ui.components.QuantumListRow
import com.quantum.app.core.common.ui.components.QuantumLoadingList
import com.quantum.app.core.common.ui.components.QuantumStatusBadge
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.DockerContainerDto
import com.quantum.app.core.network.dto.DockerStatsDto
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * Docker 只读面板 + 启停（管理员专用：服务端 [ManagerOnly] 双保险，普通用户入口不可见也调不通）。
 * 2026-09-20：容器卡片加边框、运行中容器并行拉 stats 显示内存占用（working set 口径）、
 * 日志改独立页面（原弹层放不下长文也没法复制）。
 */
@HiltViewModel
class DockerViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    private val _containers = MutableStateFlow<List<DockerContainerDto>>(emptyList())
    val containers: StateFlow<List<DockerContainerDto>> = _containers.asStateFlow()

    /** 运行中容器的内存占用（容器 id → 已格式化文案；拉取失败/已停止不进表）。 */
    private val _memory = MutableStateFlow<Map<String, String>>(emptyMap())
    val memory: StateFlow<Map<String, String>> = _memory.asStateFlow()

    private val _confirmAction = MutableStateFlow<Pair<String, String>?>(null)
    val confirmAction: StateFlow<Pair<String, String>?> = _confirmAction.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    /** 首屏加载指示（此前无任何状态：失败与"真的没有容器"看起来一模一样）。 */
    private val _loading = MutableStateFlow(true)
    val loading: StateFlow<Boolean> = _loading.asStateFlow()

    /** 列表加载失败文案：空列表且非加载中时渲染错误态 + 重试。 */
    private val _error = MutableStateFlow<String?>(null)
    val error: StateFlow<String?> = _error.asStateFlow()

    init {
        refresh()
    }

    fun refresh() {
        viewModelScope.launch {
            _loading.value = true
            runCatching { adminApi.dockerContainers(all = true).unwrap() }
                .onSuccess {
                    _containers.value = it
                    _error.value = null
                    loadMemory(it.filter { container -> container.state == "running" })
                }
                .onFailure { e ->
                    val message = (e as? ApiException)?.message ?: e.message ?: "容器列表获取失败"
                    _error.value = message
                    _toast.value = message
                }
            _loading.value = false
        }
    }

    /** 逐容器并行拉 stats（OneShot 端点，容器数个、各一次短请求）；失败静默不显示内存。 */
    private fun loadMemory(running: List<DockerContainerDto>) {
        _memory.value = emptyMap()
        running.forEach { container ->
            viewModelScope.launch {
                val text = runCatching { adminApi.dockerStats(container.id).unwrap() }
                    .getOrNull()
                    ?.let(::formatMemory)
                if (text != null) {
                    _memory.update { it + (container.id to text) }
                }
            }
        }
    }

    /** 内存 working set：cgroup v2 的 usage 含 page cache，减 stats["inactive_file"]（缺失按 v1 直接用 usage）。 */
    private fun formatMemory(stats: DockerStatsDto): String? {
        val usage = stats.memoryStats?.usage ?: 0L
        if (usage <= 0L) {
            return null
        }
        val inactive = stats.memoryStats?.stats?.get("inactive_file") ?: 0L
        val workingSet = (usage - inactive).coerceAtLeast(0L)
        val mb = workingSet / 1024.0 / 1024.0
        return if (mb >= 1024.0) "内存 %.2f GB".format(mb / 1024.0) else "内存 %.1f MB".format(mb)
    }

    fun requestAction(containerId: String, action: String) {
        _confirmAction.value = containerId to action
    }

    fun performAction(containerId: String, action: String) {
        viewModelScope.launch {
            runCatching {
                when (action) {
                    "start" -> adminApi.dockerStart(containerId).unwrap()
                    "restart" -> adminApi.dockerRestart(containerId).unwrap()
                    else -> adminApi.dockerStop(containerId).unwrap()
                }
            }.onSuccess { refresh() }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    fun dismissConfirm() {
        _confirmAction.value = null
    }

    fun consumeToast() {
        _toast.value = null
    }
}

/** 容器日志页 VM（独立页面替代原弹层，2026-09-20）。 */
@HiltViewModel
class DockerLogsViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    private val _logs = MutableStateFlow<String?>(null)
    val logs: StateFlow<String?> = _logs.asStateFlow()

    private val _loading = MutableStateFlow(true)
    val loading: StateFlow<Boolean> = _loading.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    private var inFlight = false

    fun load(containerId: String) {
        if (inFlight) {
            return
        }
        inFlight = true
        viewModelScope.launch {
            _loading.value = true
            runCatching { adminApi.dockerLogs(containerId, tail = "200").unwrap() }
                .onSuccess { _logs.value = it }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message ?: "日志读取失败" }
            _loading.value = false
            inFlight = false
        }
    }

    /** 复制成功反馈（「复制日志」按钮用）。 */
    fun copied() {
        _toast.value = "已复制到剪贴板"
    }

    fun consumeToast() {
        _toast.value = null
    }
}

@Composable
fun DockerScreen(
    onBack: () -> Unit,
    onOpenLogs: (DockerContainerDto) -> Unit,
    viewModel: DockerViewModel = hiltViewModel()
) {
    val containers by viewModel.containers.collectAsState()
    val memory by viewModel.memory.collectAsState()
    val confirmAction by viewModel.confirmAction.collectAsState()
    val loading by viewModel.loading.collectAsState()
    val error by viewModel.error.collectAsState()
    val toast by viewModel.toast.collectAsState()

    LaunchedEffect(Unit) { viewModel.refresh() }

    Column(modifier = Modifier.fillMaxSize()) {
        QuantumBackHeader(
            title = "容器",
            subtitle = if (loading) "加载中…" else "共 ${containers.size} 个",
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

        when {
            loading && containers.isEmpty() -> QuantumLoadingList(rows = 4, modifier = Modifier.weight(1f))

            containers.isEmpty() && error != null -> QuantumEmptyState(
                icon = Icons.Filled.Warning,
                title = "容器列表加载失败",
                description = error,
                actionText = "重试",
                onAction = { viewModel.refresh() },
                modifier = Modifier.weight(1f)
            )

            containers.isEmpty() -> QuantumEmptyState(
                icon = Icons.Filled.Dns,
                title = "没有读到容器",
                description = "服务端未挂载 docker.sock 或 Docker 未运行时，此页无数据",
                actionText = "重试",
                onAction = { viewModel.refresh() },
                modifier = Modifier.weight(1f)
            )

            else -> LazyColumn(
                modifier = Modifier.weight(1f),
                contentPadding = androidx.compose.foundation.layout.PaddingValues(
                    start = QuantumPage.Padding,
                    end = QuantumPage.Padding,
                    top = QuantumSpacing.Xs,
                    bottom = QuantumPage.GroupGap
                )
            ) {
                items(containers, key = { it.id }) { container ->
                    val running = container.state == "running"
                    QuantumListCard(
                        modifier = Modifier.padding(vertical = QuantumSpacing.Xs),
                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant)
                    ) {
                        Column(modifier = Modifier.fillMaxWidth()) {
                            QuantumListRow(
                                title = container.names.firstOrNull()?.removePrefix("/") ?: container.id.take(12),
                                // 镜像名不再显示（2026-09-20 用户要求）：名称长刷屏且日常用不上，要看去 Web 端
                                subtitle = listOfNotNull(
                                    container.status ?: container.state,
                                    // 运行中容器才显示内存（stats 拉取失败则省略该段）
                                    if (running) memory[container.id] else null
                                ).joinToString(" · "),
                                trailing = {
                                    QuantumStatusBadge(
                                        text = if (running) "运行中" else (container.state ?: "已停止"),
                                        active = running
                                    )
                                },
                                showChevron = true,
                                onClick = { onOpenLogs(container) }
                            )
                            container.created?.let {
                                Text(
                                    "创建于 $it",
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    modifier = Modifier.padding(horizontal = QuantumSpacing.Md, vertical = 2.dp)
                                )
                            }
                            Row {
                                TextButton(onClick = { viewModel.requestAction(container.id, if (running) "stop" else "start") }) {
                                    Text(if (running) "停止" else "启动")
                                }
                                TextButton(onClick = { viewModel.requestAction(container.id, "restart") }) { Text("重启") }
                                TextButton(onClick = { onOpenLogs(container) }) { Text("日志") }
                            }
                        }
                    }
                }
            }
        }
    }

    confirmAction?.let { (id, action) ->
        ConfirmDialog(
            title = "确认${when (action) { "stop" -> "停止"; "restart" -> "重启"; else -> "启动" }}容器？",
            content = "该操作直接影响宿主机服务，请确认容器用途后再继续。",
            onConfirm = { viewModel.performAction(id, action) },
            onDismiss = { viewModel.dismissConfirm() }
        )
    }
}

/**
 * 容器日志页：返回头（容器名）+ 刷新 + 末 200 行滚动 + 选择复制 + 「复制日志」一键。
 */
@Composable
fun DockerLogsScreen(
    containerId: String,
    containerName: String,
    onBack: () -> Unit,
    viewModel: DockerLogsViewModel = hiltViewModel()
) {
    val logs by viewModel.logs.collectAsState()
    val loading by viewModel.loading.collectAsState()
    val toast by viewModel.toast.collectAsState()
    val clipboard = LocalClipboardManager.current

    LaunchedEffect(containerId) { viewModel.load(containerId) }

    Column(modifier = Modifier.fillMaxSize()) {
        QuantumBackHeader(
            title = containerName.ifBlank { containerId.take(12) },
            subtitle = "末 200 行日志",
            onBack = onBack,
            actions = {
                QuantumButton(
                    text = "刷新",
                    icon = Icons.Filled.Refresh,
                    variant = QuantumButtonVariant.Tonal,
                    size = QuantumButtonSize.Sm,
                    onClick = { viewModel.load(containerId) }
                )
                Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                logs?.let {
                    QuantumButton(
                        text = "复制",
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
            loading && logs == null -> QuantumLoadingList(rows = 6, modifier = Modifier.weight(1f))
            logs == null -> QuantumEmptyState(
                icon = Icons.Filled.Warning,
                title = "日志读取失败",
                description = "容器可能已停止，稍后重试",
                modifier = Modifier.weight(1f)
            )
            logs.isNullOrEmpty() -> QuantumEmptyState(
                icon = Icons.Filled.Dns,
                title = "暂无日志",
                description = "该容器最近没有输出",
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
                        logs.orEmpty().takeLast(60_000),
                        style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(QuantumRadius.Md))
                            .padding(QuantumSpacing.Md)
                    )
                }
                Spacer(modifier = Modifier.height(QuantumPage.GroupGap))
            }
        }
    }
}
