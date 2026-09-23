package com.quantum.app.core.network

import com.quantum.app.core.network.session.RefreshResult
import com.quantum.app.core.network.session.RawAuthHttp
import com.quantum.app.core.network.session.SessionManager
import com.quantum.app.core.storage.prefs.TokenStoreApi
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.After
import org.junit.Before
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/** 内存版 TokenStore（JVM 单测用）。 */
open class FakeTokenStore : TokenStoreApi {
    var accessTokenValue: String? = null
    var refreshTokenValue: String? = "r-old"
    var cleared = false
    val savedTokens = mutableListOf<Triple<String, String, Long>>()

    override val baseUrl: Flow<String?> get() = emptyFlow()
    override val accessToken: Flow<String?> get() = emptyFlow()
    override val loggedIn: Flow<Boolean> get() = emptyFlow()
    override val isManager: Flow<Boolean> get() = emptyFlow()
    override val userName: Flow<String?> get() = emptyFlow()

    override suspend fun currentBaseUrl(): String? = null
    override suspend fun currentAccessToken(): String? = accessTokenValue
    override suspend fun currentRefreshToken(): String? = refreshTokenValue
    override suspend fun currentDeviceId(): String = "dev-test"
    override suspend fun currentAccessExpiresAt(): Long = 0L
    override suspend fun currentRefreshExpiresAt(): Long = Long.MAX_VALUE

    override suspend fun saveBaseUrl(url: String) = Unit
    override suspend fun saveLogin(
        baseUrl: String, userName: String, accessToken: String,
        refreshToken: String, refreshExpiresAt: Long, accessExpiresAt: Long, isManager: Boolean
    ) {
        accessTokenValue = accessToken
        refreshTokenValue = refreshToken
    }

    override suspend fun updateTokens(accessToken: String, refreshToken: String, refreshExpiresAt: Long, accessExpiresAt: Long) {
        accessTokenValue = accessToken
        refreshTokenValue = refreshToken
        savedTokens += Triple(accessToken, refreshToken, refreshExpiresAt)
    }

    override suspend fun updateManagerFlag(isManager: Boolean) = Unit
    override suspend fun clear() {
        cleared = true
        refreshTokenValue = null
    }
}

/** 计划 §4.3「401 单飞刷新」：并发 N 个刷新只有 1 个真实 HTTP 请求，其余复用结果。 */
class SessionSingleFlightTest {

    private lateinit var server: MockWebServer
    private val refreshCount = AtomicInteger(0)
    private val json = Json { ignoreUnknownKeys = true }

    @Before
    fun setUp() {
        server = MockWebServer()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/AppAuth/refresh")) {
                    val n = refreshCount.incrementAndGet()
                    Thread.sleep(100) // 拉长首刷窗口，让并发方在锁上排队
                    return MockResponse().setHeader("Content-Type", "application/json").setBody(
                        """{"Code":200,"Message":"Success","Data":{"AccessToken":"A$n","RefreshToken":"r$n","RefreshExpiresAt":"2026-10-14 00:00:00"}}"""
                    )
                }
                return MockResponse().setResponseCode(404)
            }
        }
        server.start()
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    @Test
    fun concurrentRefresh_HitsServerExactlyOnce() = runBlocking {
        val store = FakeTokenStore()
        val session = SessionManager(store, RawAuthHttp(json))
        session.updateBaseUrl(server.url("/").toString().trimEnd('/'))

        val results = (1..10).map { async(Dispatchers.IO) { session.refresh() } }.awaitAll()

        assertEquals(10, results.count { it }, "所有并发方都应拿到新令牌")
        assertEquals(1, refreshCount.get(), "并发 401 只发一次刷新请求")
        assertEquals("A1", store.accessTokenValue)
        assertEquals(1, store.savedTokens.size, "令牌仅轮换一次")
    }

    @Test
    fun refreshWithNoRefreshToken_LogsOutWithoutHttp() = runBlocking {
        val store = FakeTokenStore().apply { refreshTokenValue = null }
        val session = SessionManager(store, RawAuthHttp(json))
        session.updateBaseUrl(server.url("/").toString().trimEnd('/'))

        assertEquals(false, session.refresh(), "无刷新令牌应失败")
        assertEquals(0, refreshCount.get(), "不应发起网络请求")
        assertTrue(store.cleared)
    }

    @Test
    fun refreshServerFailure_ClearsSession() = runBlocking {
        // 旧后端兼容路径：无 401 语义时确定性拒绝以 500+「重新登录」文案表达 → 仍应登出
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":500,"Message":"刷新令牌无效或已过期，请重新登录！"}""")
        }
        val store = FakeTokenStore()
        val session = SessionManager(store, RawAuthHttp(json))
        session.updateBaseUrl(server.url("/").toString().trimEnd('/'))

        assertEquals(false, session.refresh(), "服务端拒绝应失败")
        assertTrue(store.cleared, "确定性拒绝应清本地（登出）")
    }

    @Test
    fun refreshEnvelope401_ClearsSession() = runBlocking {
        // 新后端：确定性拒绝 = 信封 Code=401
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":401,"Message":"刷新令牌无效或已过期，请重新登录！"}""")
        }
        val store = FakeTokenStore()
        val session = SessionManager(store, RawAuthHttp(json))
        session.updateBaseUrl(server.url("/").toString().trimEnd('/'))

        assertEquals(false, session.refresh())
        assertTrue(store.cleared, "401 确定性拒绝应清本地（登出）")
    }

    @Test
    fun refreshTransientFailure_KeepsSession_AndBacksOff() = runBlocking {
        // 暂时性失败（服务端临时故障 500）：不清本地令牌，且退避窗口内不再发第二次请求
        val transientCount = AtomicInteger(0)
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                transientCount.incrementAndGet()
                return MockResponse().setHeader("Content-Type", "application/json")
                    .setBody("""{"Code":500,"Message":"系统出了点问题，开发者也有点懵逼"}""")
            }
        }
        val store = FakeTokenStore()
        val session = SessionManager(store, RawAuthHttp(json))
        session.updateBaseUrl(server.url("/").toString().trimEnd('/'))

        assertEquals(false, session.refresh(), "暂时性失败应返回 false")
        assertEquals(false, store.cleared, "暂时性失败绝不能清本地会话")
        assertEquals("r-old", store.refreshTokenValue, "刷新令牌保留待重试")

        // 退避窗口内再刷：不发网络请求
        assertEquals(false, session.refresh())
        assertEquals(1, transientCount.get(), "退避窗口内不应重复发请求")
    }

    @Test
    fun refreshNetworkError_IsTransient_AndKeepsSession() = runBlocking {
        // 网络异常（对端不可达）：Transient，不清会话
        val store = FakeTokenStore()
        val session = SessionManager(store, RawAuthHttp(json))
        session.updateBaseUrl("http://127.0.0.1:1") // 保留端口，连接必被拒

        assertEquals(false, session.refresh())
        assertEquals(false, store.cleared, "网络异常不能清本地会话")
        assertEquals("r-old", store.refreshTokenValue)
    }
}

/** RawAuthHttp 通道：信封解析与失败降级。 */
class RawAuthHttpTest {

    @Test
    fun parsesSuccessfulRefresh() = runBlocking {
        val server = MockWebServer()
        server.enqueue(
            MockResponse().setHeader("Content-Type", "application/json").setBody(
                """{"Code":200,"Message":"Success","Data":{"AccessToken":"at","RefreshToken":"rt","RefreshExpiresAt":"2026-10-14 00:00:00"}}"""
            )
        )
        server.start()
        val result = RawAuthHttp(Json { ignoreUnknownKeys = true })
            .refresh(server.url("/").toString().trimEnd('/'), "r-old", "dev")
        val success = result as RefreshResult.Success
        assertEquals("at", success.accessToken)
        assertEquals("rt", success.refreshToken)
        assertTrue(success.refreshExpiresAtEpoch > 0)
        val recorded = server.takeRequest()
        assertEquals("/api/AppAuth/refresh", recorded.path)
        org.junit.Assert.assertTrue(recorded.body.readUtf8().contains("\"RefreshToken\":\"r-old\""))
        server.shutdown()
    }

    @Test
    fun definitiveRejection_500WithReloginMessage_IsRejected() {
        // 旧后端兼容：500 + 「重新登录」文案 = 确定性拒绝（区别于临时故障 500）
        val server = MockWebServer()
        server.enqueue(
            MockResponse().setHeader("Content-Type", "application/json")
                .setBody("""{"Code":500,"Message":"刷新令牌无效或已过期，请重新登录！"}""")
        )
        server.start()
        val result = RawAuthHttp(Json { ignoreUnknownKeys = true })
            .refresh(server.url("/").toString().trimEnd('/'), "r-old", "dev")
        assertEquals(RefreshResult.Rejected, result)
        server.shutdown()
    }

    @Test
    fun envelope401_IsRejected() {
        val server = MockWebServer()
        server.enqueue(
            MockResponse().setHeader("Content-Type", "application/json")
                .setBody("""{"Code":401,"Message":"刷新令牌无效或已过期，请重新登录！"}""")
        )
        server.start()
        val result = RawAuthHttp(Json { ignoreUnknownKeys = true })
            .refresh(server.url("/").toString().trimEnd('/'), "r-old", "dev")
        assertEquals(RefreshResult.Rejected, result)
        server.shutdown()
    }

    @Test
    fun genericServerFailure_IsTransient() {
        // 临时故障（500 但文案不含「重新登录」）：Transient，客户端保留会话重试
        val server = MockWebServer()
        server.enqueue(
            MockResponse().setHeader("Content-Type", "application/json")
                .setBody("""{"Code":500,"Message":"系统出了点问题"}""")
        )
        server.start()
        val result = RawAuthHttp(Json { ignoreUnknownKeys = true })
            .refresh(server.url("/").toString().trimEnd('/'), "r-old", "dev")
        assertEquals(RefreshResult.Transient, result)
        server.shutdown()
    }

    @Test
    fun blankBaseUrl_IsTransient() {
        val result = RawAuthHttp(Json { ignoreUnknownKeys = true }).refresh("", "r", "d")
        assertEquals(RefreshResult.Transient, result)
    }
}
