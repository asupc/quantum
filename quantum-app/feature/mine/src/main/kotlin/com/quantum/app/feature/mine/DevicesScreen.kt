package com.quantum.app.feature.mine

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel

/** 设备管理：列表（平台/机型/最后活跃）+ 远程解绑（换机踢除旧设备推送）。 */
@Composable
fun DevicesScreen(viewModel: MineViewModel = hiltViewModel()) {
    val devices by viewModel.devices.collectAsState()

    LaunchedEffect(Unit) { viewModel.loadDeviceList() }

    Column(modifier = Modifier.fillMaxSize().padding(12.dp)) {
        Text("我的设备", style = MaterialTheme.typography.titleMedium)
        Text(
            "解绑后该设备将不再接收厂商推送，下次登录会自动重新绑定",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        LazyColumn(modifier = Modifier.weight(1f)) {
            items(devices, key = { it.id }) { device ->
                Row(
                    modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(device.deviceName ?: device.deviceId, style = MaterialTheme.typography.titleSmall)
                        Text(
                            "${device.platform ?: "android"} · 活跃于 ${device.lastSeenAt}${if (device.enabled) "" else " · 已解绑"}",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    TextButton(onClick = { viewModel.unbindDevice(device.deviceId) }, enabled = device.enabled) {
                        Text("解绑")
                    }
                }
            }
        }
    }
}
