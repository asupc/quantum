package com.quantum.app.feature.ai

import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.outlined.DeleteOutline
import androidx.compose.material.icons.outlined.Memory
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
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
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import com.quantum.app.core.common.ui.components.ConfirmDialog
import com.quantum.app.core.common.ui.components.QuantumButton
import com.quantum.app.core.common.ui.components.QuantumButtonVariant
import com.quantum.app.core.common.ui.components.QuantumEmptyState
import com.quantum.app.core.common.ui.components.QuantumListCard
import com.quantum.app.core.common.ui.components.QuantumListRow
import com.quantum.app.core.common.ui.components.QuantumPullRefresh
import com.quantum.app.core.common.ui.components.QuantumStatusBadge
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import com.quantum.app.core.network.dto.AiConversationDto

/**
 * AI 助手会话列表（底部「AI助手」tab 根页，2026-09-21 由管理宫格升级，与会话平级）：
 * 顶部默认模型行（未配置按字段判空出引导文案）+ 会话列表 + 新建（空标题，标题由后端随首条消息生成）。
 */
@Composable
fun AiConversationsScreen(
    onOpen: (String) -> Unit,
    viewModel: AiConversationsViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsState()
    var pendingDelete by remember { mutableStateOf<AiConversationDto?>(null) }

    // 操作反馈限时展示（与 AdminScreens 同款：toast 写入 VM 后由页面渲染，4s 自动消退）
    LaunchedEffect(state.toast) {
        if (state.toast != null) {
            kotlinx.coroutines.delay(4_000)
            viewModel.consumeToast()
        }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        // 页头（tab 根页形态，与会话列表一致：无返回箭头，底部导航常驻）
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = QuantumPage.Padding, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                "AI 助手",
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.Bold
            )
            Spacer(modifier = Modifier.width(8.dp))
            Text(
                "脚本修复提案 · 对话式编排",
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        state.toast?.let {
            Text(
                it,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(horizontal = QuantumPage.Padding, vertical = QuantumSpacing.Xs)
            )
        }

        QuantumPullRefresh(refreshing = state.loading, onRefresh = { viewModel.load() }, modifier = Modifier.fillMaxSize()) {
            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(horizontal = QuantumPage.Padding, vertical = 6.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                // 默认模型行：未配置供应商时端点返回 200+空字段（不报错），按 ProviderName 判空出引导
                item {
                    val configured = state.defaultModel?.configured == true
                    QuantumListCard {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 14.dp, vertical = 12.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Icon(
                                Icons.Outlined.Memory,
                                contentDescription = null,
                                tint = if (configured) {
                                    MaterialTheme.colorScheme.primary
                                } else {
                                    MaterialTheme.colorScheme.onSurfaceVariant
                                },
                                modifier = Modifier.size(18.dp)
                            )
                            Spacer(modifier = Modifier.width(10.dp))
                            Text(
                                text = AiPayloadParser.defaultModelSubtitle(state.defaultModel),
                                style = MaterialTheme.typography.bodyMedium,
                                color = if (configured) {
                                    MaterialTheme.colorScheme.onSurface
                                } else {
                                    MaterialTheme.colorScheme.onSurfaceVariant
                                },
                                modifier = Modifier.weight(1f)
                            )
                            if (configured) {
                                QuantumStatusBadge(text = "在线", active = true)
                            }
                        }
                    }
                }

                if (!state.loading && state.conversations.isEmpty()) {
                    item {
                        QuantumEmptyState(
                            icon = Icons.Filled.AutoAwesome,
                            title = "还没有 AI 会话",
                            description = "新建会话，让 AI 帮你修复或编写任务脚本"
                        )
                    }
                }

                items(state.conversations.size, key = { state.conversations[it].id }) { index ->
                    val conv = state.conversations[index]
                    QuantumListCard {
                        QuantumListRow(
                            title = conv.title?.takeIf { it.isNotBlank() } ?: "未命名会话",
                            subtitle = conv.lastMessageTime?.let { "最后消息 $it" } ?: "暂无消息",
                            leadingIcon = Icons.Filled.AutoAwesome,
                            onClick = { onOpen(conv.id) },
                            trailing = {
                                // 无涟漪点击：行尾删除不与整行点击叠加出双反馈
                                val interaction = remember { MutableInteractionSource() }
                                Icon(
                                    Icons.Outlined.DeleteOutline,
                                    contentDescription = "删除会话",
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                    modifier = Modifier
                                        .size(20.dp)
                                        .clickable(
                                            interactionSource = interaction,
                                            indication = null
                                        ) { pendingDelete = conv }
                                )
                            }
                        )
                    }
                }

                item {
                    Spacer(modifier = Modifier.height(4.dp))
                    QuantumButton(
                        text = "新建会话",
                        onClick = { viewModel.create(onOpen) },
                        variant = QuantumButtonVariant.Primary,
                        fillWidth = true,
                        enabled = !state.creating,
                        loading = state.creating
                    )
                    Spacer(modifier = Modifier.height(12.dp))
                }
            }
        }
    }

    pendingDelete?.let { conv ->
        ConfirmDialog(
            title = "删除 AI 会话",
            content = "将删除「${conv.title?.takeIf { it.isNotBlank() } ?: "未命名会话"}」及全部对话记录，不可恢复。",
            onConfirm = {
                viewModel.delete(conv.id)
                pendingDelete = null
            },
            onDismiss = { pendingDelete = null }
        )
    }
}
