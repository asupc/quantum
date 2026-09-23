package com.quantum.app.core.common

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class MessagePayloadTest {

    @Test
    fun `空与空白载荷返回 null`() {
        assertNull(MessagePayloadParser.parse(null))
        assertNull(MessagePayloadParser.parse(""))
        assertNull(MessagePayloadParser.parse("   "))
    }

    @Test
    fun `损坏 JSON 返回 null 降级`() {
        assertNull(MessagePayloadParser.parse("{broken"))
        assertNull(MessagePayloadParser.parse("[1,2,3]"))
    }

    @Test
    fun `完整选项载荷解析`() {
        val payload = MessagePayloadParser.parse(
            """{"options":[{"key":"3","label":"3. 晴天 - 周杰伦","reply":"3","color":"green","desc":"《叶惠美》 04:29"},
               {"key":"save","label":"保存","reply":"保存3","color":null,"desc":null}]}"""
        )
        assertEquals(2, payload?.options?.size)
        val first = payload!!.options[0]
        assertEquals("3", first.key)
        assertEquals("3. 晴天 - 周杰伦", first.label)
        assertEquals("3", first.reply)
        assertEquals("green", first.color)
        assertEquals("《叶惠美》 04:29", first.desc)
        assertNull(payload.options[1].color)
        assertNull(payload.options[1].desc)
    }

    @Test
    fun `封面载荷解析`() {
        val payload = MessagePayloadParser.parse("""{"poster":"api/AppMedia/file?path=x.jpg"}""")
        assertEquals("api/AppMedia/file?path=x.jpg", payload?.poster)
        assertTrue(payload?.options.isNullOrEmpty())
    }

    @Test
    fun `未知字段容忍`() {
        // 服务端后续扩展字段（如进度类）不破坏旧客户端解析
        val payload = MessagePayloadParser.parse("""{"poster":"https://x/1.jpg","future":"whatever"}""")
        assertEquals("https://x/1.jpg", payload?.poster)
    }

    @Test
    fun `选项缺省 reply 与 color 为 null`() {
        val payload = MessagePayloadParser.parse("""{"options":[{"key":"1","label":"选项一"}]}""")
        val option = payload?.options?.single()
        assertNull(option?.reply)
        assertNull(option?.color)
    }

    @Test
    fun `点选载荷解析 taskId`() {
        // 2026-09-18 会话归并：选项载荷根部携带来源任务 Id，点选代发据此做精确路由
        val payload = MessagePayloadParser.parse(
            """{"taskId":"task-9","options":[{"key":"3","label":"3. 晴天","reply":"3"}]}"""
        )
        assertEquals("task-9", payload?.taskId)
        // 旧服务端载荷无 taskId → null（点选退化为普通代发）
        assertNull(MessagePayloadParser.parse("""{"options":[{"key":"1","label":"选项一"}]}""")?.taskId)
    }
}
