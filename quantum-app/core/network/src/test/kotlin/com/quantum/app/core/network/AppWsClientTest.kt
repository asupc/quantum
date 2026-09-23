package com.quantum.app.core.network

import com.quantum.app.core.network.session.RawAuthHttp
import com.quantum.app.core.network.session.SessionManager
import com.quantum.app.core.network.ws.AppWsClient
import com.quantum.app.core.network.ws.WsState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.After
import org.junit.Before
import org.junit.Test
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * WS 客户端重连/直推契约（MockWebServer WebSocket 升级）：
 * - 服务端以 1000 关闭（心跳超时旧码；服务端将改 1001）也必须重连——不特判关闭码；
 * - 客户端主动 disconnect()（userClose）后不再重连；
 * - notify 帧原样转发给收集者（App 层常驻收集入库）。
 */
class AppWsClientTest {

    private lateinit var server: MockWebServer
    private lateinit var client: AppWsClient
    private lateinit var scope: CoroutineScope

    private val openings = CopyOnWriteArrayList<WebSocket>()
    private var onServerOpen: (WebSocket, Int) -> Unit = { _, _ -> }

    @Before
    fun setUp() {
        server = MockWebServer()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
                    override fun onOpen(webSocket: WebSocket, response: Response) {
                        openings += webSocket
                        onServerOpen(webSocket, openings.size)
                    }
                })
        }
        server.start()
        val json = Json { ignoreUnknownKeys = true }
        val tokenStore = FakeTokenStore()
        tokenStore.accessTokenValue = "fake-token"
        val session = SessionManager(tokenStore, RawAuthHttp(json))
        runBlocking { session.load() }
        session.updateBaseUrl(server.url("/").toString())
        client = AppWsClient(session, json)
        scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    }

    @After
    fun tearDown() {
        client.disconnect()
        scope.cancel()
        // 先强制断开服务端 socket，MockWebServer 队列才能干净关闭
        openings.forEach { runCatching { it.cancel() } }
        runCatching { server.shutdown() }
    }

    @Test
    fun serverCloseCode1000_stillReconnects(): Unit = runBlocking {
        val reconnected = CountDownLatch(1)
        onServerOpen = { ws, index ->
            if (index == 1) {
                // 第一条连接：服务端以 1000（心跳超时旧码）发起关闭
                ws.close(1000, "heartbeat timeout")
            } else {
                reconnected.countDown()
            }
        }

        client.connect(scope)

        assertTrue(
            reconnected.await(20, TimeUnit.SECONDS),
            "服务端以 1000 关闭后客户端必须重连（不特判关闭码；1001 同理）"
        )
    }

    @Test
    fun userDisconnect_doesNotReconnect(): Unit = runBlocking {
        val opened = CountDownLatch(1)
        onServerOpen = { _, _ -> opened.countDown() }

        client.connect(scope)
        assertTrue(opened.await(20, TimeUnit.SECONDS), "应先建立连接")
        withTimeout(20_000) { client.state.first { it == WsState.Open } }

        client.disconnect()
        // 重连退避 1s→2s，静默观察 3.5s：客户端主动断开后不得再建连
        Thread.sleep(3_500)
        assertEquals(1, openings.size, "客户端主动 disconnect()（userClose）后不应重连")
    }

    @Test
    fun notifyFrame_isForwardedToCollectors(): Unit = runBlocking {
        onServerOpen = { ws, _ ->
            ws.send(
                """{"type":"notify","msgId":"n1","category":"system","title":"标题","content":"内容","jump":"quantum://chat"}"""
            )
        }

        client.connect(scope)

        val frame = withTimeout(20_000) { client.frames.first { it.type == "notify" } }
        assertEquals("n1", frame.msgId)
        assertEquals("system", frame.category)
        assertEquals("标题", frame.title)
        assertEquals("内容", frame.content)
        assertEquals("quantum://chat", frame.jump)
    }
}
