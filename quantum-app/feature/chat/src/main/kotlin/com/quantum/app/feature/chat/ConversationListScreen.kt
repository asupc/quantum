package com.quantum.app.feature.chat

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Chat
import androidx.compose.material.icons.filled.TaskAlt
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import com.quantum.app.feature.chat.ConversationListViewModel.ConversationUi

/**
 * 会话列表页（默认首页）：按脚本分会话，最后消息时间倒序（空会话垫底）；
 * 行 = 会话图标/标题 + 最后消息预览 + 时间 + 未读角标；点按进入会话详情（底部导航隐藏），
 * 长按弹「删除会话」确认（删除后任务再推送时自动重建）。
 */
@Composable
fun ConversationListScreen(
    onOpenSession: (String) -> Unit,
    viewModel: ConversationListViewModel = hiltViewModel()
) {
    val conversations by viewModel.conversations.collectAsState()
    // 长按待删除的会话（非空时弹确认对话框）
    var pendingDelete by remember { mutableStateOf<ConversationUi?>(null) }

    Column(modifier = Modifier.fillMaxSize()) {
        // 页头
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                "会话",
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.Bold
            )
            Spacer(modifier = Modifier.width(8.dp))
            Text(
                "${conversations.size} 个",
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        if (conversations.isEmpty()) {
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(
                    "暂无会话消息",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        } else {
            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 10.dp, vertical = 4.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp)
            ) {
                items(conversations, key = { it.sessionId }) { conversation ->
                    ConversationRow(
                        conversation,
                        onClick = { onOpenSession(conversation.sessionId) },
                        onLongClick = { pendingDelete = conversation }
                    )
                }
            }
        }
    }

    // 删除会话确认（2026-09-21 日志删除逻辑调整）：只删会话数据与本窗口显示，
    // 服务器消息记录保留（删除消息记录请前往日志中心），任务再次推送时自动重建
    pendingDelete?.let { target ->
        AlertDialog(
            onDismissRequest = { pendingDelete = null },
            title = { Text("删除会话") },
            text = {
                Text("将删除「${target.title}」会话与窗口显示，消息记录保留在服务器，任务再次推送时自动重建。")
            },
            confirmButton = {
                TextButton(onClick = {
                    pendingDelete = null
                    viewModel.deleteSession(target.sessionId)
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = {
                TextButton(onClick = { pendingDelete = null }) { Text("取消") }
            }
        )
    }
}

/** 会话行：默认会话用对话图标、任务会话用任务图标（对齐底部导航的语言）；空会话无预览/时间。 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ConversationRow(
    conversation: ConversationUi,
    onClick: () -> Unit,
    onLongClick: () -> Unit
) {
    val scheme = MaterialTheme.colorScheme
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .combinedClickable(onClick = onClick, onLongClick = onLongClick)
            .padding(horizontal = 10.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .size(44.dp)
                .clip(CircleShape)
                .background(if (conversation.sessionId.isEmpty()) scheme.primary.copy(alpha = 0.15f) else scheme.secondary.copy(alpha = 0.15f)),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                if (conversation.sessionId.isEmpty()) Icons.AutoMirrored.Filled.Chat else Icons.Filled.TaskAlt,
                contentDescription = null,
                tint = if (conversation.sessionId.isEmpty()) scheme.primary else scheme.secondary,
                modifier = Modifier.size(22.dp)
            )
        }

        Spacer(modifier = Modifier.width(12.dp))

        Column(modifier = Modifier.weight(1f)) {
            Text(
                conversation.title,
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            // 空会话（清空后保留）无预览：只显示会话名
            conversation.preview?.let {
                Spacer(modifier = Modifier.height(2.dp))
                Text(
                    it,
                    style = MaterialTheme.typography.bodySmall,
                    color = scheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }

        Spacer(modifier = Modifier.width(8.dp))

        Column(horizontalAlignment = Alignment.End) {
            conversation.time?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.labelSmall,
                    color = scheme.onSurfaceVariant
                )
                Spacer(modifier = Modifier.height(4.dp))
            }
            if (conversation.unread > 0) {
                Box(
                    modifier = Modifier
                        .clip(CircleShape)
                        .background(scheme.error)
                        .padding(horizontal = 7.dp, vertical = 2.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        if (conversation.unread > 99) "99+" else conversation.unread.toString(),
                        style = MaterialTheme.typography.labelSmall,
                        color = scheme.onError
                    )
                }
            }
        }
    }
}
