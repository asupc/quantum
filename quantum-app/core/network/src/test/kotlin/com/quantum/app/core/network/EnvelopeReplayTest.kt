package com.quantum.app.core.network

import com.quantum.app.core.network.session.AuthInterceptor
import com.quantum.app.core.network.session.EnvelopeInterceptor
import com.quantum.app.core.network.session.RawAuthHttp
import com.quantum.app.core.network.session.SessionManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.mockwebserver.RecordedRequest
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Before
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * 计划 §4.3「401 进入刷新流程后重放原请求一次」+「并发 401 只发一次刷新」：
 * MockWebServer 全链路（EnvelopeInterceptor + AuthInterceptor + SessionManager + RawAuthHttp）。
 */
class EnvelopeReplayTest {

    private lateinit var server: MockWebServer
    private val refreshCount = AtomicInteger(0)
    private val json = Json { ignoreUnknownKeys = true }

    @Before
    fun setUp() {
        server = MockWebServer()
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    private suspend fun newSession(baseUrl: String): SessionManager {
        val session = SessionManager(FakeTokenStore(), RawAuthHttp(json))
        session.updateBaseUrl(baseUrl)
        session.onLoginSuccess(
            baseUrl = baseUrl,
            userName = "user-a",
            accessToken = "old-token",
            refreshToken = "r-old",
            refreshExpiresAtRaw = "2026-10-14 00:00:00"
        )
        return session
    }

    private fun newClient(session: SessionManager) = OkHttpClient.Builder()
        .addInterceptor(AuthInterceptor(session))
        .addInterceptor(EnvelopeInterceptor(session))
        .build()

    private fun refreshTokenResponse() = MockResponse().setHeader("Content-Type", "application/json").setBody(
        """{"Code":200,"Message":"Success","Data":{"AccessToken":"new-token","RefreshToken":"r-new","RefreshExpiresAt":"2026-10-14 00:00:00"}}"""
    )

    @Test
    fun replayOnceWithNewToken_AfterSingle401() = runBlocking {
        val dataCalls = AtomicInteger(0)
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/AppAuth/refresh")) {
                    refreshCount.incrementAndGet()
                    return refreshTokenResponse()
                }
                // 首次数据请求带旧令牌 → 401；重放带新令牌 → 200
                val auth = request.getHeader("Authorization")
                return if (auth == "Bearer new-token") {
                    MockResponse().setHeader("Content-Type", "application/json")
                        .setBody("""{"Code":200,"Message":"Success","Data":true}""")
                } else {
                    dataCalls.incrementAndGet()
                    MockResponse().setHeader("Content-Type", "application/json").setBody("""{"Code":401}""")
                }
            }
        }
        server.start()
        val session = newSession(server.url("/").toString().trimEnd('/'))

        val response = newClient(session).newCall(
            Request.Builder().url(server.url("/api/Task/ping")).build()
        ).execute()

        assertEquals(200, response.code, "HTTP 恒 200")
        val body = response.body!!.string()
        println("BODY=" + body)
        println("refreshCount=" + refreshCount.get() + " accessToken=" + session.accessToken())
        assertTrue(body.contains("\"Code\":200"), "实际响应体: " + body + " refreshCount=" + refreshCount.get() + " accessToken=" + session.accessToken())
        assertEquals(expected = 1, actual = refreshCount.get(), message = "仅刷新一次")
        assertEquals(1, dataCalls.get(), "仅重放一次（原始 401 + 重放 200）")
    }

    @Test
    fun concurrent401s_RefreshOnlyOnce_AndAllSucceed() = runBlocking {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/AppAuth/refresh")) {
                    refreshCount.incrementAndGet()
                    Thread.sleep(100) // 首刷窗口
                    return refreshTokenResponse()
                }
                val auth = request.getHeader("Authorization")
                return if (auth == "Bearer new-token") {
                    MockResponse().setHeader("Content-Type", "application/json")
                        .setBody("""{"Code":200,"Message":"Success","Data":true}""")
                } else {
                    MockResponse().setHeader("Content-Type", "application/json").setBody("""{"Code":401}""")
                }
            }
        }
        server.start()
        val session = newSession(server.url("/").toString().trimEnd('/'))
        val client = newClient(session)

        val results = (1..8).map {
            async(Dispatchers.IO) {
                client.newCall(Request.Builder().url(server.url("/api/Task/ping")).build()).execute().use { r ->
                    r.body!!.string().contains("\"Code\":200")
                }
            }
        }.awaitAll()

        assertEquals(8, results.count { it }, "8 个并发请求最终都应成功（重放/直通）")
        assertEquals(1, refreshCount.get(), "并发 401 只发一次刷新请求")
    }

    @Test
    fun nonJsonResponse_NotPeeked_NoRefresh() = runBlocking {
        // §4-11：Coil 图片/ExoPlayer 视频复用同一 OkHttpClient，非 JSON 直出响应须按 content-type 短路，
        // 即便字节流里混进 {"Code":401} 字样也不得被当信封解析、不得触发刷新
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                if (request.path!!.startsWith("/api/AppAuth/refresh")) {
                    refreshCount.incrementAndGet()
                    return refreshTokenResponse()
                }
                return MockResponse().setHeader("Content-Type", "image/png")
                    .setBody("""{"Code":401}""")
            }
        }
        server.start()
        val session = newSession(server.url("/").toString().trimEnd('/'))

        val response = newClient(session).newCall(
            Request.Builder().url(server.url("/api/AppUpload/media.png")).build()
        ).execute()

        val body = response.body!!.string()
        assertTrue(body.contains("\"Code\":401"), "非 JSON 响应体须原样透传（未被拦截器改写）")
        assertEquals(0, refreshCount.get(), "非 JSON 响应不得 peek 触发令牌刷新")
    }
}
