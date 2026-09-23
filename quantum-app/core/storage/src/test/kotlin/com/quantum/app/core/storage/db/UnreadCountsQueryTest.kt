package com.quantum.app.core.storage.db

import androidx.sqlite.db.SupportSQLiteProgram
import org.junit.After
import org.junit.Before
import org.junit.Test
import java.sql.Connection
import java.sql.DriverManager
import java.sql.PreparedStatement
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * 2.2 未读聚合一的 SQL 等价性单测：JVM 直连内存 SQLite，建与 v6 Room 同构的
 * chat_message 表（含 (sessionId, seq) 联合索引 / msgId 索引），验证
 * **单条聚合查询与逐会话 COUNT（Room sessionUnread 同款 SQL）结果完全一致**，
 * 且查询计划命中索引。
 */
class UnreadCountsQueryTest {

    private lateinit var connection: Connection

    /** JDBC 参数适配器：把 SupportSQLiteQuery 的绑定序列转给 PreparedStatement。 */
    private class JdbcProgram(private val statement: PreparedStatement) : SupportSQLiteProgram {
        override fun bindNull(index: Int) = statement.setNull(index, java.sql.Types.NULL)
        override fun bindLong(index: Int, value: Long) = statement.setLong(index, value)
        override fun bindDouble(index: Int, value: Double) = statement.setDouble(index, value)
        override fun bindString(index: Int, value: String) = statement.setString(index, value)
        override fun bindBlob(index: Int, value: ByteArray) = statement.setBytes(index, value)
        override fun clearBindings() = statement.clearParameters()
        override fun close() = statement.close()
    }

    /** 记录绑定顺序（sid、wl 成对交替）的探针。 */
    private class RecordingProgram : SupportSQLiteProgram {
        val bound = mutableListOf<Any>()
        override fun bindNull(index: Int) { bound += "null" }
        override fun bindLong(index: Int, value: Long) { bound += value }
        override fun bindDouble(index: Int, value: Double) { bound += value }
        override fun bindString(index: Int, value: String) { bound += value }
        override fun bindBlob(index: Int, value: ByteArray) { bound += value }
        override fun clearBindings() = Unit
        override fun close() = Unit
    }

    @Before
    fun setUp() {
        connection = DriverManager.getConnection("jdbc:sqlite::memory:")
        connection.createStatement().use { statement ->
            statement.execute(
                """CREATE TABLE chat_message (
                    seq INTEGER NOT NULL PRIMARY KEY,
                    msgId TEXT NOT NULL,
                    direction INTEGER NOT NULL,
                    sessionId TEXT NOT NULL,
                    pending INTEGER NOT NULL
                )"""
            )
            // 与 Room v6 生成的索引同构：聚合查询的 JOIN 内层走 (sessionId, seq) 范围扫
            statement.execute("CREATE INDEX index_chat_message_sessionId_seq ON chat_message(sessionId, seq)")
            statement.execute("CREATE INDEX index_chat_message_msgId ON chat_message(msgId)")
        }
    }

    @After
    fun tearDown() {
        connection.close()
    }

    private fun insert(seq: Long, msgId: String, direction: Int, sessionId: String, pending: Boolean = false) {
        connection.prepareStatement(
            "INSERT INTO chat_message(seq, msgId, direction, sessionId, pending) VALUES (?,?,?,?,?)"
        ).use { statement ->
            statement.setLong(1, seq)
            statement.setString(2, msgId)
            statement.setInt(3, direction)
            statement.setString(4, sessionId)
            statement.setInt(5, if (pending) 1 else 0)
            statement.executeUpdate()
        }
    }

    /** 逐会话 COUNT：与 Room ChatMessageDao.sessionUnread 完全同款 SQL（等价性基准）。 */
    private fun perSessionUnread(sessionId: String, afterSeq: Long): Int {
        connection.prepareStatement(
            "SELECT COUNT(*) FROM chat_message WHERE pending = 0 AND sessionId = ? AND direction = 1 AND seq > ?"
        ).use { statement ->
            statement.setString(1, sessionId)
            statement.setLong(2, afterSeq)
            statement.executeQuery().use { resultSet ->
                resultSet.next()
                return resultSet.getInt(1)
            }
        }
    }

    /** 聚合查询：经生产同款 build()/bindTo 执行，返回 sessionId → 未读（只含 ≥ 1，miss = 0）。 */
    private fun aggregateUnread(waterlines: Map<String, Long>): Map<String, Int> {
        val query = UnreadCountsQuery.build(waterlines)
        val statement = connection.prepareStatement(query.sql)
        query.bindTo(JdbcProgram(statement))
        val resultSet = statement.executeQuery()
        val result = LinkedHashMap<String, Int>()
        while (resultSet.next()) {
            result[resultSet.getString("sessionId")] = resultSet.getInt("unread")
        }
        return result
    }

    @Test
    fun aggregateQuery_EquivalentToPerSessionCount() {
        // 数据面覆盖：多会话（含默认会话空串）、双向、pending 行、水位以下/以上、无水位会话
        insert(5, "a5", direction = 1, sessionId = "task-1")
        insert(6, "a6", direction = 2, sessionId = "task-1")   // 用户行不计未读
        insert(10, "a10", direction = 1, sessionId = "task-1") // == 水位，不计
        insert(11, "a11", direction = 1, sessionId = "task-1") // 未读
        insert(12, "a12", direction = 1, sessionId = "task-1") // 未读
        insert(13, "a13", direction = 2, sessionId = "task-1")
        insert(14, "a14", direction = 1, sessionId = "task-1", pending = true) // pending 不计
        (101..105).forEach { insert(it.toLong(), "b$it", direction = 1, sessionId = "task-2") } // 水位 0 全未读
        insert(15, "c15", direction = 1, sessionId = "")       // 默认会话水位 20 以下
        insert(21, "c21", direction = 1, sessionId = "")
        insert(22, "c22", direction = 2, sessionId = "")
        insert(23, "c23", direction = 1, sessionId = "")
        insert(25, "c25", direction = 1, sessionId = "")
        insert(30, "d30", direction = 1, sessionId = "task-empty-session") // 不在水位映射里 → 不计数

        val waterlines = UnreadCountsQuery.waterlines(
            sessionKeys = listOf("task-1", "task-2", "", "task-empty"),
            readSeqs = mapOf("task-1" to 10L, "" to 20L, "ghost" to 99L) // task-2 无水位 → 0
        )

        // 基准：逐会话 COUNT（缺省水位 0 口径）
        val expected = waterlines.keys.associateWith { sid ->
            perSessionUnread(sid, waterlines.getValue(sid))
        }.filterValues { it > 0 }

        val actual = aggregateUnread(waterlines)

        assertEquals(expected, actual, "单条聚合查询必须与逐会话 COUNT 完全等价")
        // 关键口径抽查：task-1=2（11/12）、task-2=5、默认=3（21/23/25）、无水位映射会话不计
        assertEquals(mapOf("task-1" to 2, "task-2" to 5, "" to 3), actual)
    }

    @Test
    fun aggregateQuery_EmptySessionsCountAsZero() {
        insert(1, "x1", direction = 1, sessionId = "only")
        val waterlines = UnreadCountsQuery.waterlines(listOf("only", "drained"), emptyMap())

        assertEquals(mapOf("only" to 1, "drained" to 0), waterlines.mapValues { (sid, wl) -> perSessionUnread(sid, wl) })
        assertEquals(mapOf("only" to 1), aggregateUnread(waterlines), "无未读会话不进结果（miss = 0）")
    }

    @Test
    fun waterlines_MergesSessionKeysWithReadSeqs_InStableOrder() {
        val merged = UnreadCountsQuery.waterlines(
            sessionKeys = listOf("task-1", "task-2"),
            readSeqs = mapOf("task-1" to 5L, "ghost" to 9L)
        )

        assertEquals(mapOf("task-1" to 5L, "task-2" to 0L, "ghost" to 9L), merged)
        assertEquals(listOf("task-1", "task-2", "ghost"), merged.keys.toList(), "会话键在前、纯水位键在后（迭代序稳定）")
    }

    @Test
    fun build_BindsSidThenWaterlinePerPair() {
        val probe = RecordingProgram()
        UnreadCountsQuery.build(linkedMapOf("task-1" to 10L, "task-2" to 0L)).bindTo(probe)
        assertEquals(listOf<Any>("task-1", 10L, "task-2", 0L), probe.bound, "参数按 sid,wl 成对交替绑定")
    }

    @Test
    fun sql_PlaceholderCountMatchesPairs() {
        assertEquals(2, UnreadCountsQuery.sql(1).count { it == '?' })
        assertEquals(6, UnreadCountsQuery.sql(3).count { it == '?' })
    }

    @Test
    fun aggregateQuery_PlannerUsesIndex() {
        val plan = connection.createStatement().executeQuery(
            "EXPLAIN QUERY PLAN ${UnreadCountsQuery.sql(2)}"
        )
        val detail = buildString {
            while (plan.next()) {
                appendLine(plan.getString(4))
            }
        }
        assertTrue(
            detail.contains("USING INDEX") && detail.contains("index_chat_message_sessionId_seq"),
            "聚合查询的 chat_message 访问应命中 (sessionId, seq) 联合索引，实际计划：$detail"
        )
    }

    @Test
    fun chunks_SplitsPreservingOrderAndSize() {
        val map = (1..1000).associate { "s$it" to it.toLong() }  // LinkedHashMap 保序
        val chunks = UnreadCountsQuery.chunks(map)

        assertEquals(3, chunks.size, "1000 条按 400 上限切 3 块")
        assertEquals(listOf(400, 400, 200), chunks.map { it.size })
        assertEquals(map.keys.toList(), chunks.flatMap { it.keys.toList() }, "拼接回原序、无重无漏")
        assertEquals(map, chunks.fold(emptyMap()) { acc, chunk -> acc + chunk })
        // 边界：恰好到上限只一块；空映射零块（调用方短路）
        assertEquals(1, UnreadCountsQuery.chunks((1..400).associate { "s$it" to 0L }).size)
        assertEquals(0, UnreadCountsQuery.chunks(emptyMap()).size)
    }

    @Test
    fun chunkedAggregate_MergesToPerSessionEquivalence() {
        // 建 450 会话（>400 触发分块），每会话 2 条机器人未读行
        val sessions = (1..450).map { "task-$it" }
        sessions.forEachIndexed { idx, sid ->
            insert((idx + 1) * 10L, "id$idx", direction = 1, sessionId = sid)
            insert((idx + 1) * 10L + 1, "idu$idx", direction = 1, sessionId = sid)
        }
        val waterlines = UnreadCountsQuery.waterlines(sessions, emptyMap())

        val merged = UnreadCountsQuery.chunks(waterlines)
            .fold(emptyMap<String, Int>()) { acc, chunk -> acc + aggregateUnread(chunk) }
        val expected = waterlines.keys.associateWith { perSessionUnread(it, 0L) }.filterValues { it > 0 }

        assertEquals(expected, merged, "分块查询合并结果必须与逐会话 COUNT 等价")
        assertEquals(450, merged.size)
        assertTrue(merged.values.all { it == 2 }, "每会话各 2 条未读")
    }
}
