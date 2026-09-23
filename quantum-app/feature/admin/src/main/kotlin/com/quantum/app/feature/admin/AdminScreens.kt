package com.quantum.app.feature.admin

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.Save
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import com.quantum.app.core.common.ui.components.ConfirmDialog
import com.quantum.app.core.common.ui.components.QuantumBackHeader
import com.quantum.app.core.common.ui.components.QuantumButton
import com.quantum.app.core.common.ui.components.QuantumButtonSize
import com.quantum.app.core.common.ui.components.QuantumButtonVariant
import com.quantum.app.core.common.ui.components.QuantumEmptyState
import com.quantum.app.core.common.ui.components.QuantumIconAction
import com.quantum.app.core.common.ui.components.QuantumLoadingList
import com.quantum.app.core.common.ui.components.QuantumStatusBadge
import com.quantum.app.core.common.ui.components.QuantumTextField
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import com.quantum.app.core.network.dto.EnvDto
import com.quantum.app.core.network.dto.EnvResultDto
import com.quantum.app.core.network.dto.SystemSettingDto

/**
 * 管理面页面集：环境变量 / 系统设置。
 * 单管理员体系（用户管理已移除），每页独立入口，写操作服务端兜底。
 * 2026-09-20：两页均为管理子页——顶部统一返回头、底部导航隐藏；
 * 系统设置字段集与 Web 端对齐（安全三件套补齐——此前 App 每次保存会把这三项清掉，见 DTO 注释）。
 */

@Composable
fun EnvScreen(onBack: () -> Unit, viewModel: AdminViewModel = hiltViewModel()) {
    val envs by viewModel.envs.collectAsState()
    val toast by viewModel.toast.collectAsState()
    // 编辑弹层：null=新增（+ 号入口），非 null=编辑该变量
    var editing by remember { mutableStateOf<EnvResultDto?>(null) }
    var showEditor by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) { viewModel.loadEnvs() }
    // 操作反馈限时展示（此前 toast 写入 VM 却无人渲染，失败完全静默）
    LaunchedEffect(toast) {
        if (toast != null) {
            kotlinx.coroutines.delay(4_000)
            viewModel.consumeToast()
        }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        QuantumBackHeader(
            title = "环境变量",
            subtitle = "共 ${envs.size} 个 · 供任务脚本注入使用",
            onBack = onBack,
            actions = {
                // 新增入口：页面右上 + 号（列表页不再放输入框）
                QuantumIconAction(
                    icon = Icons.Filled.Add,
                    contentDescription = "新增变量",
                    onClick = {
                        editing = null
                        showEditor = true
                    }
                )
            }
        )

        toast?.let {
            Text(
                it,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(horizontal = QuantumPage.Padding, vertical = QuantumSpacing.Xs)
            )
        }

        if (envs.isEmpty()) {
            QuantumEmptyState(
                icon = Icons.Filled.Code,
                title = "还没有环境变量",
                description = "点右上角 + 添加，或从 Web 管理端导入",
                actionText = "重新加载",
                onAction = { viewModel.loadEnvs() },
                modifier = Modifier.weight(1f)
            )
        } else {
            LazyColumn(
                modifier = Modifier.weight(1f),
                contentPadding = PaddingValues(horizontal = QuantumPage.Padding)
            ) {
                items(envs, key = { it.id }) { env ->
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(env.name, style = MaterialTheme.typography.titleSmall)
                                Spacer(modifier = Modifier.width(6.dp))
                                QuantumStatusBadge(
                                    text = if (env.enable) "启用" else "停用",
                                    active = env.enable
                                )
                            }
                            Text(
                                env.value.orEmpty().ifBlank { "（空值）" },
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 2
                            )
                            env.remark?.takeIf { it.isNotBlank() }?.let {
                                Text(
                                    it,
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        }
                        // 三个操作按钮紧凑排列（收窄内边距 + 小号字，2026-09-20）
                        EnvActionButton("编辑") {
                            editing = env
                            showEditor = true
                        }
                        EnvActionButton(if (env.enable) "停用" else "启用") { viewModel.toggleEnv(env) }
                        EnvActionButton("删除") { viewModel.deleteEnv(env.id) }
                    }
                }
            }
        }
    }

    if (showEditor) {
        EnvEditorDialog(
            env = editing,
            onDismiss = { showEditor = false },
            onSave = { name, value, remark ->
                val target = editing
                viewModel.saveEnv(
                    EnvDto(
                        id = target?.id,
                        name = name,
                        value = value,
                        // 新增恒启用；编辑保持原启用状态（省略 Enable 会被服务端当 false 存）
                        enable = target?.enable ?: true,
                        remark = remark
                    ),
                    isNew = target == null
                )
                showEditor = false
            }
        )
    }
}

/** 环境变量行内操作按钮：比默认 TextButton 紧凑（内边距 6dp + 小号字）。 */
@Composable
private fun EnvActionButton(text: String, onClick: () -> Unit) {
    TextButton(
        onClick = onClick,
        contentPadding = PaddingValues(horizontal = 6.dp),
        modifier = Modifier.heightIn(min = 36.dp)
    ) {
        Text(text, style = MaterialTheme.typography.labelMedium)
    }
}

/** 环境变量新增/编辑弹层：名称走服务端同款校验（字母开头，字母数字下划线，2-64 位）。 */
@Composable
private fun EnvEditorDialog(
    env: EnvResultDto?,
    onDismiss: () -> Unit,
    onSave: (name: String, value: String, remark: String) -> Unit
) {
    var name by remember { mutableStateOf(env?.name.orEmpty()) }
    var value by remember { mutableStateOf(env?.value.orEmpty()) }
    var remark by remember { mutableStateOf(env?.remark.orEmpty()) }
    var nameError by remember { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (env == null) "新增环境变量" else "编辑环境变量") },
        text = {
            Column(modifier = Modifier.verticalScroll(rememberScrollState())) {
                QuantumTextField(
                    value = name,
                    onValueChange = {
                        name = it
                        nameError = null
                    },
                    label = "名称",
                    placeholder = "如 API_TOKEN",
                    error = nameError,
                    helper = if (nameError == null) "字母开头，字母/数字/下划线，2-64 位" else null
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                QuantumTextField(
                    value = value,
                    onValueChange = { value = it },
                    label = "值",
                    placeholder = "变量值",
                    singleLine = false,
                    minLines = 2,
                    maxLines = 4,
                    imeAction = ImeAction.Default
                )
                Spacer(modifier = Modifier.height(QuantumSpacing.Sm))
                QuantumTextField(
                    value = remark,
                    onValueChange = { remark = it },
                    label = "备注（可选）"
                )
            }
        },
        confirmButton = {
            QuantumButton(
                text = "保存",
                size = QuantumButtonSize.Sm,
                onClick = {
                    val trimmed = name.trim()
                    nameError = when {
                        trimmed.isBlank() -> "名称不能为空"
                        !Regex("^[a-zA-Z][a-zA-Z0-9_]{1,64}$").matches(trimmed) ->
                            "名称只能用字母/数字/下划线，且以字母开头（2-64 位）"
                        else -> null
                    }
                    if (nameError == null) {
                        onSave(trimmed, value, remark.trim())
                    }
                }
            )
        },
        dismissButton = {
            QuantumButton(
                text = "取消",
                variant = QuantumButtonVariant.Ghost,
                size = QuantumButtonSize.Sm,
                onClick = onDismiss
            )
        }
    )
}

/**
 * 系统设置页（字段集与 Web 端 setting.vue 对齐，2026-09-20）：
 * 安全配置（Open AppKey/可信代理/跨域白名单/Swagger）+ 消息与推送（服务地址/两个间隔/黑名单/登录提醒）
 * + 站点与数据（自定义页脚/收缩数据库）。管理员账号密码不在本页（Web 右上角改密维护）。
 */
@Composable
fun SystemSettingScreen(onBack: () -> Unit, viewModel: AdminViewModel = hiltViewModel()) {
    val setting by viewModel.setting.collectAsState()
    val saving by viewModel.saving.collectAsState()
    val shrinking by viewModel.shrinking.collectAsState()
    val toast by viewModel.toast.collectAsState()
    var draft by remember { mutableStateOf<SystemSettingDto?>(null) }
    // 数字字段用文本暂存（服务端为 Int；保存时统一解析，非法给行内错误）
    var messageIntervalText by remember { mutableStateOf("") }
    var queueIntervalText by remember { mutableStateOf("") }
    var numberError by remember { mutableStateOf<String?>(null) }
    var confirmShrink by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        viewModel.loadSetting()
    }
    LaunchedEffect(setting) {
        // 载入/保存后重拉时同步草稿（含 AppKey 掩码回显）
        setting?.let {
            draft = it
            messageIntervalText = it.messageInterval.toString()
            queueIntervalText = it.messageQueueInterval.toString()
        }
    }

    val current = draft
    if (current == null) {
        // 加载中不再返回空白屏：给骨架占位
        Column(modifier = Modifier.fillMaxSize()) {
            QuantumBackHeader(title = "系统设置", onBack = onBack)
            QuantumLoadingList(rows = 3, modifier = Modifier.weight(1f))
        }
        return
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(QuantumSpacing.Sm)
    ) {
        QuantumBackHeader(title = "系统设置", subtitle = "平台运行参数（管理员）", onBack = onBack)

        toast?.let {
            Text(
                it,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(horizontal = QuantumPage.Padding)
            )
        }

        Column(
            modifier = Modifier.padding(horizontal = QuantumPage.Padding),
            verticalArrangement = Arrangement.spacedBy(QuantumSpacing.Sm)
        ) {
            SettingSectionTitle("安全配置")
            QuantumTextField(
                value = current.appKey.orEmpty(),
                onValueChange = { draft = current.copy(appKey = it) },
                label = "Open AppKey",
                placeholder = "已配置时显示掩码，留空/掩码保存不覆盖",
                isPassword = true,
                helper = "Open API / 外触内执的鉴权凭据，权限极高，请勿泄露"
            )
            QuantumTextField(
                value = current.knownProxies.orEmpty(),
                onValueChange = { draft = current.copy(knownProxies = it) },
                label = "可信反向代理",
                placeholder = "如 172.17.0.1,172.16.0.0/12",
                helper = "逗号分隔 IP/CIDR；仅这些直连方的 X-Forwarded-For 被采信，留空不信任任何 XFF（重启生效）"
            )
            QuantumTextField(
                value = current.allowedOrigins.orEmpty(),
                onValueChange = { draft = current.copy(allowedOrigins = it) },
                label = "跨域白名单",
                placeholder = "逗号分隔 Origin，留空拒绝一切跨域",
                helper = "允许跨域调用 API 的来源（重启生效）"
            )
            SettingSwitchRow("生产环境暴露 Swagger", current.enableSwagger) {
                draft = current.copy(enableSwagger = it)
            }

            SettingSectionTitle("消息与推送")
            QuantumTextField(
                value = current.serverPath.orEmpty(),
                onValueChange = { draft = current.copy(serverPath = it) },
                label = "服务地址",
                placeholder = "如 http://118.8.8.8:5088"
            )
            QuantumTextField(
                value = messageIntervalText,
                onValueChange = {
                    messageIntervalText = it
                    numberError = null
                },
                label = "消息发送间隔",
                placeholder = "如 5 = 每 0.5 秒一条",
                keyboardType = KeyboardType.Number,
                error = numberError
            )
            QuantumTextField(
                value = queueIntervalText,
                onValueChange = {
                    queueIntervalText = it
                    numberError = null
                },
                label = "队列处理间隔",
                placeholder = "毫秒，默认 100",
                keyboardType = KeyboardType.Number,
                error = numberError
            )
            QuantumTextField(
                value = current.blackQQ.orEmpty(),
                onValueChange = { draft = current.copy(blackQQ = it) },
                label = "黑名单用户",
                placeholder = "用户 ID，多个用逗号隔开",
                helper = "名单内用户发来的指令消息将被忽略"
            )
            SettingSwitchRow("管理员登录时推送提醒", current.loginNotify) {
                draft = current.copy(loginNotify = it)
            }

            SettingSectionTitle("站点与数据")
            QuantumTextField(
                value = current.footer.orEmpty(),
                onValueChange = { draft = current.copy(footer = it) },
                label = "自定义页脚",
                placeholder = "展示在登录页面页脚部分",
                singleLine = false,
                minLines = 3,
                maxLines = 5,
                imeAction = ImeAction.Default
            )

            Row(
                modifier = Modifier.fillMaxWidth().padding(top = QuantumSpacing.Xs),
                horizontalArrangement = Arrangement.spacedBy(QuantumSpacing.Sm)
            ) {
                QuantumButton(
                    text = "收缩数据库",
                    variant = QuantumButtonVariant.Outlined,
                    size = QuantumButtonSize.Sm,
                    loading = shrinking,
                    onClick = { confirmShrink = true },
                    modifier = Modifier.weight(1f)
                )
                QuantumButton(
                    text = "保存设置",
                    size = QuantumButtonSize.Sm,
                    loading = saving,
                    icon = Icons.Filled.Save,
                    onClick = {
                        val interval = messageIntervalText.trim().toIntOrNull()
                        val queue = queueIntervalText.trim().toIntOrNull()
                        numberError = when {
                            interval == null -> "消息发送间隔需为数字"
                            queue == null -> "队列处理间隔需为数字"
                            else -> null
                        }
                        if (interval != null && queue != null) {
                            viewModel.saveSetting(
                                current.copy(messageInterval = interval, messageQueueInterval = queue)
                            )
                        }
                    },
                    modifier = Modifier.weight(1f)
                )
            }
            Spacer(modifier = Modifier.height(QuantumPage.GroupGap))
        }
    }

    if (confirmShrink) {
        ConfirmDialog(
            title = "收缩数据库文件",
            content = "数据频繁增删会让数据库文件膨胀影响性能，建议定期收缩（SQLite VACUUM / MySQL OPTIMIZE）。确定执行？",
            onConfirm = {
                confirmShrink = false
                viewModel.shrinkDatabase()
            },
            onDismiss = { confirmShrink = false }
        )
    }
}

/** 系统设置分组标题（对齐 Web 端 n-divider 分组形态）。 */
@Composable
private fun SettingSectionTitle(title: String) {
    Text(
        title,
        style = MaterialTheme.typography.titleSmall,
        fontWeight = FontWeight.Bold,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(top = QuantumSpacing.Sm)
    )
}

/** 系统设置开关行（标签 + Switch，右对齐）。 */
@Composable
private fun SettingSwitchRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(label, style = MaterialTheme.typography.bodyMedium)
        Switch(checked = checked, onCheckedChange = onChange)
    }
}
