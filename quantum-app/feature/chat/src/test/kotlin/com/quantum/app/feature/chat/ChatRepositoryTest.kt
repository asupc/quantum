package com.quantum.app.feature.chat

import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.network.session.SessionGateway
import com.quantum.app.core.network.ws.WsFrame
import com.quantum.app.core.network.ws.WsGateway
import com.quantum.app.core.storage.db.ChatMessageDao
import com.quantum.app.core.storage.db.ChatMessageEntity
import com.quantum.app.core.storage.db.ChatSessionDao
import com.quantum.app.core.storage.db.ChatSessionEntity
import com.quantum.app.core.storage.db.OutboxDao
import com.quantum.app.core.storage.db.OutboxEntity
import com.quantum.app.core.storage.db.SessionLastMessageRow
import com.quantum.app.core.storage.db.SessionUnreadRow
import com.quantum.app.core.storage.db.SyncCursorDao
import com.quantum.app.core.storage.db.SyncCursorEntity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory
import java.util.concurrent.atomic.AtomicInteger
import org.junit.After
import org.junit.Before
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** 内存 DAO（对齐 Room IGNORE/upsert 语义）。 */
class FakeChatDao : ChatMessageDao {
    val rows = LinkedHashMap<String, ChatMessageEntity>()
    override suspend fun upsertAll(messages: List<ChatMessageEntity>) {
        messages.forEach { rows[it.msgId] = it }
    }
    override suspend fun upsert(message: ChatMessageEntity) {
        rows[message.msgId] = message
    }
    override fun page(limit: Int, offset: Int): Flow<List<ChatMessageEntity>> = flowOf(emptyList())
    override fun sessionPage(sessionId: String, limit: Int, offset: Int): Flow<List<ChatMessageEntity>> =
        flowOf(rows.values.filter { it.sessionId == sessionId }.sortedByDescending { it.seq }.drop(offset).take(limit))
    override suspend fun sessionUnread(sessionId: String, afterSeq: Long): Int =
        rows.values.count { it.sessionId == sessionId && it.direction == 1 && it.seq > afterSeq }

    /** 镜像 SQL 聚合语义：只含未读 ≥ 1 的会话（miss = 0 由调用方补默认）。 */
    override fun unreadCounts(waterlines: Map<String, Long>): Flow<Map<String, Int>> =
        flowOf(
            waterlines.mapValues { (sid, wl) ->
                rows.values.count { it.sessionId == sid && it.direction == 1 && it.seq > wl }
            }.filterValues { it > 0 }
        )

    /** @RawQuery 承载不参与本组用例（SQL 等价性由 core/storage 的 SQLite 直连单测覆盖）。 */
    override fun unreadCountsRaw(query: androidx.sqlite.db.SupportSQLiteQuery): Flow<List<SessionUnreadRow>> =
        flowOf(emptyList())
    override suspend fun sessionMaxSeq(sessionId: String): Long? =
        rows.values.filter { it.sessionId == sessionId }.maxOfOrNull { it.seq }
    override suspend fun botMessagesAfter(afterSeq: Long): List<ChatMessageEntity> =
        rows.values.filter { it.direction == 1 && it.seq > afterSeq }.sortedByDescending { it.seq }
    override suspend fun latest(): ChatMessageEntity? = rows.values.maxByOrNull { it.seq }
    override suspend fun byMsgId(msgId: String): ChatMessageEntity? = rows[msgId]
    override suspend fun byMsgIds(msgIds: List<String>): List<ChatMessageEntity> =
        rows.values.filter { it.msgId in msgIds }
    override suspend fun bySeq(seq: Long): ChatMessageEntity? = rows.values.firstOrNull { it.seq == seq }
    override suspend fun dropPending() = Unit
    /** 镜像 DAO 读改写语义：追加去重后整列覆写（默认 addPickedKey 经 bySeq/updatePickedKeys 亦走到这里）。 */
    override suspend fun updatePickedKeys(seq: Long, keys: String?) {
        rows.values.firstOrNull { it.seq == seq }?.let { rows[it.msgId] = it.copy(pickedKeys = keys) }
    }
    override suspend fun deleteSession(sessionId: String) {
        rows.values.removeAll { it.sessionId == sessionId }
    }
    override suspend fun moveSession(msgId: String, to: String): Int {
        val row = rows[msgId] ?: return 0
        if (row.sessionId == to) {
            return 0
        }
        rows[msgId] = row.copy(sessionId = to)
        return 1
    }
    override suspend fun clear() = rows.clear()
}

/** 内存会话 DAO（advanceAll 走接口默认实现，逻辑即被本组用例覆盖）。 */
class FakeSessionDao : ChatSessionDao {
    val rows = LinkedHashMap<String, ChatSessionEntity>()
    override suspend fun byKeys(keys: List<String>): List<ChatSessionEntity> = keys.mapNotNull { rows[it] }
    override suspend fun upsertAll(sessions: List<ChatSessionEntity>) {
        sessions.forEach { rows[it.sessionKey] = it }
    }
    override fun sessionsWithLast(): Flow<List<SessionLastMessageRow>> = flowOf(emptyList())
    override suspend fun delete(sessionKey: String) {
        rows.remove(sessionKey)
    }
    override suspend fun clear() = rows.clear()
}

class FakeCursorDao : SyncCursorDao {
    var value = 0L
    override suspend fun get(): SyncCursorEntity? = SyncCursorEntity(maxSeq = value)
    override suspend fun set(cursor: SyncCursorEntity) {
        value = cursor.maxSeq
    }
}

class FakeOutboxDao : OutboxDao {
    val items = mutableListOf<OutboxEntity>()
    private var nextId = 1L
    override suspend fun add(entry: OutboxEntity): Long {
        val id = nextId++
        items += entry.copy(id = id)
        return id
    }
    override suspend fun firstPending(): OutboxEntity? = items.firstOrNull { !it.failed }
    override suspend fun markFailed(id: Long) {
        items.replaceAll { if (it.id == id) it.copy(failed = true) else it }
    }
    override suspend fun remove(id: Long) {
        items.removeAll { it.id == id }
    }
    override suspend fun clear() = items.clear()
}

class FakeWs : WsGateway {
    val acks = mutableListOf<String>()
    val commands = mutableListOf<String>()
    val commandSessions = mutableListOf<String>()
    val commandTargets = mutableListOf<String?>()
    var commandAccepted = true

    override fun sendAck(msgId: String): Boolean {
        acks += msgId
        return true
    }
    override fun sendCommand(content: String, session: String, targetTask: String?): Boolean {
        commands += content
        commandSessions += session
        commandTargets += targetTask
        return commandAccepted
    }
}

class FakeSession : SessionGateway {
    override val currentBaseUrl: String = ""
    override fun accessToken(): String? = null
    override suspend fun refresh(): Boolean = false
}

/** 内存已读水位存储（镜像 SessionWaterlineCodec.advance 只进语义；默认会话空串键域）。 */
class FakeReadStore : com.quantum.app.core.storage.prefs.SessionReadStore {
    val map = mutableMapOf<String, Long>()
    override suspend fun readWatermarks(): Map<String, Long> = map.toMap()
    override suspend fun advanceReadWatermark(sessionId: String, seq: Long) {
        if ((map[sessionId] ?: 0L) < seq) {
            map[sessionId] = seq
        }
    }
}

/**
 * 计划 §9.1 服务级单测：seq 分页终止条件（本页条数 < limit）、sync_cursor 持久化、
 * msgId 三来源去重（WS 直推/REST 补拉/再次 WS）、发送链路（WS 优先/REST 兜底/失败标记 outbox）。
 */
class ChatRepositoryTest {

    private lateinit var server: MockWebServer
    private lateinit var repository: ChatRepository
    private lateinit var chatDao: FakeChatDao
    private lateinit var sessionDao: FakeSessionDao
    private lateinit var cursorDao: FakeCursorDao
    private lateinit var outboxDao: FakeOutboxDao
    private lateinit var ws: FakeWs
    private lateinit var readStore: FakeReadStore
    private val requestCount = AtomicInteger(0)

    private fun messageJson(seq: Int, msgId: String, direction: Int = 1, content: String = "c$seq") =
        """{"Id":"id$seq","Seq":$seq,"Direction":$direction,"Content":"$content","ContentType":"text","MsgId":"$msgId","Status":2,"CreateTime":"2026-09-14 10:00:00"}"""

    @Before
    fun setUp() {
        server = MockWebServer()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                requestCount.incrementAndGet()
                if (request.path!!.startsWith("/api/App/messages")) {
                    return MockResponse().setHeader("Content-Type", "application/json")
                        .setBody("""{"Code":200,"Message":"Success","Data":{"MaxSeq":0,"Data":[]}}""")
                }
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Message":"Success","Data":{"Accepted":true,"Message":"ok"}}""")
            }
        }
        server.start()
        val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
        val api = Retrofit.Builder()
            .baseUrl(server.url("/"))
            .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
            .build()
            .create(AppApi::class.java)
        chatDao = FakeChatDao()
        sessionDao = FakeSessionDao()
        cursorDao = FakeCursorDao()
        outboxDao = FakeOutboxDao()
        ws = FakeWs()
        readStore = FakeReadStore()
        repository = ChatRepository(api, ws, FakeSession(), chatDao, sessionDao, cursorDao, outboxDao, readStore)
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    @Test
    fun syncAll_PaginatesUntilShortPage_AndPersistsCursor() = runBlocking {
        val page1 = (1..ChatRepository.SYNC_PAGE_SIZE).joinToString(",") { i ->
            messageJson(i, "m$i")
        }
        val page2 = (501..700).joinToString(",") { i ->
            messageJson(i, "m$i")
        }
        val responses = listOf(
            """{"Code":200,"Data":{"MaxSeq":500,"Data":[$page1]}}""",
            """{"Code":200,"Data":{"MaxSeq":700,"Data":[$page2]}}"""
        ).map {
            MockResponse().setHeader("Content-Type", "application/json").setBody(it)
        }
        server.dispatcher = object : Dispatcher() {
            private val index = AtomicInteger(0)
            override fun dispatch(request: RecordedRequest): MockResponse =
                responses.getOrNull(index.getAndIncrement())
                    ?: MockResponse().setHeader("Content-Type", "application/json").setBody("""{"Code":500,"Message":"over"}""")
        }

        val cursor = repository.syncAll()

        assertEquals(700L, cursor)
        assertEquals(700L, cursorDao.value, "游标必须持久化（sync_cursor）")
        assertEquals(700, chatDao.rows.size, "两条页全部入库")
    }

    /**
     * 富交互本地标记（pickLabel/pickedKeys）不被服务端重推抹掉（2026-09-19 发布实测回归）：
     * 同一条回显行会经「echo 轮询/WS 首达（带标记）→ 全局同步重推（无标记实体）」先后 upsert，
     * 重推实体不带本地列，直接覆盖会把点选系统提示与已选徽标抹掉——落库前必须按 msgId 合并本地值。
     */
    @Test
    fun pickMarks_SurviveServerRepush() = runBlocking {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                val path = request.path ?: ""
                val body = when {
                    // 发送后 echo 轮询：拉回「接收」回显行（服务端行，无本地列）
                    path.startsWith("/api/App/session/messages") ->
                        """{"Code":200,"Data":{"HasMore":false,"Messages":[""" +
                            """{"Id":"i901","Seq":901,"Direction":2,"Content":"magnet:x","ContentType":"text","MsgId":"e1","Status":3,"CreateTime":"t","SessionKey":"bt"}]}}"""
                    // 全局同步：选项卡片行 + 同 msgId 回显行重推（均无本地列）
                    path.startsWith("/api/App/messages") ->
                        """{"Code":200,"Data":{"MaxSeq":901,"Data":[""" +
                            """{"Id":"i900","Seq":900,"Direction":1,"Content":"磁力卡片","ContentType":"text","MsgId":"m900","Status":2,"CreateTime":"t"}, """ +
                            """{"Id":"i901","Seq":901,"Direction":2,"Content":"magnet:x","ContentType":"text","MsgId":"e1","Status":3,"CreateTime":"t"}]}}"""
                    else -> """{"Code":200,"Data":{"Accepted":true,"Message":"ok"}}"""
                }
                return MockResponse().setHeader("Content-Type", "application/json").setBody(body)
            }
        }

        // 1) 点选代发：回显行经 echo 轮询落库，pendingPick 命中打上 pickLabel（系统提示标记）
        assertNull(repository.sendCommand("magnet:x", sessionId = "bt", targetTask = "bt-task", pickLabel = "1. 标题A"))
        assertEquals("1. 标题A", chatDao.rows["e1"]?.pickLabel, "回显行应打上点选标记")

        // 2) 选项卡片行入库后写入已选徽标列（本地 UPDATE）
        repository.syncAll()
        repository.addPickedKey(900, "magnet:x")
        assertTrue(chatDao.rows["m900"]?.pickedKeys.orEmpty().contains("magnet:x"), "已选徽标列应写入")

        // 3) 服务端重推同一批行（实体不带本地列）→ 两列标记必须保留
        repository.syncAll()
        assertEquals("1. 标题A", chatDao.rows["e1"]?.pickLabel, "重推不得抹掉 pickLabel")
        assertTrue(chatDao.rows["m900"]?.pickedKeys.orEmpty().contains("magnet:x"), "重推不得抹掉 pickedKeys")
    }

    @Test
    fun syncAll_EmptyPage_DoesNotAdvanceCursor() = runBlocking {
        cursorDao.value = 42L
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/App/messages")) {
                    requestCount.incrementAndGet()
                    return MockResponse().setHeader("Content-Type", "application/json")
                        .setBody("""{"Code":200,"Data":{"MaxSeq":42,"Data":[]}}""")
                }
                // 尾部水位交换（sessions/overview）：空响应即可，不算同步请求
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"MaxSeq":42,"Sessions":[]}}""")
            }
        }

        val cursor = repository.syncAll()

        // 空页不推进游标：服务端契约保证空页回原游标，客户端也绝不把空页的 MaxSeq 写进游标
        // （对端旧版本会回落服务端 MAX，写进去就跳过了并发落库、本页未返回的那条消息）
        assertEquals(42L, cursor)
        assertEquals(42L, cursorDao.value)
        assertEquals(1, requestCount.get(), "空页应立即结束翻页，不做多余同步请求（水位交换不计入）")
    }

    @Test
    fun onWsNotify_UsesServerSeq_AndEntersConversationFlow() = runBlocking {
        repository.onWsNotify(
            WsFrame(
                type = "notify", msgId = "n1", seq = 77, category = "task",
                title = "任务失败", content = "备份任务执行失败", createTime = "2026-09-17 10:00:00"
            )
        )

        val row = chatDao.rows["n1"]
        assertNotNull(row)
        assertEquals(77L, row.seq, "必须按服务端镜像行真实 seq 落库（自造 seq 会撞主键覆写真实消息）")
        assertEquals(1, row.direction, "通知以机器人下发方向进会话流")
        assertEquals("【任务失败】\n备份任务执行失败", row.content)
        assertEquals(ChatRepository.NOTIFY_CONTENT_TYPE, row.contentType)
        assertEquals("2026-09-17 10:00:00", row.createTime, "时间取服务端，不用本机时间")
    }

    @Test
    fun onWsNotify_WithoutSeq_FallsBackToRestSyncWithoutFabricating() = runBlocking {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                requestCount.incrementAndGet()
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"MaxSeq":12,"Data":[${messageJson(12, "n2", content = "【系统公告】\\n维护")}]}}""")
            }
        }

        // 旧服务端帧无 seq：不得自造本地 seq，应触发 REST 补拉拿回真实行
        repository.onWsNotify(WsFrame(type = "notify", msgId = "n2", title = "系统公告", content = "维护"))

        assertTrue(requestCount.get() >= 1, "帧缺 seq 应触发 REST 补拉")
        assertEquals(1, chatDao.rows.size)
        assertEquals(12L, chatDao.rows["n2"]?.seq, "入库 seq 来自服务端补拉，而非本地伪造")
    }

    @Test
    fun onWsMessage_frameWithSession_rowLandsInThatSession() = runBlocking {
        // 任务脚本会话：message 帧携带 session → 落库行归对应会话
        repository.onWsMessage(
            WsFrame(type = "message", msgId = "s1", seq = 30, content = "电影港更新", session = "task-9",
                createTime = "2026-09-18 10:00:00")
        )

        assertEquals("task-9", chatDao.rows["s1"]?.sessionId)
    }

    @Test
    fun onWsMessage_frameWithPayload_rowCarriesPayload() = runBlocking {
        // 富交互：message 帧携带 payload（可点选项/封面 JSON）→ 原文落库，渲染层再解析
        repository.onWsMessage(
            WsFrame(type = "message", msgId = "p1", seq = 33, content = "搜索结果",
                payload = """{"options":[{"key":"3","label":"3. 晴天","reply":"3"}]}""",
                createTime = "2026-09-18 11:00:00")
        )

        val row = chatDao.rows["p1"]
        assertNotNull(row)
        assertTrue(row.payload.orEmpty().contains("\"options\""), "payload 应原文落库供渲染层解析")
    }

    @Test
    fun deleteSession_RemovesLocalSessionAndRows_AndRewindsCursor() = runBlocking {
        // 删除会话（2026-09-21 日志删除逻辑调整）：服务端只删会话实体（消息记录保留）→
        // 本地删会话行与消息行（窗口/列表显示清除）+ 游标回拨 min(当前, MaxSeq)
        //（消息不删后 MaxSeq 恒为当前全局最大，回拨多为 no-op，防御性保留）
        repository.onWsMessage(
            WsFrame(type = "message", msgId = "d1", seq = 100, content = "x", session = "task-5",
                createTime = "2026-09-18 12:00:00")
        )
        cursorDao.value = 100L
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/App/session/delete")) {
                    assertEquals("task-5", request.requestUrl?.queryParameter("session"))
                    return MockResponse().setHeader("Content-Type", "application/json")
                        .setBody("""{"Code":200,"Data":{"Deleted":true,"MaxSeq":40}}""")
                }
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"MaxSeq":40,"Data":[]}}""")
            }
        }

        repository.deleteSession("task-5")

        assertEquals(0, chatDao.rows.size, "该会话本地消息行应全部删除（窗口清空）")
        assertNull(sessionDao.rows["task-5"], "会话行应随删除移除（列表不再显示）")
        assertEquals(40L, cursorDao.value, "游标回拨 minOf(当前, 返回 MaxSeq)")
    }

    @Test
    fun deleteSession_CursorAlreadyBelowMaxSeq_StaysPut() = runBlocking {
        // 服务端消息保留后 MaxSeq 恒为当前全局最大：本地游标不高于它时回拨为 no-op
        //（不会把游标往上推——只回拨不下推，避免跳过尚未同步的其他会话行）
        cursorDao.value = 30L
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/App/session/delete")) {
                    return MockResponse().setHeader("Content-Type", "application/json")
                        .setBody("""{"Code":200,"Data":{"Deleted":true,"MaxSeq":100}}""")
                }
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"MaxSeq":100,"Data":[]}}""")
            }
        }

        repository.deleteSession("task-7")

        assertEquals(30L, cursorDao.value, "游标不被 MaxSeq 抬升")
    }

    @Test
    fun messageUpsert_AdvancesSessionRow() = runBlocking {
        // 消息落库统一推进会话行：不存在建行（lastSeq=本会话最大 seq）、存在则 lastSeq 取大
        repository.onWsMessage(
            WsFrame(type = "message", msgId = "sa1", seq = 10, content = "a", session = "task-3",
                createTime = "2026-09-18 10:00:00")
        )
        repository.onWsMessage(
            WsFrame(type = "message", msgId = "sa2", seq = 12, content = "b", session = "task-3",
                createTime = "2026-09-18 10:01:00")
        )
        // 乱序补拉旧消息（seq 更小）：lastSeq 不回退
        repository.onWsMessage(
            WsFrame(type = "message", msgId = "sa0", seq = 8, content = "old", session = "task-3",
                createTime = "2026-09-18 09:59:00")
        )
        // 无 session 帧归默认会话（sessionKey 归一空串）
        repository.onWsMessage(
            WsFrame(type = "message", msgId = "sa3", seq = 13, content = "c", createTime = "2026-09-18 10:02:00")
        )

        val row = sessionDao.rows["task-3"]
        assertNotNull(row)
        assertEquals(12L, row.lastSeq, "lastSeq 取本会话已见最大 seq（乱序旧消息不回退）")
        assertNotNull(sessionDao.rows[""], "无 session 帧归默认会话（sessionKey 空串）")
        assertEquals(13L, sessionDao.rows[""]?.lastSeq)
    }

    @Test
    fun onWsNotify_taskNotifyWithSession_rowCarriesSession() = runBlocking {
        // 任务生命周期通知：notify 帧携带 session → 镜像行归对应任务会话；无 session 归默认
        repository.onWsNotify(
            WsFrame(type = "notify", msgId = "sn1", seq = 31, title = "任务通知", content = "执行完成",
                session = "task-9", createTime = "2026-09-18 10:01:00")
        )
        repository.onWsNotify(
            WsFrame(type = "notify", msgId = "sn2", seq = 32, title = "登录提醒", content = "新设备",
                createTime = "2026-09-18 10:02:00")
        )

        assertEquals("task-9", chatDao.rows["sn1"]?.sessionId)
        assertEquals("", chatDao.rows["sn2"]?.sessionId, "无 session 帧归默认会话（空串）")
    }

    @Test
    fun syncAll_restDtoSessionKey_mapsToSessionId() = runBlocking {
        // REST 补拉行带 SessionKey（PascalCase）→ 实体 sessionId；缺省归默认会话
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                requestCount.incrementAndGet()
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody(
                        """{"Code":200,"Data":{"MaxSeq":41,"Data":[""" +
                            """{"Id":"a","Seq":40,"Direction":1,"Content":"x","ContentType":"text","MsgId":"r1","Status":2,"CreateTime":"t","SessionKey":"task-7"},""" +
                            """{"Id":"b","Seq":41,"Direction":1,"Content":"y","ContentType":"text","MsgId":"r2","Status":2,"CreateTime":"t"}""" +
                            """]}}"""
                    )
            }
        }

        repository.syncAll()

        assertEquals("task-7", chatDao.rows["r1"]?.sessionId)
        assertEquals("", chatDao.rows["r2"]?.sessionId, "旧服务端/默认会话行缺 SessionKey → 空串")
    }

    @Test
    fun msgId_DedupesAcrossThreeSources() = runBlocking {
        // 来源 1：WS 直推
        repository.onWsMessage(WsFrame(type = "message", msgId = "m1", seq = 9, content = "hello", contentType = "text", createTime = "2026-09-14 10:00:00"))
        assertEquals(1, chatDao.rows.size, "仅一行")
        assertEquals(1, ws.acks.size, "WS message 帧需 ack")

        // 来源 2：REST 补拉含同一 msgId（seq/内容相同）→ 不重复
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"MaxSeq":9,"Data":[${messageJson(9, "m1")}]}}""")
        }
        repository.syncAll()
        assertEquals(1, chatDao.rows.size, "REST 补拉同 msgId 不应产生第二行")

        // 来源 3：WS 再次推送同 msgId → 仍单行（幂等 ack 照发）
        repository.onWsMessage(WsFrame(type = "message", msgId = "m1", seq = 9, content = "hello", contentType = "text", createTime = "2026-09-14 10:00:00"))
        assertEquals(1, chatDao.rows.size, "再次 WS 同 msgId 仍单行")
        assertEquals(2, ws.acks.size, "幂等 ack 照发")
    }

    @Test
    fun sendCommand_UsesWsFirst_RemovesOutbox_OnSuccess() = runBlocking {
        ws.commandAccepted = true
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                // 2.5：WS 发送成功后不再全量 syncAll，改按会话增量拉取（session/messages 第 1 页）
                if (request.path!!.startsWith("/api/App/session/messages")) {
                    return MockResponse().setHeader("Content-Type", "application/json")
                        .setBody("""{"Code":200,"Data":{"HasMore":false,"Messages":[${messageJson(1, "e1", direction = 2)}]}}""")
                }
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Message":"Success","Data":{"MaxSeq":0,"Data":[]}}""")
            }
        }

        val failure = repository.sendCommand("查看任务")

        assertEquals(null, failure)
        assertEquals(listOf("查看任务"), ws.commands)
        assertTrue(outboxDao.items.isEmpty(), "成功后 outbox 应清空")
        assertEquals(2, chatDao.rows["e1"]?.direction, "echo 接收行应经按会话增量拉取落库（右侧气泡）")
    }

    @Test
    fun sendCommand_FallsBackToRest_WhenWsUnavailable() = runBlocking {
        ws.commandAccepted = false

        val failure = repository.sendCommand("你好")

        assertEquals(null, failure)
        val commandRequests = server.requestCount
        assertTrue(commandRequests >= 1, "WS 不可用时应走 REST 兜底")
        val recorded = server.takeRequest()
        assertEquals("/api/App/command", recorded.path)
        // kotlinx 默认不编码等于默认值的字段：ContentType=text 缺省由服务端兜底，附件场景显式传 image/file
        assertTrue(recorded.body.readUtf8().contains("你好"), "REST 兜底应携带指令内容")
        // 2.5：REST 接受后单次按会话增量拉取 echo（接收行必已落库，不再全局 syncAll）
        val echoPull = server.takeRequest()
        assertTrue(echoPull.path!!.startsWith("/api/App/session/messages"), "REST 路径成功后应按会话拉取 echo")
    }

    @Test
    fun sendCommand_RateLimited_MarksOutboxFailed() = runBlocking {
        ws.commandAccepted = false
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":500,"Message":"请求频繁，请稍后重试！"}""")
        }

        val failure = repository.sendCommand("稍后重试的指令")

        assertNotNull(failure, "限流失败应返回失败文案")
        assertEquals(1, outboxDao.items.size, "outbox 保留失败项")
        assertTrue(outboxDao.items[0].failed, "失败指令应标记 outbox 可重发")
    }

    @Test
    fun sendCommand_PassesTargetTask_OnWsPath() = runBlocking {
        // 点选代发透传来源任务 Id：WS command 帧带 targetTask（合并会话内点选精确路由）
        ws.commandAccepted = true
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"HasMore":false,"Messages":[]}}""")
        }

        repository.sendCommand("3", sessionId = "dygangs", targetTask = "task-9")

        assertEquals(listOf<String?>("task-9"), ws.commandTargets, "WS 路径应透传 targetTask")
    }

    @Test
    fun sendCommand_RestFallback_CarriesTargetTaskId() = runBlocking {
        // REST 兜底同样携带 TargetTaskId（PascalCase 显式标注）
        ws.commandAccepted = false
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"Accepted":true,"Message":"ok"}}""")
        }

        repository.sendCommand("3", sessionId = "dygangs", targetTask = "task-9")

        val recorded = server.takeRequest()
        assertEquals("/api/App/command", recorded.path)
        assertTrue(recorded.body.readUtf8().contains("\"TargetTaskId\":\"task-9\""), "REST 兜底应携带 TargetTaskId")
    }

    // ==================================================================== 发送后 echo 轮询降级（2.5）

    @Test
    fun sendCommand_WsEchoPoll_FetchesSessionFirstPage_FiltersBySeqBeforeSend_StopsOnEcho() = runBlocking {
        // 预置：本会话已有 seq 40 的旧消息（发送前水位），全局游标停在 50（不得被动）
        chatDao.upsert(
            ChatMessageEntity(seq = 40, msgId = "old1", direction = 1, content = "旧", contentType = "text", createTime = "t", sessionId = "task-9")
        )
        cursorDao.value = 50L
        var sessionRequests = 0
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/App/session/messages")) {
                    sessionRequests++
                    assertEquals("task-9", request.requestUrl?.queryParameter("session"))
                    assertEquals("200", request.requestUrl?.queryParameter("limit"), "单会话分页端点 pageSize ≤ 200")
                    // 第 1 页（升序返回）：旧行 seq 40（≤ 水位，应被本地过滤）+ echo 接收行 seq 51
                    return MockResponse().setHeader("Content-Type", "application/json")
                        .setBody(
                            """{"Code":200,"Data":{"HasMore":true,"Messages":[""" +
                                messageJson(40, "old1") + "," +
                                messageJson(51, "e51", direction = 2) + "]}}"
                        )
                }
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Message":"Success","Data":{"MaxSeq":0,"Data":[]}}""")
            }
        }
        ws.commandAccepted = true

        val failure = repository.sendCommand("hello", sessionId = "task-9")

        assertEquals(null, failure)
        assertEquals(1, sessionRequests, "首层轮询即命中接收行（seq > 发送前水位），应立即停止")
        assertEquals(2, chatDao.rows["e51"]?.direction, "echo 接收行应落库渲染为右侧气泡")
        assertEquals(50L, cursorDao.value, "按会话增量拉取不得读写全局同步游标")
        assertTrue(outboxDao.items.isEmpty())
    }

    @Test
    fun sendCommand_WsEchoPoll_ExhaustsThreeAttempts_NeverCallsGlobalSync() = runBlocking {
        // echo 一直未到（服务端处理慢/丢帧）：最多 3 次（250/500/1000ms 递增）按会话分页第 1 页，
        // 绝不发 500/页的全局 syncAll；游标不回拨不推进，超时留给重连/回前台兜底补拉
        var sessionRequests = 0
        var globalSyncRequests = 0
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/App/session/messages")) {
                    sessionRequests++
                    return MockResponse().setHeader("Content-Type", "application/json")
                        .setBody("""{"Code":200,"Data":{"HasMore":false,"Messages":[]}}""")
                }
                if (request.path!!.startsWith("/api/App/messages")) {
                    globalSyncRequests++
                }
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Message":"Success","Data":{"MaxSeq":0,"Data":[]}}""")
            }
        }
        ws.commandAccepted = true
        cursorDao.value = 42L

        val failure = repository.sendCommand("慢指令")

        assertEquals(null, failure, "轮询扑空不算发送失败（outbox 照常清理，靠 WS/回前台兜底）")
        assertEquals(3, sessionRequests, "兜底轮询上限 3 次")
        assertEquals(0, globalSyncRequests, "echo 找回不得退化为全局全量同步")
        assertEquals(42L, cursorDao.value)
    }

    // ==================================================================== 跨会话触发消息迁移（2026-09-18 批次）

    @Test
    fun onWsMessageMoved_UpdatesRowAdvancesTargetSession_EmitsEvent() = runBlocking {
        // 默认会话的用户气泡（from 缺省）迁任务会话：行搬移 + 目标会话行推进 + 发切换事件
        chatDao.upsert(
            ChatMessageEntity(seq = 5, msgId = "m5", direction = 2, content = "随机密码-1", contentType = "text", createTime = "2026-09-18 10:00:00", sessionId = "")
        )
        val moves = mutableListOf<ChatRepository.SessionMoveEvent>()
        val job = launch(Dispatchers.Unconfined) { repository.sessionMoves.collect { moves += it } }

        repository.onWsMessageMoved(WsFrame(type = "message_moved", msgId = "m5", seq = 5, to = "task-pwd"))

        job.cancel()
        assertEquals("task-pwd", chatDao.rows["m5"]?.sessionId, "行应搬到目标会话")
        assertEquals(5L, sessionDao.rows["task-pwd"]?.lastSeq, "目标会话行建行/推进（本地会话列表聚合源）")
        assertEquals(listOf(ChatRepository.SessionMoveEvent(from = "", to = "task-pwd")), moves)
    }

    @Test
    fun onWsMessageMoved_RowAbsent_RemembersMove_EchoLandsInTargetSession() = runBlocking {
        // 乱序防御：迁移帧先到（行不在本地，DAO 更新 0 行），随后 echo（session 仍是来源）到达——
        // movesMap 覆盖落库会话键，不能把已迁移行 REPLACE 回旧会话
        repository.onWsMessageMoved(WsFrame(type = "message_moved", msgId = "m5", seq = 5, to = "task-pwd"))

        repository.onWsMessage(
            WsFrame(type = "message", msgId = "m5", seq = 5, content = "随机密码-1", contentType = "text", direction = 2, session = "")
        )

        assertEquals("task-pwd", chatDao.rows["m5"]?.sessionId, "echo 落行应按 movesMap 覆盖为迁移目标会话")
    }

    @Test
    fun onWsMessageMoved_FromEqualsTo_Ignored() = runBlocking {
        chatDao.upsert(
            ChatMessageEntity(seq = 5, msgId = "m5", direction = 2, content = "c", contentType = "text", createTime = "t", sessionId = "task-pwd")
        )
        val moves = mutableListOf<ChatRepository.SessionMoveEvent>()
        val job = launch(Dispatchers.Unconfined) { repository.sessionMoves.collect { moves += it } }

        repository.onWsMessageMoved(WsFrame(type = "message_moved", msgId = "m5", seq = 5, from = "task-pwd", to = "task-pwd"))

        job.cancel()
        assertTrue(moves.isEmpty(), "from === to 应忽略")
        assertEquals("task-pwd", chatDao.rows["m5"]?.sessionId)
    }

    // ==================================================================== §4-4 同步并发/缺 seq 帧 ack

    @Test
    fun onWsMessage_WithoutSeq_AcksFrameAfterRestSync() = runBlocking {
        // §4-4：帧缺 seq → REST 补拉取回真实行后必须 ack，否则服务端按未确认一直重投同一条
        repository.onWsMessage(WsFrame(type = "message", msgId = "ns1", content = "无 seq 帧", createTime = "t"))
        assertTrue(ws.acks.contains("ns1"), "缺 seq 帧补拉后应 ack（防服务端重投）")
    }

    @Test
    fun syncAll_ConcurrentCalls_SerializeToConsistentCursor() = runBlocking {
        // §4-4：登录+重连几乎同时各发一次 syncAll，Mutex 保证同实例串行、收敛到一致游标，
        // 交错读「cursor→翻页→写回」不再重复整轮补拉或竞写共享游标
        val fullPage = (1..ChatRepository.SYNC_PAGE_SIZE).joinToString(",") { i -> messageJson(i, "cc$i") }
        var index = 0
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                val body = if (index++ == 0) {
                    """{"Code":200,"Data":{"MaxSeq":${ChatRepository.SYNC_PAGE_SIZE},"Data":[$fullPage]}}"""
                } else {
                    """{"Code":200,"Data":{"MaxSeq":${ChatRepository.SYNC_PAGE_SIZE},"Data":[]}}"""
                }
                return MockResponse().setHeader("Content-Type", "application/json").setBody(body)
            }
        }

        val a = launch { repository.syncAll() }
        val b = launch { repository.syncAll() }
        a.join()
        b.join()

        assertEquals(500L, cursorDao.value, "并发同步收敛到同一游标")
        assertEquals(500, chatDao.rows.size, "两次同步不应产生重复行（msgId 幂等）")
    }

    // ==================================================================== 会话已读水位双端同步（2026-09-21 批次）

    @Test
    fun onSessionRead_NormalizesKey_AndAdvancesOnlyForward() = runBlocking {
        // 默认会话：session 缺省 → 空串键域
        repository.onSessionRead(null, 7)
        repository.onSessionRead("", 5) // 回退帧忽略（服务端只发递增值，防御旧/异常服务端）
        assertEquals(7L, readStore.map[""])

        // 会话键 trim 归一；重复/回退不推进
        repository.onSessionRead(" task-a ", 3)
        repository.onSessionRead("task-a", 2)
        assertEquals(3L, readStore.map["task-a"])

        // 无效帧（seq 缺失/非正数）整体忽略
        repository.onSessionRead("task-b", null)
        repository.onSessionRead("task-b", 0)
        assertNull(readStore.map["task-b"])
    }

    @Test
    fun markSessionRead_PostsPerSessionReadSeqs() = runBlocking {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                assertEquals("/api/App/sessions/read", request.path)
                val body = request.body.readUtf8()
                assertTrue(body.contains("\"ReadSeqs\""), "请求体应为 {ReadSeqs:{会话键:水位}}")
                assertTrue(body.contains("\"task-a\"") && body.contains("7"))
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"Advanced":[{"Session":"task-a","Seq":7}]}}""")
            }
        }

        repository.markSessionRead("task-a", 7)
    }

    @Test
    fun syncAll_ExchangesReadWatermarks_ReportsLocalAbsorbsAuthoritative() = runBlocking {
        // 本地水位（键域 = 会话键，默认会话空串——Flow 解码域，不读 DataStore 原始 "_" 串）
        readStore.map["task-a"] = 5
        readStore.map[""] = 2
        var overviewBody: String? = null
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/App/sessions/overview")) {
                    overviewBody = request.body.readUtf8()
                    // 服务端权威水位：other=9 待吸收；task-a=5 与本地持平不重复写；默认会话 1 低于本地 2 不回拨
                    return MockResponse().setHeader("Content-Type", "application/json")
                        .setBody(
                            """{"Code":200,"Data":{"MaxSeq":9,"Sessions":[""" +
                                """{"SessionKey":"task-a","Total":1,"Unread":0,"ReadSeq":5,"Last":null},""" +
                                """{"SessionKey":"other","Total":2,"Unread":0,"ReadSeq":9,"Last":null},""" +
                                """{"SessionKey":"","Total":0,"Unread":0,"ReadSeq":1,"Last":null}]}}"""
                        )
                }
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"MaxSeq":9,"Data":[]}}""")
            }
        }

        repository.syncAll()

        // 上报：本地全部会话水位随 overview 请求体送出（含默认会话空串键）
        assertTrue(overviewBody.orEmpty().contains("\"task-a\":5"))
        assertTrue(overviewBody.orEmpty().contains("\"\":2"))
        // 吸收：只进合并——新权威水位推进、持平/更低不动
        assertEquals(5L, readStore.map["task-a"])
        assertEquals(9L, readStore.map["other"])
        assertEquals(2L, readStore.map[""])
    }

    @Test
    fun syncAll_WatermarkExchangeFailure_DoesNotBreakSync() = runBlocking {
        // 水位交换失败（旧服务端 404→信封 500）runCatching 静默：游标与消息入库不受影响
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().setHeader("Content-Type", "application/json")
                    .setBody(
                        if (request.path!!.startsWith("/api/App/messages")) {
                            """{"Code":200,"Data":{"MaxSeq":3,"Data":[${messageJson(3, "wm3")}]}}"""
                        } else {
                            """{"Code":500,"Message":"sessions/overview 不可用"}"""
                        }
                    )
        }

        val cursor = repository.syncAll()

        assertEquals(3L, cursor)
        assertEquals(3L, cursorDao.value)
        assertNotNull(chatDao.rows["wm3"])
        Unit
    }

    @Test
    fun normalizeSessionRead_PureFunction_KeyAndSeqGuards() {
        // 帧字段 → 动作参数的纯函数语义：null/空白键归默认会话、trim、非正数 seq 无效
        assertEquals("" to 7L, ChatRepository.normalizeSessionRead(null, 7))
        assertEquals("" to 7L, ChatRepository.normalizeSessionRead("  ", 7))
        assertEquals("task-a" to 3L, ChatRepository.normalizeSessionRead(" task-a ", 3))
        assertNull(ChatRepository.normalizeSessionRead("task-a", null))
        assertNull(ChatRepository.normalizeSessionRead("task-a", 0))
        assertNull(ChatRepository.normalizeSessionRead("task-a", -1))
    }

    @Test
    fun absorbedReadWatermarks_PureFunction_OnlyNewerSurvives() {
        val local = mapOf("task-a" to 5L, "" to 2L)
        val absorbed = ChatRepository.absorbedReadWatermarks(
            local,
            listOf("task-a" to 5L, "task-a" to 8L, "" to 1L, "other" to 9L)
        )
        // 持平不吸收、只取更新、本地超前不回拨、新会话照收
        assertEquals(listOf("task-a" to 8L, "other" to 9L), absorbed)
    }
}
