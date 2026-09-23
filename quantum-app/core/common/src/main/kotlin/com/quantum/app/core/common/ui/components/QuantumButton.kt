package com.quantum.app.core.common.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSize
import com.quantum.app.core.common.ui.theme.QuantumSpacing

/** 按钮语义变体：同屏只允许一个 Primary。 */
enum class QuantumButtonVariant { Primary, Tonal, Outlined, Ghost, Danger }

/** 按钮尺寸：Md=44dp（主操作）、Sm=36dp（行内/次要）。 */
enum class QuantumButtonSize { Md, Sm }

/**
 * 统一按钮：五种语义变体 + 两档尺寸 + loading/disabled。
 * 高度、圆角、内距全部取 token，替代各页自行拼 Button/OutlinedButton/TextButton。
 */
@Composable
fun QuantumButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    variant: QuantumButtonVariant = QuantumButtonVariant.Primary,
    size: QuantumButtonSize = QuantumButtonSize.Md,
    icon: ImageVector? = null,
    enabled: Boolean = true,
    loading: Boolean = false,
    fillWidth: Boolean = false
) {
    val scheme = MaterialTheme.colorScheme
    val container = when (variant) {
        QuantumButtonVariant.Primary -> scheme.primary
        QuantumButtonVariant.Tonal -> scheme.primary.copy(alpha = 0.14f)
        QuantumButtonVariant.Outlined -> Color.Transparent
        QuantumButtonVariant.Ghost -> Color.Transparent
        QuantumButtonVariant.Danger -> scheme.error
    }
    val content = when (variant) {
        QuantumButtonVariant.Primary -> scheme.onPrimary
        QuantumButtonVariant.Tonal -> scheme.primary
        QuantumButtonVariant.Outlined, QuantumButtonVariant.Ghost -> scheme.onSurfaceVariant
        QuantumButtonVariant.Danger -> scheme.onError
    }
    val shape = RoundedCornerShape(QuantumRadius.Md)
    val height = if (size == QuantumButtonSize.Md) QuantumSize.ControlHeight else QuantumSize.ControlHeightSm
    val horizontal = if (size == QuantumButtonSize.Md) QuantumSpacing.Lg else QuantumSpacing.Md
    val clickable = enabled && !loading

    Row(
        modifier = modifier
            .then(if (fillWidth) Modifier.fillMaxWidth() else Modifier)
            .height(height)
            .alpha(if (enabled) 1f else 0.45f)
            .clip(shape)
            .background(container)
            .then(
                if (variant == QuantumButtonVariant.Outlined) {
                    Modifier.border(1.dp, scheme.outline, shape)
                } else {
                    Modifier
                }
            )
            .clickable(enabled = clickable, onClick = onClick)
            .padding(horizontal = horizontal),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (loading) {
            CircularProgressIndicator(
                modifier = Modifier.size(QuantumSize.IconSm),
                strokeWidth = 2.dp,
                color = content
            )
        } else if (icon != null) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = content,
                modifier = Modifier.size(QuantumSize.IconMd)
            )
            Spacer(modifier = Modifier.width(QuantumSpacing.Sm))
        }
        Text(
            text = text,
            color = content,
            style = MaterialTheme.typography.labelLarge,
            maxLines = 1
        )
    }
}
