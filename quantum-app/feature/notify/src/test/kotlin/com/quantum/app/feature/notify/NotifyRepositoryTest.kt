package com.quantum.app.feature.notify

import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.storage.db.NotificationDao
import com.quantum.app.core.storage.db.NotificationEntity
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.After
import org.junit.Before
import org.junit.Test
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** 内存通知 DAO：镜像 Room IGNORE / msgId IN 语义，并记录批量/单条插入调用数。 */
class FakeNotificationDao : NotificationDao {
    val rows = LinkedHashMap<String, NotificationEntity>()
    var insertAllCalls = 0
    var insertSingleCalls = 0

    override suspend fun insertIfAbsent(notification: NotificationEntity): Long {
        insertSingleCalls++
        return if (rows.containsKey(notification.msgId)) {
            -1L
        } else {
            rows[notification.msgId] = notification
            rows.size.toLong()
        }
    }

    override suspend fun insertAllIfAbsent(notifications: List<NotificationEntity>): List<Long> {
        insertAllCalls++
        return notifications.map { n ->
            if (rows.containsKey(n.msgId)) {
                -1L
            } else {
                rows[n.msgId] = n
                rows.size.toLong()
            }
        }
    }

    override fun byMsgIds(ids: List<String>): Flow<List<NotificationEntity>> =
        flowOf(ids.mapNotNull { rows[it] })

    override fun page(limit: Int, offset: Int): Flow<List<NotificationEntity>> =
        flowOf(rows.values.sortedByDescending { it.createdAt }.drop(offset).take(limit))

    override fun all(): Flow<List<NotificationEntity>> = flowOf(rows.values.toList())

    override fun unreadCount(): Flow<Int> = flowOf(rows.values.count { it.readAt == null })

    override suspend fun markRead(msgId: String, readAt: String) {
        rows[msgId]?.let { rows[msgId] = it.copy(readAt = readAt) }
    }

    override suspend fun markAllRead(readAt: String) {
        rows.keys.toList().forEach { key -> rows[key] = rows.getValue(key).copy(readAt = readAt) }
    }

    override suspend fun clear() = rows.clear()
}

/**
 * 2.3 通知同步批插：syncFromServer 一次列表拉取只走**一次**批量 INSERT OR IGNORE
 * （单事务、Room 表只失效一次），新增计数只算真正插入的行；msgId 缺省回退 Id。
 */
class NotifyRepositoryTest {

    private lateinit var server: MockWebServer
    private lateinit var dao: FakeNotificationDao
    private lateinit var repository: NotifyRepository

    @Before
    fun setUp() {
        server = MockWebServer()
        server.start()
        val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
        val api = Retrofit.Builder()
            .baseUrl(server.url("/"))
            .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
            .build()
            .create(AppApi::class.java)
        dao = FakeNotificationDao()
        repository = NotifyRepository(api, dao)
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    private fun dtoJson(id: String, msgIdJson: String = "\"m-$id\"", title: String = "标题$id") =
        """{"Id":"$id","Title":"$title","Content":"内容$id","Category":"system","Jump":null,"MsgId":$msgIdJson,"CreatedAt":"2026-09-18 10:00:00","ReadAt":null}"""

    @Test
    fun syncFromServer_BatchInsertsOnce_AndCountsOnlyNewRows() = runBlocking {
        // 预置一条已存在（WS/厂商推送双通道先落过）；syncFromServer 的单条插入口径不再被同步链路使用
        dao.insertIfAbsent(
            NotificationEntity(msgId = "m-a", title = "旧通知", content = "旧", createdAt = "2026-09-17 09:00:00")
        )
        dao.insertSingleCalls = 0
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                assertTrue(request.path!!.startsWith("/api/AppNotification/list"))
                assertEquals("1", request.requestUrl?.queryParameter("page"))
                assertEquals("100", request.requestUrl?.queryParameter("pageSize"))
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody(
                        """{"Code":200,"Message":"Success","Data":{"TotalCount":3,"Page":1,"PageSize":100,"Data":[""" +
                            dtoJson("a") + "," + dtoJson("b") + "," + dtoJson("c") + "]}}"
                    )
            }
        }

        val added = repository.syncFromServer()

        assertEquals(2, added, "新增只算真正插入的行（m-a 已存在被 IGNORE）")
        assertEquals(1, dao.insertAllCalls, "一次同步只允许一次批量插入（单事务，Room 只失效一次）")
        assertEquals(0, dao.insertSingleCalls, "同步链路不得退回逐条独立事务插入")
        assertEquals(3, dao.rows.size)
        assertEquals("标题b", dao.rows["m-b"]?.title)
    }

    @Test
    fun syncFromServer_MsgIdMissing_FallsBackToDtoId() = runBlocking {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"Data":[""" + dtoJson("legacy", msgIdJson = "null") + "],\"TotalCount\":1,\"Page\":1,\"PageSize\":100}}")
        }

        val added = repository.syncFromServer()

        assertEquals(1, added)
        assertTrue(dao.rows.containsKey("legacy"), "旧服务端无 MsgId 时以 Id 作幂等键")
    }

    @Test
    fun syncFromServer_EmptyPage_QueriesNothing() = runBlocking {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":200,"Data":{"Data":[],"TotalCount":0,"Page":1,"PageSize":100}}""")
        }

        val added = repository.syncFromServer()

        assertEquals(0, added)
        assertEquals(0, dao.insertAllCalls, "空批次直接返回，不惊动 Room")
        assertTrue(dao.rows.isEmpty())
    }
}
