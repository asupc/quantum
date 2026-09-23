package com.quantum.app.feature.mine

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Fingerprint
import androidx.compose.material.icons.filled.Logout
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Security
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TimePicker
import androidx.compose.material3.rememberTimePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import com.quantum.app.core.common.DndWindow
import com.quantum.app.core.common.ui.components.QuantumButton
import com.quantum.app.core.common.ui.components.QuantumButtonSize
import com.quantum.app.core.common.ui.components.QuantumButtonVariant
import com.quantum.app.core.common.ui.components.QuantumCard
import com.quantum.app.core.common.ui.components.QuantumStatusBadge

/**
 * 运行状态自检卡片：
 * - 消息通道（WS）：已连接=实时帧可达；未连接=实时推送断（消息只能靠切换页面触发 REST 补拉）；
 * - 系统通知权限：后台弹本地通知的硬前提（Android 13+ 默认拒绝）；启动时的自动申请被系统
 *   静默拒绝（二次拒后不再弹窗）时，可在此重新申请或跳转系统设置手动开启。
 */
@Composable
private fun RunSelfCheckCard(viewModel: MineViewModel) {
    val wsState by viewModel.wsState.collectAsState()
    val context = LocalContext.current
    var notifGranted by remember { mutableStateOf<Boolean?>(null) }
    val permLauncher = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestPermission()
    ) { granted ->
        notifGranted = granted || androidx.core.app.NotificationManagerCompat.from(context).areNotificationsEnabled()
    }
    LaunchedEffect(Unit) {
        notifGranted = androidx.core.app.NotificationManagerCompat.from(context).areNotificationsEnabled()
    }

    QuantumCard(modifier = Modifier.fillMaxWidth()) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth()
        ) {
            Icon(
                Icons.Default.Notifications,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(20.dp)
            )
            Spacer(modifier = Modifier.width(8.dp))
            Text("运行状态自检", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
        }

        Spacer(modifier = Modifier.height(10.dp))

        // 消息通道状态
        Row(
            modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Column {
                Text("消息通道（WS）", style = MaterialTheme.typography.bodyMedium)
                Text(
                    when (wsState) {
                        com.quantum.app.core.network.ws.WsState.Open -> "实时消息推送在线"
                        com.quantum.app.core.network.ws.WsState.Connecting -> "正在建立连接…"
                        else -> "未连接：新消息将不会实时到达"
                    },
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            QuantumStatusBadge(
                text = when (wsState) {
                    com.quantum.app.core.network.ws.WsState.Open -> "已连接"
                    com.quantum.app.core.network.ws.WsState.Connecting -> "连接中"
                    else -> "未连接"
                },
                active = wsState == com.quantum.app.core.network.ws.WsState.Open
            )
        }

        // 系统通知权限状态与操作
        Row(
            modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text("系统通知权限", style = MaterialTheme.typography.bodyMedium)
                Text(
                    if (notifGranted == true) "已开启：后台可弹出本地通知"
                    else "未开启：后台通知将被系统拦截",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            Spacer(modifier = Modifier.width(8.dp))
            if (notifGranted == false) {
                OutlinedButton(
                    onClick = {
                        if (android.os.Build.VERSION.SDK_INT >= 33 && androidx.core.content.ContextCompat.checkSelfPermission(
                                context, android.Manifest.permission.POST_NOTIFICATIONS
                            ) != android.content.pm.PackageManager.PERMISSION_GRANTED
                        ) {
                            permLauncher.launch(android.Manifest.permission.POST_NOTIFICATIONS)
                        } else {
                            // 已被系统永久拒绝（不再弹窗）或渠道被关：直接跳应用通知设置页
                            runCatching {
                                context.startActivity(
                                    android.content.Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                                        .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, context.packageName)
                                )
                            }
                        }
                    },
                    shape = RoundedCornerShape(8.dp)
                ) {
                    Text("开启通知")
                }
            } else {
                QuantumStatusBadge(text = if (notifGranted == true) "已开启" else "检测中…", active = notifGranted == true)
            }
        }
    }
}

/**
 * 我的页：设备管理、扫码授权（摄像头扫码登录）、通知偏好（三类开关+免打扰）、
 * 个人信息（单管理员；改密在 Web 管理端）、生物识别门禁开关、登出。
 * 版本检查/端内自更新已整体移除（升级改为手动安装新 APK），仅保留只读版本号展示。
 */
@Composable
fun MineScreen(
    installedVersionCode: Int,
    onOpenDevices: () -> Unit,
    onOpenScan: () -> Unit,
    viewModel: MineViewModel = hiltViewModel()
) {
    val userName by viewModel.userName.collectAsState()
    val notifyPrefs by viewModel.notifyPrefs.collectAsState()
    val biometricLock by viewModel.biometricLock.collectAsState()
    val toast by viewModel.toast.collectAsState()
    var prefsDraft by remember { mutableStateOf<com.quantum.app.core.network.dto.NotifySettingDto?>(null) }
    // 免打扰时间选择弹层目标："start" / "end"，null = 不显示
    var dndPicker by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) {
        viewModel.loadNotifyPrefs()
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        // 用户概览卡片
        QuantumCard(modifier = Modifier.fillMaxWidth()) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(48.dp)
                        .clip(CircleShape)
                        .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.15f)),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        Icons.Default.Person,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(26.dp)
                    )
                }

                Spacer(modifier = Modifier.width(12.dp))

                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        userName ?: "已登录用户",
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.Bold
                    )
                    Spacer(modifier = Modifier.height(2.dp))
                    Text(
                        "客户端版本: v$installedVersionCode",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }

                QuantumStatusBadge(
                    text = "管理员",
                    active = true,
                    activeColor = MaterialTheme.colorScheme.secondary
                )
            }
        }

        // 运行状态自检：消息通道（WS）与系统通知权限是消息实时到达/后台弹通知的两个硬前提，
        // 出问题时在此一目了然并可就地修复（申请权限或跳系统设置）。
        RunSelfCheckCard(viewModel)

        // 离线推送偏好卡片
        QuantumCard(modifier = Modifier.fillMaxWidth()) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Icon(
                    Icons.Default.Notifications,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(20.dp)
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text("通知与推送偏好", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
            }

            Spacer(modifier = Modifier.height(10.dp))

            val draft = prefsDraft ?: notifyPrefs
            listOf(
                "任务执行结果通知" to draft.taskPush to { v: Boolean -> prefsDraft = draft.copy(taskPush = v) },
                "系统事件与审计通知" to draft.systemPush to { v: Boolean -> prefsDraft = draft.copy(systemPush = v) },
                "安全敏感操作提醒" to draft.securityPush to { v: Boolean -> prefsDraft = draft.copy(securityPush = v) }
            ).forEach { (labelAndValue, setter) ->
                val (label, value) = labelAndValue
                Row(
                    modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Text(label, style = MaterialTheme.typography.bodyMedium)
                    Switch(
                        checked = value,
                        onCheckedChange = setter,
                        colors = SwitchDefaults.colors(
                            checkedThumbColor = MaterialTheme.colorScheme.onPrimary,
                            checkedTrackColor = MaterialTheme.colorScheme.primary
                        )
                    )
                }
            }

            Spacer(modifier = Modifier.height(8.dp))

            // 免打扰时段：点击弹时间选择器（此前是自由文本 HH:mm，输成 "9:00" 被服务端拒绝、
            // 界面还乐观显示"已保存"，重进页面就成了"数据丢失"）
            Row(
                modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("免打扰时段", style = MaterialTheme.typography.bodyMedium)
                    Text(
                        if (draft.dndStart == null || draft.dndEnd == null) {
                            "未设置：全天正常提醒"
                        } else {
                            "该时段内不弹系统通知（消息照常进会话与通知中心）"
                        },
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
            Row(
                modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                OutlinedButton(
                    onClick = { dndPicker = "start" },
                    modifier = Modifier.weight(1f),
                    shape = RoundedCornerShape(8.dp)
                ) {
                    Text(draft.dndStart?.let { "起 $it" } ?: "设置开始")
                }
                OutlinedButton(
                    onClick = { dndPicker = "end" },
                    modifier = Modifier.weight(1f),
                    shape = RoundedCornerShape(8.dp)
                ) {
                    Text(draft.dndEnd?.let { "止 $it" } ?: "设置结束")
                }
            }
            if (draft.dndStart != null || draft.dndEnd != null) {
                TextButton(onClick = { prefsDraft = draft.copy(dndStart = null, dndEnd = null) }) {
                    Text("清除免打扰时段")
                }
            }

            Spacer(modifier = Modifier.height(10.dp))

            Button(
                onClick = { viewModel.saveNotifyPrefs(draft) },
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(8.dp)
            ) {
                Text("保存通知偏好")
            }
        }

        // 安全与设备管理卡片
        QuantumCard(modifier = Modifier.fillMaxWidth()) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Icon(
                    Icons.Default.Security,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(20.dp)
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text("安全与设备", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
            }

            Spacer(modifier = Modifier.height(8.dp))

            Row(
                modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(
                        Icons.Default.Fingerprint,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.secondary,
                        modifier = Modifier.size(22.dp)
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    Column {
                        Text("生物识别门禁", style = MaterialTheme.typography.bodyMedium)
                        Text(
                            "冷启动或切回前台验证指纹/面容",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
                Switch(
                    checked = biometricLock,
                    onCheckedChange = { viewModel.setBiometricLock(it) },
                    colors = SwitchDefaults.colors(
                        checkedThumbColor = MaterialTheme.colorScheme.onPrimary,
                        checkedTrackColor = MaterialTheme.colorScheme.primary
                    )
                )
            }

            Spacer(modifier = Modifier.height(6.dp))

            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(
                    onClick = onOpenDevices,
                    modifier = Modifier.weight(1f),
                    shape = RoundedCornerShape(8.dp)
                ) {
                    Icon(Icons.Default.Devices, contentDescription = null, modifier = Modifier.size(16.dp))
                    Spacer(modifier = Modifier.width(6.dp))
                    Text("设备管理")
                }

                OutlinedButton(
                    onClick = onOpenScan,
                    modifier = Modifier.weight(1f),
                    shape = RoundedCornerShape(8.dp)
                ) {
                    Icon(Icons.Default.QrCodeScanner, contentDescription = null, modifier = Modifier.size(16.dp))
                    Spacer(modifier = Modifier.width(6.dp))
                    Text("扫码授权")
                }
            }

            Spacer(modifier = Modifier.height(8.dp))
            Text(
                "修改登录密码：请在 Web 管理端「修改密码」操作，改密后所有登录态将失效需重新登录。",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        // 登出操作区
        Button(
            onClick = { viewModel.logout() },
            modifier = Modifier.fillMaxWidth().height(44.dp),
            shape = RoundedCornerShape(10.dp),
            colors = ButtonDefaults.buttonColors(
                containerColor = MaterialTheme.colorScheme.errorContainer.copy(alpha = 0.5f),
                contentColor = MaterialTheme.colorScheme.error
            )
        ) {
            Icon(Icons.Default.Logout, contentDescription = null, modifier = Modifier.size(18.dp))
            Spacer(modifier = Modifier.width(8.dp))
            Text("退出当前登录账号", fontWeight = FontWeight.SemiBold)
        }

        // 操作反馈（保存偏好/解绑设备等）
        toast?.let {
            Text(
                it,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(horizontal = 4.dp)
            )
        }

        Spacer(modifier = Modifier.height(16.dp))
    }

    // 免打扰时间选择：确定后仅改草稿，仍需点「保存通知偏好」提交（与三个开关同一套语义）
    val pickerDraft = prefsDraft ?: notifyPrefs
    dndPicker?.let { which ->
        DndTimePickerDialog(
            title = if (which == "start") "免打扰开始时间" else "免打扰结束时间",
            initial = if (which == "start") pickerDraft.dndStart else pickerDraft.dndEnd,
            onDismiss = { dndPicker = null },
            onConfirm = { hm ->
                prefsDraft = if (which == "start") {
                    pickerDraft.copy(dndStart = hm)
                } else {
                    pickerDraft.copy(dndEnd = hm)
                }
                dndPicker = null
            }
        )
    }
}

/** 免打扰时间选择弹层：只产出规范的 HH:mm，杜绝手输格式问题（服务端按 HH:mm 严格校验）。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun DndTimePickerDialog(
    title: String,
    initial: String?,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit
) {
    val seed = DndWindow.parse(initial) ?: java.time.LocalTime.of(22, 0)
    val state = rememberTimePickerState(
        initialHour = seed.hour,
        initialMinute = seed.minute,
        is24Hour = true
    )
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Box(modifier = Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                TimePicker(state = state)
            }
        },
        confirmButton = {
            QuantumButton(
                text = "确定",
                size = QuantumButtonSize.Sm,
                onClick = {
                    onConfirm(DndWindow.format(java.time.LocalTime.of(state.hour, state.minute)))
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

