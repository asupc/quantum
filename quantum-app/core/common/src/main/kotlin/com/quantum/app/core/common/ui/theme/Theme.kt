package com.quantum.app.core.common.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/**
 * Quantum 统一设计规范（与 Web 管理端「暗色科技风」视觉语言完全对齐）：
 * 锁定 Slate Dark Tech 配色系统，消除 Android 12+ 动态壁纸对科技深色调的色彩污染。
 *
 * 色值集中在本对象：Material 主题与拿不到 MaterialTheme 的场景（桌面小组件等）共用一份，
 * 避免各写一份后逐渐漂移。窗口底色请与 res/values/colors.xml 的 quantum_window_background 同步。
 */
object QuantumPalette {
    /** 页面最深一级底色（窗口背景/启动闪屏同值）。 */
    val Background = Color(0xFF0B0F19)
    val Surface = Color(0xFF111827)
    val SurfaceVariant = Color(0xFF1E293B)
    val SurfaceContainer = Color(0xFF162032)
    val SurfaceContainerHigh = Color(0xFF1C2841)
    val SurfaceContainerHighest = Color(0xFF243048)

    val Cyan = Color(0xFF0EA5E9)          // 主强调色：天空蓝 / 量子青
    val CyanContainer = Color(0xFF0C4A6E)
    val Indigo = Color(0xFF6366F1)        // 辅助强调：电光紫蓝
    val IndigoContainer = Color(0xFF312E81)
    val Emerald = Color(0xFF10B981)       // 成功 / 启用状态
    val EmeraldContainer = Color(0xFF064E3B)
    val Rose = Color(0xFFF43F5E)          // 危险 / 失败 / 停用
    val RoseContainer = Color(0xFF881337)
    val Amber = Color(0xFFF59E0B)         // 警示 / 进行中（富文本 orange 标记色）

    val TextPrimary = Color(0xFFF8FAFC)
    val TextSecondary = Color(0xFFCBD5E1)
    val TextMuted = Color(0xFF94A3B8)
    val Outline = Color(0xFF334155)
    val OutlineVariant = Color(0xFF1E293B)
}

/**
 * 深色槽位必须成套覆盖：M3 基线对未赋值槽位有默认值，其中 surfaceContainer* 落在深紫
 * （弹层/底部抽屉串色）、inverseSurface 落在近白 #E6E1E9（Snackbar 变白底——与用户反馈的
 * 「白色背景」同一类问题）。
 */
private val DarkColors = darkColorScheme(
    primary = QuantumPalette.Cyan,
    onPrimary = Color.White,
    primaryContainer = QuantumPalette.CyanContainer,
    onPrimaryContainer = Color(0xFFE0F2FE),
    inversePrimary = Color(0xFF7DD3FC),

    secondary = QuantumPalette.Indigo,
    onSecondary = Color.White,
    secondaryContainer = QuantumPalette.IndigoContainer,
    onSecondaryContainer = Color(0xFFE0E7FF),

    tertiary = QuantumPalette.Emerald,
    onTertiary = Color.White,
    tertiaryContainer = QuantumPalette.EmeraldContainer,
    onTertiaryContainer = Color(0xFFD1FAE5),

    error = QuantumPalette.Rose,
    onError = Color.White,
    errorContainer = QuantumPalette.RoseContainer,
    onErrorContainer = Color(0xFFFFE4E6),

    background = QuantumPalette.Background,
    onBackground = QuantumPalette.TextPrimary,

    surface = QuantumPalette.Surface,
    onSurface = QuantumPalette.TextPrimary,
    surfaceVariant = QuantumPalette.SurfaceVariant,
    onSurfaceVariant = QuantumPalette.TextMuted,
    surfaceTint = QuantumPalette.Cyan,
    surfaceDim = QuantumPalette.Background,
    surfaceBright = QuantumPalette.SurfaceContainerHighest,
    surfaceContainerLowest = QuantumPalette.Background,
    surfaceContainerLow = QuantumPalette.Surface,
    surfaceContainer = QuantumPalette.SurfaceContainer,
    surfaceContainerHigh = QuantumPalette.SurfaceContainerHigh,
    surfaceContainerHighest = QuantumPalette.SurfaceContainerHighest,

    // 端内不出现亮底弹层：inverse* 亦取深色，Snackbar 等反色提示一律深底浅字
    inverseSurface = QuantumPalette.SurfaceVariant,
    inverseOnSurface = QuantumPalette.TextPrimary,

    outline = QuantumPalette.Outline,
    outlineVariant = QuantumPalette.OutlineVariant,
    scrim = Color.Black
)

/** 备用浅色方案（当前构建未启用，QuantumTheme 默认锁定深色）；槽位同样覆盖齐全以避免串色。 */
private val LightColors = lightColorScheme(
    primary = Color(0xFF0284C7),
    onPrimary = Color.White,
    secondary = Color(0xFF4F46E5),
    tertiary = Color(0xFF059669),
    error = Color(0xFFE11D48),
    background = Color(0xFFF8FAFC),
    onBackground = Color(0xFF0F172A),
    surface = Color.White,
    onSurface = Color(0xFF1E293B),
    surfaceVariant = Color(0xFFF1F5F9),
    onSurfaceVariant = Color(0xFF64748B),
    surfaceTint = Color(0xFF0284C7),
    surfaceContainerLowest = Color.White,
    surfaceContainerLow = Color(0xFFF8FAFC),
    surfaceContainer = Color(0xFFF1F5F9),
    surfaceContainerHigh = Color(0xFFE7EBF1),
    surfaceContainerHighest = Color(0xFFDDE3EC),
    inverseSurface = Color(0xFF1E293B),
    inverseOnSurface = Color(0xFFF8FAFC),
    inversePrimary = Color(0xFF7DD3FC),
    outline = Color(0xFFCBD5E1),
    outlineVariant = Color(0xFFE2E8F0)
)

@Composable
fun QuantumTheme(
    darkTheme: Boolean = true, // 默认锁定 Dark Tech 体验
    content: @Composable () -> Unit
) {
    val colorScheme = if (darkTheme) DarkColors else LightColors
    MaterialTheme(colorScheme = colorScheme, content = content)
}
