package com.quantum.app.feature.task

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Inbox
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import com.quantum.app.core.common.ui.components.QuantumButton
import com.quantum.app.core.common.ui.components.QuantumButtonSize
import com.quantum.app.core.common.ui.components.QuantumButtonVariant
import com.quantum.app.core.common.ui.components.QuantumCollapsibleTitle
import com.quantum.app.core.common.ui.components.QuantumEmptyState
import com.quantum.app.core.common.ui.components.QuantumIconAction
import com.quantum.app.core.common.ui.components.QuantumListCard
import com.quantum.app.core.common.ui.components.QuantumListRow
import com.quantum.app.core.common.ui.components.QuantumLoadMoreFooter
import com.quantum.app.core.common.ui.components.QuantumLoadMoreTrigger
import com.quantum.app.core.common.ui.components.QuantumLoadingList
import com.quantum.app.core.common.ui.components.QuantumPullRefresh
import com.quantum.app.core.common.ui.components.QuantumTextField
import com.quantum.app.core.common.ui.components.rememberCollapseFraction
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import com.quantum.app.core.network.dto.TaskDto

/**
 * 任务列表（C 档页面级体验）：可收缩大标题 + 下拉刷新 + 骨架/空态/错误态三态
 * + 触底加载更多 + 行增删动画；搜索语义不变。
 * 任务分组已在服务端整体移除，列表为平铺形态（不再按分组粘性标题）。
 *
 * 行语义（2026-09-18 调整；2026-09-20 再调整）：点行 = 打开编辑弹层；行内「执行」单任务直发
 * （多选批量执行已移除——真机反馈多选勾选麻烦，单行执行一步到位），「日志」跳任务日志，
 * 行内开关直接启停；「任务总览」大卡已移除（计数已在标题副行）。
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun TaskScreen(
    onOpenLog: (String) -> Unit,
    viewModel: TaskViewModel = hiltViewModel()
) {
    val page by viewModel.page.collectAsState()
    val loading by viewModel.loading.collectAsState()
    val refreshing by viewModel.refreshing.collectAsState()
    val endReached by viewModel.endReached.collectAsState()
    val error by viewModel.error.collectAsState()
    val toast by viewModel.toast.collectAsState()
    val editing by viewModel.editing.collectAsState()
    var key by remember { mutableStateOf("") }

    val listState = rememberLazyListState()
    val collapse = rememberCollapseFraction(listState)
    val enabledCount = page.items.count { it.enable }

    // 触底加载下一页（加载中/已到底时由组件内部与 VM 双重短路）
    QuantumLoadMoreTrigger(
        state = listState,
        enabled = page.items.isNotEmpty() && !endReached && !loading && !refreshing,
        onLoadMore = { viewModel.loadMore() }
    )

    QuantumPullRefresh(
        refreshing = refreshing,
        onRefresh = { viewModel.refresh() },
        modifier = Modifier.fillMaxSize()
    ) {
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize()
        ) {
            item(key = "title") {
                QuantumCollapsibleTitle(
                    title = "任务",
                    subtitle = if (page.total > 0) {
                        "共 ${page.total} 个 · $enabledCount 个已启用"
                    } else {
                        null
                    },
                    fraction = collapse,
                    actions = {
                        QuantumIconAction(
                            icon = Icons.Filled.Refresh,
                            contentDescription = "刷新",
                            onClick = { viewModel.refresh() }
                        )
                    }
                )
            }
            item(key = "toast") {
                toast?.let {
                    Text(
                        it,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.padding(horizontal = QuantumPage.Padding)
                    )
                }
            }
            item(key = "search") {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = QuantumPage.Padding, vertical = QuantumSpacing.Sm),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    QuantumTextField(
                        value = key,
                        onValueChange = { key = it },
                        modifier = Modifier.weight(1f),
                        placeholder = "搜索任务名称 / 脚本",
                        leadingIcon = Icons.Filled.Search,
                        imeAction = ImeAction.Search,
                        onDone = { viewModel.load(key = key) }
                    )
                    Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                    QuantumButton(
                        text = "搜索",
                        onClick = { viewModel.load(key = key) },
                        variant = QuantumButtonVariant.Tonal,
                        size = QuantumButtonSize.Sm
                    )
                }
            }

            when {
                // 三态：首屏骨架 / 失败重试 / 真空态 / 数据
                loading && page.items.isEmpty() -> item(key = "skeleton") {
                    QuantumLoadingList(rows = 4)
                }

                page.items.isEmpty() && error != null -> item(key = "error") {
                    QuantumEmptyState(
                        icon = Icons.Filled.Warning,
                        title = "加载失败",
                        description = error,
                        actionText = "重试",
                        onAction = { viewModel.refresh() }
                    )
                }

                page.items.isEmpty() -> item(key = "empty") {
                    QuantumEmptyState(
                        icon = Icons.Filled.Inbox,
                        title = "还没有任务",
                        description = "在 Web 管理端创建任务后，这里会显示任务列表与执行入口",
                        actionText = "重新加载",
                        onAction = { viewModel.refresh() }
                    )
                }

                else -> {
                    items(page.items, key = { task -> task.id }) { task ->
                        QuantumListCard(
                            modifier = Modifier
                                .padding(
                                    horizontal = QuantumPage.Padding,
                                    vertical = QuantumSpacing.Xs
                                )
                                .animateItem(),
                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant)
                        ) {
                            QuantumListRow(
                                title = task.name,
                                subtitle = listOfNotNull(
                                    task.fileName?.takeIf { it.isNotBlank() },
                                    task.cron?.takeIf { it.isNotBlank() }
                                ).joinToString(" · ").ifBlank { null },
                                trailing = {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        // 单任务执行（多选批量已移除）：结果经任务通知推送
                                        TextButton(onClick = { viewModel.exec(listOf(task.id)) }) { Text("执行") }
                                        // 任务日志：按任务名过滤日志中心（路由 task/{id}/log）
                                        TextButton(onClick = { onOpenLog(task.id) }) { Text("日志") }
                                        // 启停：直接切换并原位刷新该行（不再整页重载）
                                        Switch(
                                            checked = task.enable,
                                            onCheckedChange = { viewModel.toggleEnable(task) }
                                        )
                                    }
                                },
                                onClick = { viewModel.openEditor(task) }
                            )
                        }
                    }

                    item(key = "loadmore") {
                        QuantumLoadMoreFooter(loading = loading, endReached = endReached)
                    }
                }
            }

            item(key = "bottom-gap") {
                Spacer(modifier = Modifier.height(QuantumPage.GroupGap))
            }
        }
    }

    // 编辑弹层：点行打开；「查看日志」转去 task/{id}/log（本页不重建，列表不重载）
    editing?.let { task ->
        TaskEditorDialog(
            task = task,
            onDismiss = { viewModel.closeEditor() },
            onOpenLog = {
                viewModel.closeEditor()
                onOpenLog(task.id)
            },
            onSave = { viewModel.saveTask(it) }
        )
    }
}

/**
 * 任务编辑弹层（对齐 Web 管理端编辑窗的可改字段）：名称 / 脚本 / 触发指令(+正则) /
 * 指令变量 / 会话名 / 定时 / 备注 + 启用·推送开关（「走代理」开关已随平台移除代理支持而删除，2026-09-20）。
 * 其余字段（子任务、推送分组、等待时间、起止通知文本、通讯方式…）随列表行原样回带，
 * 不在这里暴露但也不会被覆盖清零。
 */
@Composable
private fun TaskEditorDialog(
    task: TaskDto,
    onDismiss: () -> Unit,
    onOpenLog: () -> Unit,
    onSave: (TaskDto) -> Unit
) {
    var name by remember { mutableStateOf(task.name) }
    var fileName by remember { mutableStateOf(task.fileName.orEmpty()) }
    var command by remember { mutableStateOf(task.command.orEmpty()) }
    var commandEnv by remember { mutableStateOf(task.commandEnv.orEmpty()) }
    var sessionName by remember { mutableStateOf(task.sessionName.orEmpty()) }
    var cron by remember { mutableStateOf(task.cron.orEmpty()) }
    var remark by remember { mutableStateOf(task.remark.orEmpty()) }
    var enableRegex by remember { mutableStateOf(task.enableRegex) }
    var enable by remember { mutableStateOf(task.enable) }
    var enablePush by remember { mutableStateOf(task.enablePush) }
    var nameError by remember { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("编辑任务") },
        text = {
            Column(
                modifier = Modifier
                    .heightIn(max = 480.dp)
                    .verticalScroll(rememberScrollState())
            ) {
                QuantumTextField(
                    value = name,
                    onValueChange = {
                        name = it
                        nameError = null
                    },
                    label = "任务名称",
                    error = nameError
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                QuantumTextField(
                    value = fileName,
                    onValueChange = { fileName = it },
                    label = "执行脚本",
                    placeholder = "如 music_search.cs",
                    helper = "需位于服务端 scripts/quantum 目录，扩展名 .cs"
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                QuantumTextField(
                    value = command,
                    onValueChange = { command = it },
                    label = "触发指令",
                    placeholder = "支持正则（在下方开启）"
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Switch(checked = enableRegex, onCheckedChange = { enableRegex = it })
                    Spacer(modifier = Modifier.width(8.dp))
                    Text("触发指令按正则匹配", style = MaterialTheme.typography.bodyMedium)
                }
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                QuantumTextField(
                    value = commandEnv,
                    onValueChange = { commandEnv = it },
                    label = "指令变量",
                    placeholder = "把用户消息赋值到该环境变量"
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                QuantumTextField(
                    value = sessionName,
                    onValueChange = { sessionName = it },
                    label = "会话名",
                    placeholder = "留空不归组",
                    helper = "相同会话名的任务在会话页合并为一个会话"
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                QuantumTextField(
                    value = cron,
                    onValueChange = { cron = it },
                    label = "定时执行",
                    placeholder = "Quartz Cron，如 0 0/30 * * * ?",
                    helper = "留空 = 不定时"
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                QuantumTextField(
                    value = remark,
                    onValueChange = { remark = it },
                    label = "备注",
                    singleLine = false,
                    minLines = 2,
                    maxLines = 3,
                    imeAction = ImeAction.Default
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                SwitchRow("启用任务", enable) { enable = it }
                SwitchRow("消息推送", enablePush) { enablePush = it }
            }
        },
        confirmButton = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                QuantumButton(
                    text = "取消",
                    variant = QuantumButtonVariant.Ghost,
                    size = QuantumButtonSize.Sm,
                    onClick = onDismiss
                )
                Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                QuantumButton(
                    text = "保存",
                    size = QuantumButtonSize.Sm,
                    onClick = {
                        nameError = if (name.isBlank()) "任务名称不能为空" else null
                        if (nameError == null) {
                            onSave(
                                task.copy(
                                    name = name.trim(),
                                    fileName = fileName.trim(),
                                    command = command.trim().ifBlank { null },
                                    commandEnv = commandEnv.trim().ifBlank { null },
                                    sessionName = sessionName.trim().ifBlank { null },
                                    cron = cron.trim().ifBlank { null },
                                    remark = remark.trim().ifBlank { null },
                                    enableRegex = enableRegex,
                                    enable = enable,
                                    enablePush = enablePush
                                    // enableProxy 不在编辑面暴露：copy 缺省沿用行内原值回带
                                )
                            )
                        }
                    }
                )
            }
        },
        dismissButton = {
            QuantumButton(
                text = "查看日志",
                variant = QuantumButtonVariant.Tonal,
                size = QuantumButtonSize.Sm,
                onClick = onOpenLog
            )
        }
    )
}

/** 编辑弹层里的开关行（标签 + Switch）。 */
@Composable
private fun SwitchRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(label, style = MaterialTheme.typography.bodyMedium)
        Switch(checked = checked, onCheckedChange = onChange)
    }
}
