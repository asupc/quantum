package com.quantum.app.core.common.ui.components

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import com.quantum.app.core.common.ui.theme.QuantumRadius

/** 弹层语义：Danger 用于删除/注销这类不可逆操作（确认键走 error 色）。 */
enum class QuantumDialogVariant { Normal, Danger }

/**
 * 统一确认弹层：圆角 18、surfaceContainerHigh 底、右下角按钮组
 * （Ghost 取消 + Primary/Danger 确认）。替代各页裸写 `AlertDialog`。
 */
@Composable
fun QuantumDialog(
    title: String,
    onDismiss: () -> Unit,
    modifier: Modifier = Modifier,
    content: String? = null,
    body: (@Composable () -> Unit)? = null,
    confirmText: String = "确认",
    onConfirm: (() -> Unit)? = null,
    dismissText: String = "取消",
    variant: QuantumDialogVariant = QuantumDialogVariant.Normal
) {
    AlertDialog(
        modifier = modifier,
        onDismissRequest = onDismiss,
        shape = RoundedCornerShape(QuantumRadius.Lg),
        containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
        title = {
            Text(
                text = title,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface
            )
        },
        text = {
            if (body != null) {
                body()
            } else if (content != null) {
                Text(
                    text = content,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        },
        confirmButton = {
            QuantumButton(
                text = confirmText,
                onClick = {
                    onConfirm?.invoke()
                    onDismiss()
                },
                variant = if (variant == QuantumDialogVariant.Danger) {
                    QuantumButtonVariant.Danger
                } else {
                    QuantumButtonVariant.Primary
                },
                size = QuantumButtonSize.Sm
            )
        },
        dismissButton = {
            QuantumButton(
                text = dismissText,
                onClick = onDismiss,
                variant = QuantumButtonVariant.Ghost,
                size = QuantumButtonSize.Sm
            )
        }
    )
}

/**
 * 详情弹层：等宽风可滚动正文（日志详情、脚本内容这类长文本）。
 */
@Composable
fun QuantumTextDialog(
    title: String,
    text: String,
    onDismiss: () -> Unit,
    modifier: Modifier = Modifier,
    emptyText: String = "（无内容）"
) {
    AlertDialog(
        modifier = modifier,
        onDismissRequest = onDismiss,
        shape = RoundedCornerShape(QuantumRadius.Lg),
        containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
        title = {
            Text(
                text = title,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface
            )
        },
        text = {
            Box(
                modifier = Modifier
                    .heightIn(max = 360.dp)
                    .verticalScroll(rememberScrollState())
                    .horizontalScroll(rememberScrollState())
            ) {
                Text(
                    text = text.ifBlank { emptyText },
                    style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        },
        confirmButton = {
            QuantumButton(
                text = "关闭",
                onClick = onDismiss,
                variant = QuantumButtonVariant.Ghost,
                size = QuantumButtonSize.Sm
            )
        }
    )
}
