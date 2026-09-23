package com.quantum.app.core.network

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** WS 帧解析器（契约 §4.3：小写驼峰、sync 帧多 direction）。 */
class WsFrameTest {

    private val json = Json { ignoreUnknownKeys = true }

    @Test
    fun parsesDirectMessageFrame_withoutDirection() {
        val frame = json.decodeFromString<com.quantum.app.core.network.ws.WsFrame>(
            """{"type":"message","msgId":"m1","seq":7,"content":"你好","contentType":"text","createTime":"2026-09-14 10:00:00"}"""
        )
        assertEquals("message", frame.type)
        assertEquals("m1", frame.msgId)
        assertEquals(7L, frame.seq)
        assertNull(frame.direction)
    }

    @Test
    fun parsesSyncMessageFrame_withDirection() {
        val frame = json.decodeFromString<com.quantum.app.core.network.ws.WsFrame>(
            """{"type":"message","msgId":"m2","seq":8,"content":"hi","contentType":"text","direction":1,"createTime":"2026-09-14 10:00:01"}"""
        )
        assertEquals(1, frame.direction)
    }

    @Test
    fun parsesNotifyFrame() {
        val frame = json.decodeFromString<com.quantum.app.core.network.ws.WsFrame>(
            """{"type":"notify","msgId":"n1","category":"task","title":"任务","content":"完成","jump":"quantum://task/t/log"}"""
        )
        assertEquals("notify", frame.type)
        assertEquals("n1", frame.msgId)
        assertEquals("task", frame.category)
        assertEquals("任务", frame.title)
        assertEquals("完成", frame.content)
        assertEquals("quantum://task/t/log", frame.jump)
    }

    @Test
    fun parsesNotifyFrame_withMinimalFields() {
        // 最小 notify 帧：category/title/content/jump 全缺省（服务端只保证 type/msgId）
        val frame = json.decodeFromString<com.quantum.app.core.network.ws.WsFrame>(
            """{"type":"notify","msgId":"n2"}"""
        )
        assertEquals("notify", frame.type)
        assertEquals("n2", frame.msgId)
        assertNull(frame.category)
        assertNull(frame.title)
        assertNull(frame.content)
        assertNull(frame.jump)
    }

    @Test
    fun parsesNotifyFrame_ignoresUnknownExtraKeys() {
        val frame = json.decodeFromString<com.quantum.app.core.network.ws.WsFrame>(
            """{"type":"notify","msgId":"n3","category":"system","title":"t","content":"c","extra":"x","nested":{"a":1}}"""
        )
        assertEquals("notify", frame.type)
        assertEquals("system", frame.category)
        assertEquals("t", frame.title)
    }

    @Test
    fun encodesPingFrame() {
        val text = json.encodeToString(
            com.quantum.app.core.network.ws.WsFrame(type = "ping")
        )
        assertEquals("""{"type":"ping"}""", text)
    }

    @Test
    fun encodesCommandFrame_withAndWithoutTargetTask() {
        // 点选代发（2026-09-18 会话归并）：command 帧携带 targetTask 做精确路由；手打缺省省略
        val withTarget = json.encodeToString(
            com.quantum.app.core.network.ws.WsFrame(type = "command", content = "3", session = "dygangs", targetTask = "task-9")
        )
        assertEquals("""{"type":"command","content":"3","session":"dygangs","targetTask":"task-9"}""", withTarget)
        val withoutTarget = json.encodeToString(
            com.quantum.app.core.network.ws.WsFrame(type = "command", content = "3", session = "dygangs")
        )
        assertEquals("""{"type":"command","content":"3","session":"dygangs"}""", withoutTarget)
    }

    @Test
    fun parsesMessageMovedFrame_withAndWithoutFrom() {
        // 跨会话触发消息迁移（2026-09-18 批次）：from 缺省 = 默认会话
        val frame = json.decodeFromString<com.quantum.app.core.network.ws.WsFrame>(
            """{"type":"message_moved","msgId":"m9","seq":42,"to":"task-pwd"}"""
        )
        assertEquals("message_moved", frame.type)
        assertEquals("m9", frame.msgId)
        assertEquals(42L, frame.seq)
        assertNull(frame.from)
        assertEquals("task-pwd", frame.to)

        val withFrom = json.decodeFromString<com.quantum.app.core.network.ws.WsFrame>(
            """{"type":"message_moved","msgId":"m9","seq":42,"from":"task-music","to":"task-pwd"}"""
        )
        assertEquals("task-music", withFrom.from)
        assertEquals("task-pwd", withFrom.to)
    }
}
