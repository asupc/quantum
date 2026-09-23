package com.quantum.app.core.storage.db

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.RawQuery
import androidx.room.Transaction
import androidx.room.Upsert
import androidx.sqlite.db.SupportSQLiteQuery
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.map
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.Json

/**
 * 会话消息（对齐服务端 t_chat_message + 本地态）：seq 为主键、msgId 幂等去重，
 * 服务端是唯一事实源，本地库可随时删库重建（登录后全量重同步）。
 * v6 起带二级索引：(sessionId, seq) 联合索引服务会话内分页/未读聚合，msgId 非唯一索引
 * 服务幂等去重按 msgId 点查（存量数据无唯一性保证，不可加 UNIQUE）。
 */
@Entity(
    tableName = "chat_message",
    indices = [
        Index(value = ["sessionId", "seq"]),
        Index(value = ["msgId"])
    ]
)
data class ChatMessageEntity(
    @PrimaryKey val seq: Long,
    val msgId: String,
    /** 1=发送(机器人→用户 右侧) / 2=接收(用户→机器人) */
    val direction: Int,
    val content: String,
    val contentType: String = "text",
    /** 配文：image/file 消息附带的文字说明（服务端 ContentText），文本消息为 null */
    val contentText: String? = null,
    /** 服务端 Status：1发送中/2已送达/3已读；本地方向 2 的已读恒为 3 */
    val status: Int = STATUS_SENT_PENDING,
    /** 服务端原串 yyyy-MM-dd HH:mm:ss */
    val createTime: String,
    /** 会话键（= 任务 Id，服务端 SessionKey）：空串 = 默认会话（快捷回复/系统通知/存量消息） */
    val sessionId: String = "",
    /** image/file 消息的元数据缓存（fileId/fileName/size），可空 JSON */
    val fileMeta: String? = null,
    /** 结构化富交互载荷（服务端 Payload JSON 原文）：可点选项/视频封面，可空 */
    val payload: String? = null,
    /** 本地已选 key 集合（JSON 字符串数组，点选成功后追加去重）：多值——每个点过的 key 都标「已选」 */
    val pickedKeys: String? = null,
    /** 点选代发标记（本端单端记忆）：非空 = 该「接收」行由点选选项产生，渲染为居中系统提示 */
    val pickLabel: String? = null,
    /** outbox 发送中标记（同步回包确认后清除） */
    val pending: Boolean = false
) {
    companion object {
        const val DIRECTION_FROM_BOT = 1
        const val DIRECTION_FROM_USER = 2
        const val STATUS_SENT_PENDING = 1
        const val STATUS_DELIVERED = 2
        const val STATUS_READ = 3

        /**
         * pickedKeys 列编解码（JSON 字符串数组；kotlinx 而非 org.json——后者在 JVM 单测是 stub）。
         * 空集合存 null 与旧行兼容；损坏串自愈为空集（本地记忆损坏降级为「无已选」）。
         */
        fun encodePickedKeys(keys: Collection<String>): String? =
            if (keys.isEmpty()) null else pickedKeysJson.encodeToString(pickedKeysSerializer, keys.toList())

        fun decodePickedKeys(raw: String?): Set<String> {
            if (raw.isNullOrEmpty()) {
                return emptySet()
            }
            return runCatching {
                pickedKeysJson.decodeFromString(pickedKeysSerializer, raw).toSet()
            }.getOrDefault(emptySet())
        }

        private val pickedKeysJson = Json
        private val pickedKeysSerializer = ListSerializer(String.serializer())
    }
}

@Dao
interface ChatMessageDao {
    @Upsert
    suspend fun upsertAll(messages: List<ChatMessageEntity>)

    @Upsert
    suspend fun upsert(message: ChatMessageEntity)

    @Query("SELECT * FROM chat_message WHERE pending = 0 ORDER BY seq DESC LIMIT :limit OFFSET :offset")
    fun page(limit: Int, offset: Int): Flow<List<ChatMessageEntity>>

    @Query("SELECT * FROM chat_message ORDER BY seq DESC LIMIT 1")
    suspend fun latest(): ChatMessageEntity?

    @Query("SELECT * FROM chat_message WHERE msgId = :msgId LIMIT 1")
    suspend fun byMsgId(msgId: String): ChatMessageEntity?

    /** 按 msgId 批量查已有行（[persistMessages] 落库前合并本地富交互标记列用）。 */
    @Query("SELECT * FROM chat_message WHERE msgId IN (:msgIds)")
    suspend fun byMsgIds(msgIds: List<String>): List<ChatMessageEntity>

    /** 指定会话的分页消息（seq 倒序，UI 端反转显示）。 */
    @Query("SELECT * FROM chat_message WHERE pending = 0 AND sessionId = :sessionId ORDER BY seq DESC LIMIT :limit OFFSET :offset")
    fun sessionPage(sessionId: String, limit: Int, offset: Int): Flow<List<ChatMessageEntity>>

    /** 会话内未读数：机器人下发且 seq 超过本地已读水位的条数。 */
    @Query("SELECT COUNT(*) FROM chat_message WHERE pending = 0 AND sessionId = :sessionId AND direction = 1 AND seq > :afterSeq")
    suspend fun sessionUnread(sessionId: String, afterSeq: Long): Int

    /**
     * 未读聚合一（2.2）：一条 SQL 得到全部会话的未读数（替代逐会话 COUNT 的 N+1）。
     * waterlines = 会话键 → 本地已读水位（缺省会话不进映射时按 0 计——与逐会话查询口径一致，
     * 由 [UnreadCountsQuery.waterlines] 统一合并）；结果只含未读 ≥ 1 的会话，miss 视为 0。
     * 空映射直接返回空 Flow 不发查询。Room 经 observedEntities 盯 chat_message 失效，
     * 消息落库/删除即重查；水位变化由上层 flatMapLatest 换新查询。
     */
    fun unreadCounts(waterlines: Map<String, Long>): Flow<Map<String, Int>> {
        val chunks = UnreadCountsQuery.chunks(waterlines)
        if (chunks.isEmpty()) {
            return flowOf(emptyMap())
        }
        if (chunks.size == 1) {
            return unreadCountsRaw(UnreadCountsQuery.build(chunks.first()))
                .map { rows -> rows.associate { it.sessionId to it.unread } }
        }
        // §4-13：会话数超单查询绑定参数上限 → 分块各发一条聚合查询再合并。
        // 各块会话键互斥（一会话只落一块），并集即正确总量、绝不重复计数；
        // combine 让任一块失效重查都触发整体重发（observedEntities 盯同一张表）。
        val flows = chunks.map { chunk ->
            unreadCountsRaw(UnreadCountsQuery.build(chunk))
                .map { rows -> rows.associate { it.sessionId to it.unread } }
        }
        return combine(flows) { maps -> maps.fold(emptyMap()) { acc, m -> acc + m } }
    }

    /** [unreadCounts] 的 @RawQuery 承载（水位成对绑定参数），生产代码请走上层默认方法。 */
    @RawQuery(observedEntities = [ChatMessageEntity::class])
    fun unreadCountsRaw(query: SupportSQLiteQuery): Flow<List<SessionUnreadRow>>

    /** 会话内最大 seq（进入会话时推进本地已读水位用）。 */
    @Query("SELECT MAX(seq) FROM chat_message WHERE pending = 0 AND sessionId = :sessionId")
    suspend fun sessionMaxSeq(sessionId: String): Long?

    /** 补拉通知：某水位后新入库的机器人下发消息（seq 倒序，按会话归并由调用方完成）。 */
    @Query("SELECT * FROM chat_message WHERE pending = 0 AND direction = 1 AND seq > :afterSeq ORDER BY seq DESC")
    suspend fun botMessagesAfter(afterSeq: Long): List<ChatMessageEntity>

    @Query("DELETE FROM chat_message WHERE pending = 1")
    suspend fun dropPending()

    /**
     * 点选成功后把 key 追加进该选项块的已选集合（读改写 JSON 数组、去重；本地语义，多设备各自维护）。
     * 行不存在（会话已清空）静默忽略。
     */
    @Transaction
    suspend fun addPickedKey(seq: Long, key: String) {
        val row = bySeq(seq) ?: return
        val keys = ChatMessageEntity.decodePickedKeys(row.pickedKeys) + key
        updatePickedKeys(seq, ChatMessageEntity.encodePickedKeys(keys))
    }

    @Query("SELECT * FROM chat_message WHERE seq = :seq LIMIT 1")
    suspend fun bySeq(seq: Long): ChatMessageEntity?

    @Query("UPDATE chat_message SET pickedKeys = :keys WHERE seq = :seq")
    suspend fun updatePickedKeys(seq: Long, keys: String?)

    /** 删除该会话全部本地消息行（清空/删除会话用；会话行由 ChatSessionDao 单独维护）。 */
    @Query("DELETE FROM chat_message WHERE sessionId = :sessionId")
    suspend fun deleteSession(sessionId: String)

    /**
     * 跨会话触发消息迁移（message_moved 帧，2026-09-18 批次）：按 msgId 把行搬到目标会话，
     * 已在目标会话/行不存在则 0 行（幂等）。返回受影响行数（供调用方决定后续动作）。
     */
    @Query("UPDATE chat_message SET sessionId = :to WHERE msgId = :msgId AND sessionId != :to")
    suspend fun moveSession(msgId: String, to: String): Int

    @Query("DELETE FROM chat_message")
    suspend fun clear()
}

/** 未读聚合行（[ChatMessageDao.unreadCounts] 的查询结果 POJO）。 */
data class SessionUnreadRow(
    val sessionId: String,
    val unread: Int
)

/**
 * 会话行（2026-09-18 会话生命周期）：让会话有独立存在性——清空记录保留会话、
 * 支持从列表单独删除；会话列表改以本表为主 LEFT JOIN 最后消息（不再由消息行聚合，
 * 行删即会话消失的旧行为废除）。与消息行同库同生命周期，删除后任务再推送时自动重建。
 */
@Entity(tableName = "chat_session")
data class ChatSessionEntity(
    /** 会话键（对齐消息行 sessionId：任务 Id 或共享会话名）：空串 = 默认会话 */
    @PrimaryKey val sessionKey: String,
    /** 会话首见时间（首条消息落库时刻，本地时钟毫秒；仅空会话排序兜底用，不展示） */
    val createTime: Long,
    /** 本会话已见最大消息 Seq（排序用）；消息行删除（清空）后保留原值 */
    val lastSeq: Long
)

/** 会话列表行：会话表为主 LEFT JOIN 每会话最后一条消息；空会话（无消息行）last* 字段为 null。 */
data class SessionLastMessageRow(
    val sessionKey: String,
    val createTime: Long,
    val lastSeq: Long,
    /** 最后消息正文（预览用）；空会话 null */
    val lastContent: String? = null,
    /** 最后消息类型（预览用）；空会话 null */
    val lastContentType: String? = null,
    /** 最后消息服务端时间串；空会话 null（列表不显示时间） */
    val lastCreateTime: String? = null
)

@Dao
interface ChatSessionDao {

    /**
     * 会话行推进（消息落库同一落库函数内调用）：按 sessionId 分组，
     * 不存在建行（createTime = 首见时间）、存在则 lastSeq = max(原值, 本批最大 seq)。
     */
    @Transaction
    suspend fun advanceAll(messages: List<ChatMessageEntity>) {
        if (messages.isEmpty()) {
            return
        }
        val existing = byKeys(messages.map { it.sessionId }.distinct()).associateBy { it.sessionKey }
        val now = System.currentTimeMillis()
        val rows = messages.groupBy { it.sessionId }.map { (key, group) ->
            val maxSeq = group.maxOf { it.seq }
            val old = existing[key]
            ChatSessionEntity(
                sessionKey = key,
                createTime = old?.createTime ?: now,
                lastSeq = maxOf(old?.lastSeq ?: 0L, maxSeq)
            )
        }
        upsertAll(rows)
    }

    @Query("SELECT * FROM chat_session WHERE sessionKey IN (:keys)")
    suspend fun byKeys(keys: List<String>): List<ChatSessionEntity>

    @Upsert
    suspend fun upsertAll(sessions: List<ChatSessionEntity>)

    /**
     * 会话列表（主从结构）：会话表为主 LEFT JOIN 每会话最后一条消息（该会话 MAX(seq) 行），
     * 空会话返回行、last* 为 null；排序：有消息的按 lastSeq 降序在前，空会话垫底（按首见时间降序）。
     */
    @Query(
        """SELECT s.sessionKey AS sessionKey, s.createTime AS createTime, s.lastSeq AS lastSeq,
           m.content AS lastContent, m.contentType AS lastContentType, m.createTime AS lastCreateTime
           FROM chat_session s
           LEFT JOIN chat_message m ON m.sessionId = s.sessionKey AND m.pending = 0
                AND m.seq = (SELECT MAX(seq) FROM chat_message WHERE sessionId = s.sessionKey AND pending = 0)
           ORDER BY (m.seq IS NULL) ASC, s.lastSeq DESC, s.createTime DESC"""
    )
    fun sessionsWithLast(): Flow<List<SessionLastMessageRow>>

    /** 删除会话行（删除会话时随消息行一起清）。 */
    @Query("DELETE FROM chat_session WHERE sessionKey = :sessionKey")
    suspend fun delete(sessionKey: String)

    @Query("DELETE FROM chat_session")
    suspend fun clear()
}

/**
 * 站内通知（对齐服务端 t_app_notification），msgId 为全局幂等键。
 * v6 起 msgId 加显式索引：会话页通知卡片按 msgId IN (...) 按需查元数据（2.3）走索引。
 */
@Entity(tableName = "notification", indices = [Index("msgId")])
data class NotificationEntity(
    @PrimaryKey val msgId: String,
    val title: String,
    val content: String,
    val category: String = "system",
    val jump: String? = null,
    val createdAt: String,
    val readAt: String? = null
)

@Dao
interface NotificationDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIfAbsent(notification: NotificationEntity): Long

    /**
     * 批量插入（2.3）：单事务 INSERT OR IGNORE，通知同步一次落库只失效一次 Room 表，
     * 返回各条 rowId（-1 = 已存在被忽略），调用方据此统计新增条数。
     */
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertAllIfAbsent(notifications: List<NotificationEntity>): List<Long>

    /** 按 msgId 集合查元数据（2.3：会话页通知卡片按需关联；空集由调用方短路，不发查询）。 */
    @Query("SELECT * FROM notification WHERE msgId IN (:ids)")
    fun byMsgIds(ids: List<String>): Flow<List<NotificationEntity>>

    @Query("SELECT * FROM notification ORDER BY createdAt DESC LIMIT :limit OFFSET :offset")
    fun page(limit: Int, offset: Int): Flow<List<NotificationEntity>>

    /** 全量通知（会话流通知卡片按 msgId 关联元数据用；单管理员场景数据量有限）。 */
    @Query("SELECT * FROM notification")
    fun all(): Flow<List<NotificationEntity>>

    @Query("SELECT COUNT(*) FROM notification WHERE readAt IS NULL")
    fun unreadCount(): Flow<Int>

    @Query("UPDATE notification SET readAt = :readAt WHERE msgId = :msgId AND readAt IS NULL")
    suspend fun markRead(msgId: String, readAt: String)

    @Query("UPDATE notification SET readAt = :readAt WHERE readAt IS NULL")
    suspend fun markAllRead(readAt: String)

    @Query("DELETE FROM notification")
    suspend fun clear()
}

/** 同步游标（单行）：maxSeq 为该用户已同步到的全局最大 Seq。 */
@Entity(tableName = "sync_cursor")
data class SyncCursorEntity(
    @PrimaryKey val id: Int = 0,
    val maxSeq: Long = 0L
)

@Dao
interface SyncCursorDao {
    @Query("SELECT * FROM sync_cursor WHERE id = 0")
    suspend fun get(): SyncCursorEntity?

    @Upsert
    suspend fun set(cursor: SyncCursorEntity)
}

/** 本地待发送指令（发送中气泡的持久化），同步回包后删除。 */
@Entity(tableName = "outbox")
data class OutboxEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val content: String,
    val createTime: Long = System.currentTimeMillis(),
    val failed: Boolean = false
)

@Dao
interface OutboxDao {
    @Insert
    suspend fun add(entry: OutboxEntity): Long

    @Query("SELECT * FROM outbox WHERE failed = 0 ORDER BY id LIMIT 1")
    suspend fun firstPending(): OutboxEntity?

    @Query("UPDATE outbox SET failed = 1 WHERE id = :id")
    suspend fun markFailed(id: Long)

    @Query("DELETE FROM outbox WHERE id = :id")
    suspend fun remove(id: Long)

    @Query("DELETE FROM outbox")
    suspend fun clear()
}
