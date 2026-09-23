package com.quantum.app.core.common.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSize
import com.quantum.app.core.common.ui.theme.QuantumSpacing

/**
 * 统一输入框（filled 风格）：
 * - 标签**固定在框上方**（12sp 次要色），不用 M3 浮动 label 以免文字跳动；
 * - 聚焦时边框变主色并加粗（1.5dp），错误态边框与文案走 error；
 * - 非空显示清空按钮，密码型带可见性切换；高度对齐 `QuantumSize.ControlHeight`。
 *
 * 替代各页裸用 `OutlinedTextField`（历史上形状/高度/label 策略各不相同）。
 */
@Composable
fun QuantumTextField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    label: String? = null,
    placeholder: String? = null,
    singleLine: Boolean = true,
    minLines: Int = 1,
    maxLines: Int = if (singleLine) 1 else 5,
    enabled: Boolean = true,
    error: String? = null,
    helper: String? = null,
    isPassword: Boolean = false,
    keyboardType: KeyboardType = KeyboardType.Text,
    imeAction: ImeAction = ImeAction.Done,
    leadingIcon: ImageVector? = null,
    onDone: (() -> Unit)? = null
) {
    val scheme = MaterialTheme.colorScheme
    var focused by remember { mutableStateOf(false) }
    var revealed by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(QuantumRadius.Md)
    val borderColor = when {
        error != null -> scheme.error
        focused -> scheme.primary
        else -> scheme.outlineVariant
    }

    Column(modifier = modifier) {
        if (label != null) {
            Text(
                text = label,
                style = MaterialTheme.typography.labelSmall,
                color = if (error != null) scheme.error else scheme.onSurfaceVariant,
                modifier = Modifier.padding(start = 2.dp, bottom = QuantumSpacing.Sm - 2.dp)
            )
        }
        BasicTextField(
            value = value,
            onValueChange = onValueChange,
            enabled = enabled,
            singleLine = singleLine,
            minLines = minLines,
            maxLines = maxLines,
            textStyle = MaterialTheme.typography.bodyLarge.copy(color = scheme.onSurface),
            cursorBrush = SolidColor(scheme.primary),
            visualTransformation = if (isPassword && !revealed) {
                PasswordVisualTransformation()
            } else {
                VisualTransformation.None
            },
            keyboardOptions = KeyboardOptions(keyboardType = keyboardType, imeAction = imeAction),
            keyboardActions = KeyboardActions(onDone = { onDone?.invoke() }),
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = QuantumSize.ControlHeight)
                .clip(shape)
                .background(if (enabled) scheme.surfaceContainerHigh else scheme.surfaceContainerLow)
                .border(if (focused) 1.5.dp else 1.dp, borderColor, shape)
                .onFocusChanged { focused = it.isFocused }
                .padding(horizontal = QuantumSpacing.Md, vertical = QuantumSpacing.Md - 1.dp),
            decorationBox = { innerTextField ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (leadingIcon != null) {
                        Icon(
                            imageVector = leadingIcon,
                            contentDescription = null,
                            tint = scheme.onSurfaceVariant,
                            modifier = Modifier.size(QuantumSize.IconMd)
                        )
                        Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                    }
                    Box(modifier = Modifier.weight(1f)) {
                        if (value.isEmpty() && placeholder != null) {
                            Text(
                                text = placeholder,
                                style = MaterialTheme.typography.bodyLarge,
                                color = scheme.onSurfaceVariant.copy(alpha = 0.65f),
                                maxLines = 1
                            )
                        }
                        innerTextField()
                    }
                    if (value.isNotEmpty() && enabled) {
                        Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                        Icon(
                            imageVector = Icons.Filled.Close,
                            contentDescription = "清空",
                            tint = scheme.onSurfaceVariant,
                            modifier = Modifier
                                .size(QuantumSize.IconSm)
                                .clickable { onValueChange("") }
                        )
                    }
                    if (isPassword) {
                        Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
                        Icon(
                            imageVector = if (revealed) Icons.Filled.VisibilityOff else Icons.Filled.Visibility,
                            contentDescription = if (revealed) "隐藏" else "显示",
                            tint = scheme.onSurfaceVariant,
                            modifier = Modifier
                                .size(QuantumSize.IconMd)
                                .clickable { revealed = !revealed }
                        )
                    }
                }
            }
        )
        val supporting = error ?: helper
        if (supporting != null) {
            Text(
                text = supporting,
                style = MaterialTheme.typography.labelSmall,
                color = if (error != null) scheme.error else scheme.onSurfaceVariant,
                modifier = Modifier.padding(start = 2.dp, top = QuantumSpacing.Xs)
            )
        }
    }
}
