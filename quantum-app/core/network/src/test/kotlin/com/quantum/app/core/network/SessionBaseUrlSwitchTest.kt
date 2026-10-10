package com.quantum.app.core.network

import com.quantum.app.core.network.session.AuthInterceptor
import com.quantum.app.core.network.session.RawAuthHttp
import com.quantum.app.core.network.session.SessionManager
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.After
import org.junit.Before
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * F6 回归（对抗审计）：换服务器地址必须作废旧服务器的登录态。
 * 旧行为：`updateBaseUrl` 只改 baseUrl 不清令牌，而 AuthInterceptor 按「请求 host/port ==
 * 当前 baseUrl」附 Bearer —— 用户改地址（未登出）后，旧服务器的 JWT 会被发给新服务器
 * （跨服务器令牌混淆）。同 host+port 的路径微调不得误清。
 */
class SessionBaseUrlSwitchTest {

    private lateinit var serverA: MockWebServer
    private lateinit var serverB: MockWebServer
    private val json = Json { ignoreUnknownKeys = true }

    @Before
    fun setUp() {
        serverA = MockWebServer()
        serverB = MockWebServer()
        listOf(serverA, serverB).forEach { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse =
                    MockResponse().setHeader("Content-Type", "application/json")
                        .setBody("""{"Code":200,"Message":"Success","Data":true}""")
            }
            server.start()
        }
    }

    @After
    fun tearDown() {
        listOf(serverA, serverB).forEach { runCatching { it.shutdown() } }
    }

    /** 「http://host:port」形态的 baseUrl（MockWebServer 每次分配不同端口）。 */
    private fun baseUrl(server: MockWebServer): String = server.url("/").toString().trimEnd('/')

    private suspend fun loggedInSession(store: FakeTokenStore, url: String): SessionManager =
        SessionManager(store, RawAuthHttp(json)).apply {
            updateBaseUrl(url)
            onLoginSuccess(
                baseUrl = url,
                userName = "user-a",
                accessToken = "token-from-a",
                refreshToken = "r-a",
                refreshExpiresAtRaw = "2026-10-14 00:00:00"
            )
        }

    /** 走 AuthInterceptor 的裸请求（不套信封拦截器，避免 401 刷新语义混进来）。 */
    private fun call(session: SessionManager, url: String) {
        val client = OkHttpClient.Builder().addInterceptor(AuthInterceptor(session)).build()
        client.newCall(Request.Builder().url(url).build()).execute().use { it.body?.string() }
    }

    @Test
    fun changingHost_clearsAccessToken_soNewServerGetsNoBearer() = runBlocking {
        val store = FakeTokenStore()
        val session = loggedInSession(store, baseUrl(serverA))
        assertTrue(store.accessTokenValue != null, "前置：A 服务器会话已建立")

        // 用户改地址到 B（未登出）
        session.updateBaseUrl(baseUrl(serverB))

        assertNull(session.accessToken(), "换服务器后不得再持有旧服务器 access token")
        assertNull(store.accessTokenValue, "落盘 access token 同步清除（防重启复活）")
        assertTrue(store.cleared, "落盘清除必须真的执行（tokenStore.clear）")

        // 对 B 的请求不得携带 Bearer
        val url = baseUrl(serverB) + "/api/App/messages?afterSeq=0"
        call(session, url)
        val recorded = serverB.takeRequest()
        assertNull(recorded.getHeader("Authorization"), "新服务器不得收到旧服务器的 Bearer")
        // 旧服务器地址也不再收到任何请求
        assertEquals(0, serverA.requestCount)
    }

    @Test
    fun changingPortOnSameHost_alsoClearsToken() = runBlocking {
        // 同 host 不同 port = 不同服务器实例（局域网多实例场景），必须同样清
        val store = FakeTokenStore()
        val session = loggedInSession(store, "http://127.0.0.1:5088")

        session.updateBaseUrl("http://127.0.0.1:5089")

        assertNull(session.accessToken())
        assertTrue(store.cleared)
    }

    @Test
    fun sameOriginPathTweak_doesNotClearToken() = runBlocking {
        // 仅路径/尾斜杠微调不是换服务器：不得误清登录态
        val store = FakeTokenStore()
        val session = loggedInSession(store, "http://127.0.0.1:5088")

        session.updateBaseUrl("http://127.0.0.1:5088/")

        assertEquals("token-from-a", session.accessToken(), "同 host:port 不得清令牌")
        assertTrue(!store.cleared)
    }

    @Test
    fun firstTimeConfigFromBlank_doesNotClear_orBroadcastLogout() = runBlocking {
        // 首启配置地址（原地址为空）：无旧服务器令牌可混淆，不清也不广播登出（登录态本就为空）
        val store = FakeTokenStore()
        val session = SessionManager(store, RawAuthHttp(json))

        session.updateBaseUrl(baseUrl(serverA))

        assertTrue(!store.cleared, "首次配置地址不得触发落盘清除")
        assertNull(session.accessToken())
    }

    @Test
    fun changingHost_broadcastsLoggedOut_toTearDownOldSession() = runBlocking {
        // 广播登出：UI 回登录页、AppPushHandler 断开指向旧服务器的 WS
        val store = FakeTokenStore()
        val session = loggedInSession(store, baseUrl(serverA))
        var loggedOut = false
        // UNDISPATCHED：先让收集器订阅上（SharedFlow 无 replay，迟到订阅收不到已发事件）
        val job = launch(start = CoroutineStart.UNDISPATCHED) {
            session.events.collect { if (it == SessionManager.Event.LoggedOut) loggedOut = true }
        }

        session.updateBaseUrl(baseUrl(serverB))
        delay(100)
        job.cancel()

        assertTrue(loggedOut, "换服务器应广播 LoggedOut（旧会话不可能在新服务器上继续）")
    }
}
