package com.quantum.app.core.push

/**
 * 推送载荷：与 /ws/app 下行 notify 报文同构。
 * 由 WS 常驻收集者（AppPushHandler）消费：msgId 幂等入库后按需弹本地通知（SystemNotifier）。
 */
data class PushPayload(
    val category: String,
    val title: String,
    val content: String,
    val jump: String?,
    val msgId: String?
)
