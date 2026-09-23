package com.quantum.app.feature.chat

/**
 * 当前在屏的会话页（sessionId，空串=默认会话；null=没有会话页在前台）。
 * 通知抑制的判定粒度：只有「用户正盯着这个会话」才不弹系统通知，
 * App 在其他任何页面（前台其他 tab / 后台）都照常弹——由会话页组合时上报、
 * 离开时清空（DisposableEffect），AppPushHandler 在帧到达时读取。
 */
object ChatScreenTracker {
    @Volatile
    var visibleSessionId: String? = null
}
