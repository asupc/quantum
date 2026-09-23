package com.quantum.app.core.common

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * 消息富交互载荷（服务端 t_chat_message.Payload 的 JSON，camelCase 契约）：
 * 服务端组装（脚本门面）、不解析透传；客户端解析失败按无载荷降级为普通消息渲染。
 */
@Serializable
data class MessagePayload(
    /** 产生本消息的来源任务 Id（点选代发透传给 command 帧 targetTask 做精确路由）；旧服务端缺省 null */
    val taskId: String? = null,
    /** 视频/音频封面地址（绝对 URL 或 api/ 开头的相对地址） */
    val poster: String? = null,
    /** 可点选项（空 = 无选项的普通消息） */
    val options: List<MessageOption> = emptyList()
)

/** 可点选项：点按即以 [reply]（服务端已归一为缺省 key）作为指令文本回复。 */
@Serializable
data class MessageOption(
    val key: String,
    val label: String,
    val reply: String? = null,
    /** 调色板色名（与富文本标记同枚举 red/green/orange/blue/purple/gray；null/未知 = 默认灰） */
    val color: String? = null,
    /** 副行说明（专辑/时长等） */
    val desc: String? = null
)

object MessagePayloadParser {

    private val json = Json { ignoreUnknownKeys = true }

    /** 空/损坏载荷返回 null（调用方降级普通渲染，绝不抛出）。 */
    fun parse(raw: String?): MessagePayload? {
        if (raw.isNullOrBlank()) {
            return null
        }
        return runCatching { json.decodeFromString<MessagePayload>(raw) }.getOrNull()
    }
}
