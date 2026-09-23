package com.quantum.app.core.common.ui.theme

import androidx.compose.ui.unit.dp

/**
 * 设计 token（间距）：元素内距用 Xs/Sm，行内元素间距用 Sm/Md，卡片内距用 Md，
 * 页面左右留白用 Lg，分组之间用 Xl/Xxl。新增页面一律取这里的值，不再手写 dp。
 */
object QuantumSpacing {
    val Xs = 4.dp
    val Sm = 8.dp
    val Md = 12.dp
    val Lg = 16.dp
    val Xl = 20.dp
    val Xxl = 24.dp
}

/**
 * 设计 token（圆角）：Sm=小控件、Md=卡片与主控件、Lg=弹层与大卡片、Xl=大面板、Pill=胶囊。
 */
object QuantumRadius {
    val Sm = 10.dp
    val Md = 14.dp
    val Lg = 18.dp
    val Xl = 24.dp
    val Pill = 50.dp
}

/** 设计 token（尺寸）：控件高度、可点最小尺寸、图标尺寸。 */
object QuantumSize {
    /** 主控件高度（输入框/主按钮）。 */
    val ControlHeight = 44.dp

    /** 紧凑控件高度（次要按钮/小筛选）。 */
    val ControlHeightSm = 36.dp

    /** 可点元素最小尺寸（触达规范）。 */
    val TouchMin = 44.dp

    val IconSm = 18.dp
    val IconMd = 20.dp
    val IconLg = 24.dp

    /** 列表行 leading 图标容器边长。 */
    val LeadingBox = 34.dp
}

/** 设计 token（页面骨架）：统一留白，杜绝 12/14/20/32 混用。 */
object QuantumPage {
    /** 页面左右留白。 */
    val Padding = 16.dp

    /** 页面顶部留白。 */
    val TopPadding = 12.dp

    /** 分组之间间距。 */
    val GroupGap = 20.dp

    /** 卡片/控件之间间距。 */
    val CardGap = 12.dp
}
