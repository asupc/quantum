package com.quantum.app.feature.chat

import com.quantum.app.core.storage.db.ChatMessageEntity
import com.quantum.app.core.storage.db.NotificationEntity
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * 2.3 通知元数据按需关联流：只对当前会话**通知类型**消息的 msgId 发 IN 查询（去重），
 * 空集/无通知消息不发查询；查询结果按 msgId 建映射（miss 由卡片降级渲染）。
 */
class NotifyMetaFlowTest {

    private fun message(seq: Long, msgId: String, contentType: String = "text") =
        ChatMessageEntity(seq = seq, msgId = msgId, direction = 1, content = "c", contentType = contentType, createTime = "t")

    private fun notify(msgId: String) =
        NotificationEntity(msgId = msgId, title = "标题", content = "内容", createdAt = "t")

    @Test
    fun emptyMessages_NeverQueries() = runTest {
        val queries = mutableListOf<List<String>>()

        val meta = notificationMetaFlow(flowOf(emptyList())) { ids ->
            queries += ids
            flowOf(emptyList())
        }.first()

        assertTrue(queries.isEmpty(), "空消息集应短路，不发查询")
        assertEquals(emptyMap(), meta)
    }

    @Test
    fun noNotifyMessages_NeverQueries() = runTest {
        val queries = mutableListOf<List<String>>()

        val meta = notificationMetaFlow(
            flowOf(listOf(message(1, "t1"), message(2, "t2")))
        ) { ids ->
            queries += ids
            flowOf(emptyList())
        }.first()

        assertTrue(queries.isEmpty(), "无通知类型消息时不发查询（普通消息 msgId 不在通知表）")
        assertEquals(emptyMap(), meta)
    }

    @Test
    fun onlyNotifyMsgIdsQueried_Distinct() = runTest {
        val messages = listOf(
            message(1, "t1"),
            message(2, "n1", contentType = ChatRepository.NOTIFY_CONTENT_TYPE),
            message(3, "n2", contentType = ChatRepository.NOTIFY_CONTENT_TYPE),
            // 同 msgId 双行（WS/REST 两条途径不同 seq 落库的既有场景）→ 去重
            message(4, "n1", contentType = ChatRepository.NOTIFY_CONTENT_TYPE)
        )
        var queried: List<String>? = null

        val meta = notificationMetaFlow(flowOf(messages)) { ids ->
            queried = ids
            flowOf(listOf(notify("n1"), notify("n2")))
        }.first()

        assertEquals(listOf("n1", "n2"), queried, "只查通知类消息的 msgId 且去重")
        assertEquals(setOf("n1", "n2"), meta.keys)
        assertEquals("标题", meta["n1"]?.title)
    }

    @Test
    fun missingRows_DegradeToEmptyMap() = runTest {
        val messages = listOf(message(1, "n1", contentType = ChatRepository.NOTIFY_CONTENT_TYPE))

        val meta = notificationMetaFlow(flowOf(messages)) {
            flowOf(emptyList())
        }.first()

        assertEquals(emptyMap(), meta, "通知表关联不到时返回空映射（卡片降级为正文）")
    }
}
