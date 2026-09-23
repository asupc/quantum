package com.quantum.app.feature.notify

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Chat
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Security
import androidx.compose.material.icons.filled.TaskAlt
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import com.quantum.app.core.common.ui.components.QuantumCard
import com.quantum.app.core.common.ui.components.QuantumStatusBadge

/**
 * 通知中心列表：分类 tab（全部/task/system/security）、未读角标、
 * 点按回调深链路由（jump 由 Shell 解析）、单条/全部已读。
 */
@Composable
fun NotifyScreen(
    onOpenJump: (String?) -> Unit,
    viewModel: NotifyViewModel = hiltViewModel()
) {
    val notifications by viewModel.notifications.collectAsState()
    val unread by viewModel.unread.collectAsState()
    val filter by viewModel.filter.collectAsState()

    Column(modifier = Modifier.fillMaxSize()) {
        // 顶部筛选与批量操作栏
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 14.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                listOf("all" to "全部", "task" to "任务", "system" to "系统", "security" to "安全").forEach { (key, label) ->
                    FilterChip(
                        selected = filter == key,
                        onClick = { viewModel.filter(key) },
                        label = { Text(label) },
                        shape = RoundedCornerShape(8.dp),
                        colors = FilterChipDefaults.filterChipColors(
                            selectedContainerColor = MaterialTheme.colorScheme.primaryContainer,
                            selectedLabelColor = MaterialTheme.colorScheme.onPrimaryContainer
                        )
                    )
                }
            }

            TextButton(onClick = { viewModel.markAllRead() }) {
                Icon(Icons.Default.CheckCircle, contentDescription = null, modifier = Modifier.size(16.dp))
                Spacer(modifier = Modifier.width(4.dp))
                Text("全部已读")
            }
        }

        // 通知卡片列表
        val filteredList = notifications.filter { filter == "all" || it.category == filter }
        LazyColumn(
            modifier = Modifier.weight(1f),
            contentPadding = PaddingValues(horizontal = 14.dp, vertical = 6.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            if (filteredList.isEmpty()) {
                item {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(top = 80.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        Icon(
                            Icons.Default.Info,
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f),
                            modifier = Modifier.size(48.dp)
                        )
                        Spacer(modifier = Modifier.height(12.dp))
                        Text(
                            "暂无通知消息",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }

            items(filteredList, key = { it.msgId }) { item ->
                val isUnread = item.readAt == null
                val border = if (isUnread) {
                    BorderStroke(1.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.5f))
                } else null

                val icon = when (item.category) {
                    "task" -> Icons.Default.TaskAlt
                    "security" -> Icons.Default.Security
                    "chat" -> Icons.Default.Chat
                    else -> Icons.Default.Info
                }
                val iconColor = when (item.category) {
                    "task" -> MaterialTheme.colorScheme.primary
                    "security" -> MaterialTheme.colorScheme.error
                    "chat" -> MaterialTheme.colorScheme.secondary
                    else -> MaterialTheme.colorScheme.tertiary
                }

                QuantumCard(
                    onClick = {
                        viewModel.markRead(item)
                        onOpenJump(item.jump)
                    },
                    border = border,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.Top
                    ) {
                        Box(
                            modifier = Modifier
                                .size(36.dp)
                                .clip(RoundedCornerShape(8.dp))
                                .background(iconColor.copy(alpha = 0.15f)),
                            contentAlignment = Alignment.Center
                        ) {
                            Icon(
                                imageVector = icon,
                                contentDescription = null,
                                tint = iconColor,
                                modifier = Modifier.size(20.dp)
                            )
                        }

                        Spacer(modifier = Modifier.width(10.dp))

                        Column(modifier = Modifier.weight(1f)) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.SpaceBetween
                            ) {
                                Text(
                                    item.title,
                                    style = MaterialTheme.typography.titleSmall,
                                    fontWeight = if (isUnread) FontWeight.Bold else FontWeight.Medium,
                                    color = if (isUnread) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant
                                )

                                if (isUnread) {
                                    QuantumStatusBadge(
                                        text = "NEW",
                                        active = true,
                                        activeColor = MaterialTheme.colorScheme.primary
                                    )
                                }
                            }

                            Spacer(modifier = Modifier.height(4.dp))
                            Text(
                                item.content,
                                style = MaterialTheme.typography.bodyMedium,
                                maxLines = 2,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )

                            Spacer(modifier = Modifier.height(6.dp))
                            Text(
                                "${item.createdAt} · ${categoryLabel(item.category)}",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.outline
                            )
                        }
                    }
                }
            }
        }

        // 底部刷新栏
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 14.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            Text(
                if (unread > 0) "未读消息: $unread 条" else "全部已读",
                style = MaterialTheme.typography.bodySmall,
                color = if (unread > 0) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
            )
            TextButton(onClick = { viewModel.refresh() }) {
                Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(16.dp))
                Spacer(modifier = Modifier.width(4.dp))
                Text("刷新")
            }
        }
    }
}

/** 未知 category 按 system 兜底展示（契约 §3.3）。 */
fun categoryLabel(category: String): String = when (category) {
    "task" -> "任务调度"
    "security" -> "安全审计"
    "chat" -> "会话消息"
    else -> "系统通知"
}

