package com.quantum.app.core.storage.db

import java.sql.Connection
import java.sql.DriverManager
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * v7 → v8（G-Push 会话标题）迁移的真库验证：用 sqlite-jdbc 造一份 **v7 形态**的库、写入数据，
 * 再按 [AppSchemaMigrations.V7_TO_V8] 逐条执行，断言：
 * 1. 两个新列以可空形态出现；
 * 2. 存量消息行/会话行原样保留（新列为 NULL，即按旧规则渲染，不出现假标题）；
 * 3. **outbox 待发正文与 pickedKeys 已选态活过迁移**——服务端没有这两份数据，
 *    destructive 重建会把它们清掉，这正是本次必须走显式迁移的理由。
 */
class AppSchemaMigrationsTest {

    private fun openV7Schema(): Connection {
        val conn = DriverManager.getConnection("jdbc:sqlite::memory:")
        conn.createStatement().use {
            // v7 形态：chat_message 尚无 sessionTitle、chat_session 尚无 displayTitle
            it.executeUpdate(
                """CREATE TABLE chat_message (
                       seq INTEGER PRIMARY KEY NOT NULL, msgId TEXT NOT NULL, direction INTEGER NOT NULL,
                       content TEXT NOT NULL, contentType TEXT NOT NULL, contentText TEXT,
                       status INTEGER NOT NULL, createTime TEXT NOT NULL, sessionId TEXT NOT NULL,
                       fileMeta TEXT, payload TEXT, pickedKeys TEXT, pickLabel TEXT, pending INTEGER NOT NULL)"""
            )
            it.executeUpdate(
                "CREATE TABLE chat_session (sessionKey TEXT PRIMARY KEY NOT NULL, createTime INTEGER NOT NULL, lastSeq INTEGER NOT NULL)"
            )
            it.executeUpdate(
                """CREATE TABLE outbox (
                       id INTEGER PRIMARY KEY AUTOINCREMENT, content TEXT NOT NULL,
                       createTime INTEGER NOT NULL, failed INTEGER NOT NULL)"""
            )
            it.executeUpdate(
                "INSERT INTO chat_message VALUES (1,'m1',1,'旧消息','text',NULL,3,'2026-01-01 00:00:00','',NULL,NULL,'[\"k1\"]',NULL,0)"
            )
            it.executeUpdate("INSERT INTO chat_session VALUES ('',1000,1)")
            it.executeUpdate("INSERT INTO outbox (content,createTime,failed) VALUES ('待重发的正文',2000,0)")
        }
        return conn
    }

    private fun columns(conn: Connection, table: String): List<String> =
        conn.createStatement().use { st ->
            st.executeQuery("PRAGMA table_info($table)").let { rs ->
                buildList {
                    while (rs.next()) {
                        add(rs.getString("name") + "?" + rs.getString("type"))
                    }
                }
            }
        }

    @Test
    fun `v7 库无新列 迁移后出现且可空`() {
        val conn = openV7Schema()
        assertFalse(columns(conn, "chat_message").any { it.startsWith("sessionTitle") })
        assertFalse(columns(conn, "chat_session").any { it.startsWith("displayTitle") })

        AppSchemaMigrations.V7_TO_V8.forEach { conn.createStatement().execute(it) }

        assertTrue(columns(conn, "chat_message").contains("sessionTitle?TEXT"))
        assertTrue(columns(conn, "chat_session").contains("displayTitle?TEXT"))
        conn.close()
    }

    @Test
    fun `存量行原样保留 新列为 NULL 不伪造标题`() {
        val conn = openV7Schema()
        AppSchemaMigrations.V7_TO_V8.forEach { conn.createStatement().execute(it) }

        conn.createStatement().use {
            val rs = it.executeQuery("SELECT content, pickedKeys, sessionTitle FROM chat_message WHERE seq=1")
            assertTrue(rs.next())
            assertEquals("旧消息", rs.getString("content"))
            // 选项已选态必须在
            assertEquals("[\"k1\"]", rs.getString("pickedKeys"))
            // 存量行没有标题来源：必须 NULL（渲染侧回落任务名/会话键），不能落空串冒充
            assertTrue(rs.getString("sessionTitle") == null)
            assertFalse(rs.next())

            val session = it.executeQuery("SELECT lastSeq, displayTitle FROM chat_session WHERE sessionKey=''")
            assertTrue(session.next())
            assertEquals(1L, session.getLong("lastSeq"))
            assertEquals(null, session.getString("displayTitle"))
        }
        conn.close()
    }

    @Test
    fun `outbox 待发正文活过迁移`() {
        val conn = openV7Schema()
        AppSchemaMigrations.V7_TO_V8.forEach { conn.createStatement().execute(it) }

        conn.createStatement().use {
            val rs = it.executeQuery("SELECT COUNT(*) FROM outbox WHERE content='待重发的正文' AND failed=0")
            assertTrue(rs.next())
            assertEquals(1, rs.getInt(1))
        }
        conn.close()
    }

    @Test
    fun `迁移清单与新增列声明一一对应`() {
        // 防止「实体加了列但忘了 ALTER」：声明表/列必须与 SQL 严格对齐
        AppSchemaMigrations.V7_TO_V8_ADDED_COLUMNS.forEach { (table, added) ->
            added.forEach { column ->
                assertTrue(
                    AppSchemaMigrations.V7_TO_V8.any { it.contains("ALTER TABLE `$table` ADD COLUMN `$column`") },
                    "$table.$column 缺 ALTER 语句"
                )
            }
        }
        assertEquals(AppSchemaMigrations.V7_TO_V8_ADDED_COLUMNS.values.sumOf { it.size }, AppSchemaMigrations.V7_TO_V8.size)
    }

    @Test
    fun `实体确实带上了这两列`() {
        // 具名实参只能在属性存在时编译通过：把实体与迁移列名的对齐钉到编译期
        val message = ChatMessageEntity(
            seq = 9, msgId = "m9", direction = 1, content = "c",
            createTime = "2026-09-26 00:00:00", sessionTitle = "机房监控"
        )
        val session = ChatSessionEntity(sessionKey = "external:c1:ab", createTime = 1L, lastSeq = 9L, displayTitle = "机房监控")

        assertEquals("机房监控", message.sessionTitle)
        assertEquals("机房监控", session.displayTitle)
    }
}
