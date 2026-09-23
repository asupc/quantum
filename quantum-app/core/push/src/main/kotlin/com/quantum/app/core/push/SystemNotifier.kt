package com.quantum.app.core.push

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 系统通知（本地弹出）：渠道与 category 一一对应（契约 §6）——
 * quantum_task=task、quantum_system=system/security、quantum_chat=chat。
 * 消息实时性由常驻 WS 承担：后台（进程存活）收到 notify 帧时由此弹系统通知；
 * 前台抑制由调用方决定（App 在前台时会话页在屏，仅入库不弹）。
 */
@Singleton
class SystemNotifier @Inject constructor(@ApplicationContext private val context: Context) {

    fun ensureChannels() {
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_TASK, "任务通知", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "任务开始/结束/失败"
            }
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_SYSTEM, "系统与安全", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "公告/群发/登录提醒"
            }
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_CHAT, "对话消息", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "离线对话消息"
            }
        )
    }

    /**
     * 弹系统通知；msgId 作通知 id（同 id 覆盖，天然去重）。
     * jump 深链经 quantum:// scheme 回 App，由 Shell 路由。
     * largeImage 非空时以大图样式（BigPictureStyle）展示：折叠态文案，展开态显示图片。
     */
    fun post(
        category: String,
        title: String,
        content: String,
        jump: String?,
        msgId: String?,
        largeImage: android.graphics.Bitmap? = null
    ) {
        if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) {
            return
        }
        val channelId = when (category) {
            "task" -> CHANNEL_TASK
            "chat" -> CHANNEL_CHAT
            else -> CHANNEL_SYSTEM
        }
        val intent = Intent(Intent.ACTION_VIEW).apply {
            data = Uri.parse(jump ?: DeepLinkFallback.CHAT)
            `package` = context.packageName
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        }
        val pending = PendingIntent.getActivity(
            context,
            (msgId ?: title).hashCode(),
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val style = if (largeImage != null) {
            NotificationCompat.BigPictureStyle()
                .bigPicture(largeImage)
                .setSummaryText(content)
        } else {
            NotificationCompat.BigTextStyle().bigText(content)
        }
        val notification = NotificationCompat.Builder(context, channelId)
            .setSmallIcon(android.R.drawable.ic_dialog_info)
            .setContentTitle(title)
            .setContentText(content)
            .setStyle(style)
            .setAutoCancel(true)
            .setContentIntent(pending)
            .build()
        runCatching {
            NotificationManagerCompat.from(context).notify((msgId ?: title ?: "quantum").hashCode(), notification)
        }
    }

    private object DeepLinkFallback {
        const val CHAT = "quantum://chat"
    }

    companion object {
        const val CHANNEL_TASK = "quantum_task"
        const val CHANNEL_SYSTEM = "quantum_system"
        const val CHANNEL_CHAT = "quantum_chat"
    }
}
