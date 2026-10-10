package com.quantum.app.core.network

import com.quantum.app.core.network.ws.WsFrameCodec
import kotlinx.serialization.json.Json
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * F1 回归（对抗审计）：WS 帧标量类型漂移不得导致整帧丢弃。
 * 场景：服务端/中间层把 `seq` 写成字符串（`"42"`）、`msgId` 写成数字、`direction` 写成字符串——
 * 旧实现 `decodeFromString` 抛 SerializationException 后 return，帧被静默丢弃且不 ack；
 * 新实现按字段语义归一后照常解码，且正常帧与上行编码契约（[com.quantum.app.core.network.WsFrameTest]）不变。
 */
class WsFrameCodecTest {

    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }

    // ---------------------------------------------------------------- 可归一：必须成功解码

    @Test
    fun seqAsNumericString_Decodes() {
        val outcome = WsFrameCodec.decode(
            json,
            """{"type":"message","msgId":"m1","seq":"42","content":"hi","contentType":"text","createTime":"t"}"""
        )

        val frame = assertNotNull(outcome.frame, "字符串 seq 必须归一后解码，不得丢帧：${outcome.reason}")
        assertEquals(42L, frame.seq)
        assertEquals("m1", frame.msgId)
        assertEquals("hi", frame.content)
    }

    @Test
    fun msgIdAsNumber_DecodesAsString() {
        val outcome = WsFrameCodec.decode(
            json, """{"type":"message","msgId":12345,"seq":9,"content":"c","createTime":"t"}"""
        )

        val frame = assertNotNull(outcome.frame, "数字 msgId 应取原文串：${outcome.reason}")
        assertEquals("12345", frame.msgId, "ack 幂等键按原文串比对，不得补零/转浮点")
        assertEquals(9L, frame.seq)
    }

    @Test
    fun directionAsString_Decodes() {
        val outcome = WsFrameCodec.decode(
            json, """{"type":"message","msgId":"m2","seq":10,"content":"c","direction":"1","createTime":"t"}"""
        )

        val frame = assertNotNull(outcome.frame, "字符串 direction 应归一：${outcome.reason}")
        assertEquals(1, frame.direction)
    }

    @Test
    fun seqWhitespacePaddedString_Decodes() {
        val outcome = WsFrameCodec.decode(
            json, """{"type":"message","msgId":"m3","seq":" 77 ","content":"c","createTime":"t"}"""
        )

        val frame = assertNotNull(outcome.frame, "带空白的数字串应可解析：${outcome.reason}")
        assertEquals(77L, frame.seq)
    }

    @Test
    fun syncFrame_allFieldsCoerced() {
        val outcome = WsFrameCodec.decode(
            json,
            """{"type":"sync","afterSeq":"100","maxSeq":"700","direction":"1","token":123}"""
        )

        val frame = assertNotNull(outcome.frame, outcome.reason)
        assertEquals("sync", frame.type)
        assertEquals(100L, frame.afterSeq)
        assertEquals(700L, frame.maxSeq)
        assertEquals(1, frame.direction)
        assertEquals("123", frame.token)
    }

    @Test
    fun unknownType_StillDecodes_NotAnError() {
        // 未知 type（新协议扩展）不是「不可用帧」：原样下发给上层忽略，不得丢帧
        val outcome = WsFrameCodec.decode(json, """{"type":"future_thing","msgId":"f1","seq":5}""")

        val frame = assertNotNull(outcome.frame, outcome.reason)
        assertEquals("future_thing", frame.type)
        assertEquals(5L, frame.seq)
    }

    // ---------------------------------------------------------------- 坏标量：帧可用、字段降级为 null

    @Test
    fun seqNull_DecodesWithNullSeq_NoThrow() {
        val outcome = WsFrameCodec.decode(json, """{"type":"message","msgId":"n1","seq":null,"content":"c"}""")

        val frame = assertNotNull(outcome.frame, outcome.reason)
        assertNull(frame.seq, "seq 缺失/非法 → null（上层按缺 seq 触发 REST 补拉，绝不自造）")
        assertEquals("n1", frame.msgId)
    }

    @Test
    fun seqUnparseableString_DecodesWithNullSeq_NoThrow() {
        // 非数字串不应炸掉整帧：该字段降级为 null，其余字段照常
        val outcome = WsFrameCodec.decode(json, """{"type":"message","msgId":"bad","seq":"not-a-number","content":"c"}""")

        val frame = assertNotNull(outcome.frame, outcome.reason)
        assertNull(frame.seq)
        assertEquals("bad", frame.msgId)
        assertEquals("c", frame.content)
    }

    // ---------------------------------------------------------------- 真正不可用：失败且有原因（不抛）

    @Test
    fun nonJson_FailsWithReason_NoThrow() {
        val outcome = WsFrameCodec.decode(json, "这是心跳文本，不是帧")

        assertNull(outcome.frame)
        assertNotNull(outcome.reason)
    }

    @Test
    fun emptyString_FailsWithReason_NoThrow() {
        val outcome = WsFrameCodec.decode(json, "")

        assertNull(outcome.frame)
        assertTrue(outcome.reason.orEmpty().isNotBlank(), "空串必须带原因失败")
    }

    @Test
    fun jsonArray_FailsWithReason_NoThrow() {
        val outcome = WsFrameCodec.decode(json, """[{"type":"message"}]""")

        assertNull(outcome.frame)
        assertTrue(outcome.reason.orEmpty().contains("对象"), "顶层非对象应说明原因：${outcome.reason}")
    }

    @Test
    fun missingType_FailsWithReason_NoThrow() {
        val outcome = WsFrameCodec.decode(json, """{"msgId":"m1","seq":1}""")

        assertNull(outcome.frame)
        assertTrue(outcome.reason.orEmpty().contains("type"), "缺 type 应说明原因：${outcome.reason}")
    }

    // ---------------------------------------------------------------- 正常帧语义不变

    @Test
    fun happyPath_Unchanged() {
        val outcome = WsFrameCodec.decode(
            json,
            """{"type":"message","msgId":"m1","seq":7,"content":"你好","contentType":"text","direction":1,"createTime":"2026-09-14 10:00:00"}"""
        )

        val frame = assertNotNull(outcome.frame)
        assertEquals("message", frame.type)
        assertEquals("m1", frame.msgId)
        assertEquals(7L, frame.seq)
        assertEquals(1, frame.direction)
        assertEquals("你好", frame.content)
        assertEquals("2026-09-14 10:00:00", frame.createTime)
        assertNull(frame.session)
    }

    @Test
    fun unknownExtraKeys_Ignored() {
        val outcome = WsFrameCodec.decode(
            json, """{"type":"notify","msgId":"n3","category":"system","extra":"x","nested":{"a":1}}"""
        )

        val frame = assertNotNull(outcome.frame)
        assertEquals("system", frame.category)
    }
}
