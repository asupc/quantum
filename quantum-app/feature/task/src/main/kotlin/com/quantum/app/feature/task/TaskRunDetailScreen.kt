package com.quantum.app.feature.task

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Article
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
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
import com.quantum.app.core.common.ui.components.QuantumIconAction
import com.quantum.app.core.common.ui.components.QuantumListCard
import com.quantum.app.core.common.ui.components.QuantumLoadingList
import com.quantum.app.core.common.ui.components.QuantumSectionHeader
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.TaskRunDetailDto
import com.quantum.app.core.network.dto.TaskRunRowDto
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * 执行详情（GET api/TaskRun/{runId}，一期 G2 App 只读）。
 * 服务端把「不存在」与「越权」同为一条「执行记录不存在」，故错误态只给一个文案分支。
 */
@HiltViewModel
class TaskRunDetailViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    private var runId: String? = null

    private val _detail = MutableStateFlow<TaskRunDetailDto?>(null)
    val detail: StateFlow<TaskRunDetailDto?> = _detail.asStateFlow()

    private val _loading = MutableStateFlow(true)
    val loading: StateFlow<Boolean> = _loading.asStateFlow()

    private val _error = MutableStateFlow<String?>(null)
    val error: StateFlow<String?> = _error.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    fun load(runId: String) {
        this.runId = runId
        viewModelScope.launch {
            _loading.value = true
            runCatching { adminApi.taskRunDetail(runId).unwrap() }
                .onSuccess {
                    _detail.value = it
                    _error.value = null
                }
                .onFailure { e ->
                    val message = (e as? ApiException)?.message ?: e.message
                    _detail.value = null
                    _error.value = message
                    _toast.value = message
                }
            _loading.value = false
        }
    }

    /** 重新拉取（保持路由里的 runId；日志/重试状态可能已变化）。 */
    fun refresh() {
        runId?.let { load(it) }
    }

    fun consumeToast() {
        _toast.value = null
    }
}

/**
 * 执行详情页：本次执行概览 + 同一根执行的尝试时间轴 + 日志入口。
 *
 * 两个硬口径：
 * - **不拼接、不显示任何文件路径**：DTO 未解码 ScriptFile，日志可达性只给 `LogAvailable`/`LogId`，
 *   「查看日志」复用 App 既有日志详情链路（manage/logs/{logId}）；`LogAvailable=false` 只显示占位文案；
 * - 时间一律 UTC 串按 UTC 解读后转本地显示（TaskRunDisplay）。
 */
@Composable
fun TaskRunDetailScreen(
    runId: String,
    onBack: () -> Unit,
    onOpenLog: (String) -> Unit = {},
    viewModel: TaskRunDetailViewModel = hiltViewModel()
) {
    val detail by viewModel.detail.collectAsState()
    val loading by viewModel.loading.collectAsState()
    val error by viewModel.error.collectAsState()
    val toast by viewModel.toast.collectAsState()

    LaunchedEffect(runId) { viewModel.load(runId) }

    // 委托属性（by collectAsState()）不能被智能转换，先落一个稳定局部值再分支
    val detailValue: TaskRunDetailDto? = detail

    Column(modifier = Modifier.fillMaxSize()) {
        QuantumBackHeader(
            title = detailValue?.run?.taskName?.takeIf { it.isNotBlank() } ?: "执行详情",
            subtitle = "只读视图 · 状态描述脚本执行结果，不代表接口是否成功",
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
            loading && detailValue == null -> QuantumLoadingList(rows = 4, modifier = Modifier.weight(1f))
            detailValue == null -> QuantumEmptyState(
                icon = Icons.Filled.Warning,
                title = "执行记录不存在",
                description = error ?: "记录可能已超出保留窗口，或不属于当前账号可见范围",
                actionText = "重试",
                onAction = { viewModel.refresh() },
                modifier = Modifier.weight(1f)
            )
            else -> Column(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = QuantumPage.Padding, vertical = QuantumPage.CardGap)
            ) {
                RunOverviewCard(detailValue.run)
                Spacer(modifier = Modifier.height(QuantumPage.GroupGap))
                RunTimeline(detailValue.attempts)
                Spacer(modifier = Modifier.height(QuantumPage.GroupGap))
                RunLogEntry(
                    logAvailable = detailValue.logAvailable,
                    logId = detailValue.logId,
                    onOpenLog = onOpenLog
                )
                Spacer(modifier = Modifier.height(QuantumPage.GroupGap))
            }
        }
    }
}

/** 概览卡：状态/触发源/尝试/耗时/起止/安全摘要/中止原因。 */
@Composable
private fun RunOverviewCard(run: TaskRunRowDto?) {
    if (run == null) {
        return
    }
    val meta = TaskRunDisplay.statusMeta(run.status)
    val toneColor = runToneColor(meta.tone)
    QuantumListCard {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(
                    start = QuantumSpacing.Md + 2.dp,
                    end = QuantumSpacing.Md + 2.dp,
                    top = QuantumSpacing.Md
                ),
            verticalAlignment = Alignment.CenterVertically
        ) {
            RunStatusChip(run.status)
            TaskRunDisplay.attemptBadge(run.attempt)?.let {
                Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                Text(
                    it,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.primary
                )
            }
        }
        Column(modifier = Modifier.padding(horizontal = QuantumSpacing.Md + 2.dp, vertical = QuantumSpacing.Sm)) {
            TaskRunDetailField("触发源", TaskRunDisplay.triggerLabel(run.triggerSource))
            TaskRunDetailField("耗时", TaskRunDisplay.formatElapsed(run.elapsedMs))
            TaskRunDetailField(
                "开始时间",
                TaskRunDisplay.formatUtcToLocal(TaskRunDisplay.startedAtOrCreated(run.startedAtUtc, run.createdAtUtc))
            )
            TaskRunDetailField("结束时间", TaskRunDisplay.formatUtcToLocal(run.finishedAtUtc))
            // 到期重试时刻只在服务端排了下次重试时非空（Failed 且策略允许）
            run.nextAttemptAtUtc?.takeIf { it.isNotBlank() }?.let {
                TaskRunDetailField("下次重试", TaskRunDisplay.formatUtcToLocal(it))
            }
            TaskRunDetailField(
                "安全摘要",
                TaskRunDisplay.summaryOf(run.status, run.safeSummary),
                valueColor = toneColor,
                multiline = true
            )
            run.cancelReason?.takeIf { it.isNotBlank() }?.let {
                TaskRunDetailField("中止原因", it, valueColor = MaterialTheme.colorScheme.error, multiline = true)
            }
            // RunId 可复制（报障时与 Web 端详情同一口径），它不是文件路径
            Text(
                "RunId",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = QuantumSpacing.Sm)
            )
            SelectionContainer {
                Text(
                    run.id,
                    style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 2.dp)
                )
            }
        }
    }
}

/** 尝试时间轴：Attempts 按 Attempt 升序，每项「第 N 次尝试 · 终态」+ 耗时 + 摘要。 */
@Composable
private fun RunTimeline(attempts: List<TaskRunRowDto>) {
    val ordered = TaskRunDisplay.sortedAttempts(attempts)
    Column {
        QuantumSectionHeader(
            title = "执行时间轴",
            subtitle = "共 ${ordered.size} 次尝试（同一根执行，含自动重试）"
        )
        Spacer(modifier = Modifier.height(QuantumPage.CardGap))
        ordered.forEach { attempt ->
            val meta = TaskRunDisplay.statusMeta(attempt.status)
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = QuantumPage.CardGap),
                verticalAlignment = Alignment.Top
            ) {
                TaskRunAttemptDot(
                    attempt = attempt.attempt,
                    color = runToneColor(meta.tone),
                    modifier = Modifier.padding(top = QuantumSpacing.Sm)
                )
                Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                QuantumListCard(modifier = Modifier.weight(1f)) {
                    Column(modifier = Modifier.padding(QuantumSpacing.Md)) {
                        Text(
                            "第 ${attempt.attempt} 次尝试 · ${meta.label}",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurface
                        )
                        Text(
                            text = listOfNotNull(
                                TaskRunDisplay.triggerLabel(attempt.triggerSource),
                                "耗时 ${TaskRunDisplay.formatElapsed(attempt.elapsedMs)}",
                                TaskRunDisplay.formatUtcToLocal(
                                    TaskRunDisplay.startedAtOrCreated(attempt.startedAtUtc, attempt.createdAtUtc),
                                    pattern = "MM-dd HH:mm:ss"
                                )
                            ).joinToString(" · "),
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(top = 2.dp)
                        )
                        Text(
                            TaskRunDisplay.summaryOf(attempt.status, attempt.safeSummary),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(top = QuantumSpacing.Xs)
                        )
                        attempt.cancelReason?.takeIf { it.isNotBlank() }?.let {
                            Text(
                                "中止原因：$it",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.error,
                                modifier = Modifier.padding(top = QuantumSpacing.Xs)
                            )
                        }
                    }
                }
            }
        }
    }
}

/**
 * 日志入口：只按服务端给的 `LogAvailable`/`LogId` 决定显示——
 * 可达才出「查看日志」按钮（跳 App 既有日志详情页），不可达只给占位文案，绝不拼路径。
 */
@Composable
private fun RunLogEntry(logAvailable: Boolean, logId: String?, onOpenLog: (String) -> Unit) {
    QuantumListCard {
        Column(modifier = Modifier.padding(QuantumSpacing.Md)) {
            Text(
                "执行日志",
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            if (logAvailable && !logId.isNullOrBlank()) {
                Text(
                    "日志仍可读，可查看全文并复制。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = QuantumSpacing.Xs)
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                QuantumButton(
                    text = "查看日志",
                    icon = Icons.AutoMirrored.Filled.Article,
                    variant = QuantumButtonVariant.Tonal,
                    size = QuantumButtonSize.Sm,
                    onClick = { onOpenLog(logId) }
                )
            } else {
                Text(
                    "日志已清理或尚未落库，仅保留本条执行记录。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = QuantumSpacing.Xs)
                )
            }
        }
    }
}

/** 「标签：值」行（值缺失统一给「-」，不留空行）。 */
@Composable
private fun TaskRunDetailField(
    label: String,
    value: String,
    modifier: Modifier = Modifier,
    valueColor: Color = MaterialTheme.colorScheme.onSurface,
    multiline: Boolean = false
) {
    Column(modifier = modifier.fillMaxWidth().padding(vertical = QuantumSpacing.Xs)) {
        Text(
            label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Text(
            text = value.ifBlank { "-" },
            style = MaterialTheme.typography.bodyMedium,
            color = valueColor,
            maxLines = if (multiline) Int.MAX_VALUE else 2,
            overflow = if (multiline) TextOverflow.Visible else TextOverflow.Ellipsis,
            modifier = Modifier.padding(top = 2.dp)
        )
    }
}

/** 时间轴节点的序号圆点：按该次尝试的终态色档着色（顺序由数字表达，颜色不表示「越往后越糟」）。 */
@Composable
private fun TaskRunAttemptDot(attempt: Int, color: Color, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .size(26.dp)
            .clip(CircleShape)
            .background(color.copy(alpha = 0.14f))
            .border(1.dp, color.copy(alpha = 0.4f), CircleShape),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = "$attempt",
            style = MaterialTheme.typography.labelSmall,
            fontWeight = FontWeight.Bold,
            color = color
        )
    }
}
