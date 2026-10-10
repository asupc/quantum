package com.quantum.app.core.network.ws

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull

/**
 * WS 帧容错解码（F-审计）：服务端手写 JSON 的标量类型漂移（`"seq":"42"`、`"msgId":42`、
 * `"direction":"1"`）此前会让 `decodeFromString<WsFrame>` 抛 SerializationException，
 * 整帧被静默丢弃（消息永久丢失且不 ack，服务端按未确认反复重投）。
 *
 * 现按字段语义做**只读标量归一**后再交给常规解码器：
 * - 数字字段（seq/afterSeq/maxSeq）：字符串数字还原为数字；不可解析 → null
 *   （帧仍可用：上层按「缺 seq」触发 REST 补拉，绝不自造 seq）；
 * - 数字字段（direction）：同上（字符串/数字互通）；
 * - 字符串字段（msgId/session/content 等）：数字/布尔取原文串（`42` → "42"）；
 * - 结构不可用的帧（非 JSON、顶层不是对象、缺 type）返回失败结果，交调用方 WARN 后丢弃。
 *
 * **不改变正常帧的解码语义，也不参与上行编码**（encode 走原序列化器，字节不变）。
 */
object WsFrameCodec {

    /** 长整型标量字段（字符串数字需还原）。 */
    private val LONG_FIELDS = setOf("seq", "afterSeq", "maxSeq")

    /** 整型标量字段。 */
    private val INT_FIELDS = setOf("direction")

    /** 字符串标量字段（数字/布尔取原文串）。 */
    private val STRING_FIELDS = setOf(
        "type", "token", "content", "msgId", "contentType", "contentText",
        "session", "sessionTitle", "targetTask", "payload", "createTime",
        "category", "title", "jump", "from", "to"
    )

    /**
     * 解码结果：成功携带 [frame]；失败携带 [reason]（含帧型，供分类告警）且 frame 为 null。
     * 失败即「真正不可用」，调用方丢弃该帧（连接与后续帧不受影响）。
     */
    data class Outcome(val frame: WsFrame?, val reason: String?) {
        val ok: Boolean get() = frame != null
    }

    /** 容错解码：任何输入都不抛异常（异常折叠为失败结果）。 */
    fun decode(json: Json, text: String): Outcome {
        val element = runCatching { json.parseToJsonElement(text) }.getOrElse { e ->
            return Outcome(null, "非法 JSON：${e.message}")
        }
        val obj = element as? JsonObject ?: return Outcome(null, "帧不是 JSON 对象")
        val type = (obj["type"] as? JsonPrimitive)?.contentOrNull
            ?: return Outcome(null, "帧缺 type")
        val normalized = JsonObject(obj.mapValues { (key, value) -> coerceScalar(key, value) })
        val frame = runCatching { json.decodeFromJsonElement(WsFrame.serializer(), normalized) }
            .getOrElse { e -> return Outcome(null, "字段类型不可用（type=$type）：${e.message}") }
        return Outcome(frame, null)
    }

    /** 单字段标量归一：只处理已知字段名，其余原样透传（未知扩展字段不影响解码）。 */
    private fun coerceScalar(key: String, value: JsonElement): JsonElement {
        val primitive = value as? JsonPrimitive ?: return value
        if (primitive is JsonNull) {
            return value
        }
        return when (key) {
            // 空格友好：中间层补白不改变数字语义
            in LONG_FIELDS -> primitive.content.trim().toLongOrNull()
                ?.let { JsonPrimitive(it) } ?: JsonNull
            in INT_FIELDS -> primitive.content.trim().toIntOrNull()
                ?.let { JsonPrimitive(it) } ?: JsonNull
            in STRING_FIELDS -> if (primitive.isString) value else JsonPrimitive(primitive.content)
            else -> value
        }
    }
}
