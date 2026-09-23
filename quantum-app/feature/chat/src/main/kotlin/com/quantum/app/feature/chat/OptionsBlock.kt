package com.quantum.app.feature.chat

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.quantum.app.core.common.MessagePayloadParser
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.storage.db.ChatMessageEntity

/**
 * 消息可点选项块（2026-09-18 富交互）：渲染在气泡内容下方，
 * 点按即以 reply 文本走指令通道回复（与手打完全等价的二次触发）。
 * - 可重复点选 + 多值已选（2026-09-19）：同一块内可继续点其它/同一选项（如逐首保存多首歌），
 *   本地 pickedKeys 集合记录每个点过的 key（行尾逐项「已选」徽标）；点选产生的回显消息行
 *   渲染为居中系统提示「已选择「label」」而非用户气泡（本端单端记忆，见 docs/选项已选态改造计划.md）；
 * - 过期态：同会话出现更新的带选项消息后，旧消息选项置灰加「已过期」——脚本重发新列表即作废旧列表，
 *   多设备同步后判定一致，无需服务端消费态；过期块保留已选标记（历史事实）。
 */
@Composable
internal fun OptionsBlock(
    message: ChatMessageEntity,
    latestOptionsSeq: Long,
    viewModel: ChatViewModel
) {
    val payload = remember(message.payload) { MessagePayloadParser.parse(message.payload) }
    val options = payload?.options
    if (options.isNullOrEmpty()) {
        return
    }
    val pickedKeys = remember(message.pickedKeys) { ChatMessageEntity.decodePickedKeys(message.pickedKeys) }
    val expired = message.seq < latestOptionsSeq
    val blockAlpha = if (expired) 0.45f else 1f

    Column(modifier = Modifier.alpha(blockAlpha)) {
        Spacer(modifier = Modifier.height(8.dp))
        HorizontalDivider(
            thickness = 1.dp,
            color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.6f),
            modifier = Modifier.fillMaxWidth()
        )
        Spacer(modifier = Modifier.height(8.dp))

        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            options.forEach { option ->
                OptionRow(
                    label = option.label,
                    desc = option.desc,
                    color = optionColor(option.color),
                    chosen = pickedKeys.contains(option.key),
                    enabled = !expired,
                    onClick = {
                        viewModel.pickOption(message.seq, option.key, option.reply ?: option.key, option.label, payload.taskId)
                    }
                )
            }
        }
        if (expired) {
            Spacer(modifier = Modifier.height(4.dp))
            Text(
                "该列表已过期（脚本已推送新列表）",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

/** 可点选项行：色点 + 主文案（+副行说明）+ 已选徽标（多值：点过的 key 均保留），整行可点（≥44dp 触达）。 */
@Composable
private fun OptionRow(
    label: String,
    desc: String?,
    color: androidx.compose.ui.graphics.Color,
    chosen: Boolean,
    enabled: Boolean,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(QuantumRadius.Sm))
            .background(color.copy(alpha = if (chosen) 0.22f else 0.10f))
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .size(6.dp)
                .clip(CircleShape)
                .background(color)
        )
        Spacer(modifier = Modifier.width(8.dp))
        Column(modifier = Modifier.weight(1f).padding(vertical = 2.dp)) {
            Text(
                label,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.Medium,
                color = MaterialTheme.colorScheme.onSurface
            )
            if (!desc.isNullOrBlank()) {
                Text(
                    desc,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
        if (chosen) {
            Text(
                "已选",
                style = MaterialTheme.typography.labelSmall,
                color = color,
                fontWeight = FontWeight.Medium
            )
        }
    }
}

/** 选项色名 → 调色板（与富文本标记同一枚举；非法名兜底灰）。 */
@Composable
internal fun optionColor(name: String?): androidx.compose.ui.graphics.Color {
    val rich = when (name?.lowercase()) {
        "red" -> com.quantum.app.core.common.RichColor.RED
        "green" -> com.quantum.app.core.common.RichColor.GREEN
        "orange" -> com.quantum.app.core.common.RichColor.ORANGE
        "blue" -> com.quantum.app.core.common.RichColor.BLUE
        "purple" -> com.quantum.app.core.common.RichColor.PURPLE
        else -> null
    }
    return rich?.palette() ?: MaterialTheme.colorScheme.onSurfaceVariant
}
