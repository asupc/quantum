package com.quantum.app.core.network.ws

import android.util.Log
import com.quantum.app.core.network.session.SessionManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.TimeUnit
import javax.inject.Inject
import javax.inject.Singleton

/** WS 下行/上行帧（服务端手写小写驼峰；direction 仅 sync 补拉帧携带）。 */
@Serializable
data class WsFrame(
    val type: String,
    val token: String? = null,
    val content: String? = null,
    val msgId: String? = null,
    val seq: Long? = null,
    val contentType: String? = null,
    /** 配文（message 帧）：image/file 消息附带的文字说明，旧服务端缺省 null */
    val contentText: String? = null,
    /** 会话键（message/notify 帧）：= 任务 Id，客户端据此归档到对应脚本会话；缺省 null = 默认会话 */
    val session: String? = null,
    /** 点选来源任务 Id（command 帧上行）：合并会话内点选代发的精确路由依据；手打/重发缺省 null */
    val targetTask: String? = null,
    /** 结构化富交互载荷（message 帧，JSON 字符串）：可点选项/视频封面，缺省 null = 普通消息 */
    val payload: String? = null,
    val direction: Int? = null,
    val createTime: String? = null,
    val category: String? = null,
    val title: String? = null,
    val jump: String? = null,
    val afterSeq: Long? = null,
    val maxSeq: Long? = null,
    /** 跨会话触发迁移来源会话键（message_moved 帧）：缺省 null = 默认会话 */
    val from: String? = null,
    /** 跨会话触发迁移目标会话键（message_moved 帧）：= 触发任务的会话键 */
    val to: String? = null
)

enum class WsState { Disconnected, Connecting, Open }

/** WS 上行命令抽象（ChatRepository 依赖；单测注入内存实现）。 */
interface WsGateway {
    fun sendAck(msgId: String): Boolean
    fun sendCommand(content: String, session: String = "", targetTask: String? = null): Boolean
}

/**
 * /ws/app 长连接客户端（契约 docs/App端API契约.md §4）：
 * - 握手 /ws/app?token=<accessToken>；error「token 无效或已过期」→ 触发刷新后重连；
 * - **每 25s 发应用层 {"type":"ping"}**（服务端只按应用层帧计活，OkHttp 协议层 ping 不算）；
 * - 指数退避 1s→30s 重连；收 message 帧由上层入库+ack，notify 帧转通知模块。
 * - 生命周期：前台保持；退后台 60s 后由 Shell 调 [disconnect]（厂商推送兜底，不做前台服务）。
 */
@Singleton
class AppWsClient @Inject constructor(
    private val sessionManager: SessionManager,
    private val json: Json
) : WsGateway {
    private val _state = MutableStateFlow(WsState.Disconnected)
    val state: StateFlow<WsState> = _state.asStateFlow()

    private val _frames = MutableSharedFlow<WsFrame>(extraBufferCapacity = 64)
    val frames: SharedFlow<WsFrame> = _frames.asSharedFlow()

    private val client = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .pingInterval(20, TimeUnit.SECONDS)
        .build()

    /** §4-8 状态迁移互斥量：连接生命周期字段一律在 `synchronized(lock)` 下变更（OkHttp 回调线程与调用线程并发）。 */
    private val lock = Any()

    @Volatile private var webSocket: WebSocket? = null
    @Volatile private var heartbeatJob: Job? = null
    @Volatile private var reconnectJob: Job? = null
    @Volatile private var backoffSeconds = 1L
    @Volatile private var wantConnected = false

    /** 客户端主动 [disconnect] 置位：服务端/网络发起的关闭（含心跳超时 1000/1001）一律重连。 */
    @Volatile
    private var userClose = false

    fun connect(scope: CoroutineScope) {
        val base = sessionManager.currentBaseUrl
        val token = sessionManager.accessToken()
        if (base.isBlank() || token.isNullOrBlank()) {
            return
        }
        // §4-8：状态变更主体串行化。OkHttp 监听器回调在其后台线程异步派发，不会同步回调进本块，
        // 锁内建连安全；监听器各回调同样 synchronized(lock)，与 connect 同锁可重入。
        synchronized(lock) {
            if (webSocket != null && _state.value == WsState.Disconnected) {
                // 僵死引用（握手失败/刷新令牌后遗留）：作废重建，否则后续所有 connect() 都会因引用非空而空转
                resetSocket()
            }
            if (webSocket != null) {
                // Open 复用现有连接；Connecting 等待在途握手，避免并发建两条连接
                return
            }
            wantConnected = true
            userClose = false
            _state.value = WsState.Connecting
            val wsBase = base.replaceFirst("http", "ws")
            val request = Request.Builder().url("$wsBase/ws/app?token=$token").build()
            webSocket = client.newWebSocket(request, object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    synchronized(lock) {
                        userClose = false
                        _state.value = WsState.Open
                        backoffSeconds = 1
                        startHeartbeat(scope)
                    }
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    val frame = if (text.isBlank()) {
                        // 分类日志①：空帧（对端异常/中间层截断），不再静默丢弃
                        Log.w(TAG, "WS 收到空帧，已忽略")
                        return
                    } else {
                        runCatching { json.decodeFromString<WsFrame>(text) }.getOrElse { e ->
                            // 分类日志②：解码异常（协议漂移/脏数据），打印原因与原文片段供排查
                            Log.w(TAG, "WS 帧解码失败: ${e.message}, raw=${text.take(200)}", e)
                            return
                        }
                    }
                    if (frame.type == "error" && frame.content?.contains("token") == true) {
                        // 令牌过期：刷新后重连（不指数退避）。必须先作废当前连接引用，
                        // 否则 connect() 会因引用非空而早退，令牌刷新等于空转
                        scope.launch {
                            synchronized(lock) { resetSocket() }
                            sessionManager.refresh()
                            reconnect(scope, immediate = true)
                        }
                        return
                    }
                    // 其余帧（含服务端 error 文案）一律下发：限流/拒收必须能被上层看见，不能静默丢弃
                    _frames.tryEmit(frame)
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    var retry = false
                    synchronized(lock) {
                        _state.value = WsState.Disconnected
                        stopHeartbeat()
                        this@AppWsClient.webSocket = null
                        retry = wantConnected && !userClose
                    }
                    Log.w(TAG, "WS onFailure: ${t.message} code=${response?.code} retry=$retry", t)
                    if (retry) reconnect(scope)
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    // 对端发起优雅关闭（如服务端心跳超时）：回执 close 完成握手，
                    // onClosed 随后触发并按 wantConnected/userClose 策略重连
                    webSocket.close(1000, null)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    var retry = false
                    synchronized(lock) {
                        _state.value = WsState.Disconnected
                        stopHeartbeat()
                        this@AppWsClient.webSocket = null
                        // 不特判关闭码：服务端心跳超时即将从 1000 改 1001，任何非客户端主动发起的关闭都重连
                        retry = wantConnected && !userClose
                    }
                    if (retry) reconnect(scope)
                }
            })
        }
    }

    /** message/notify 帧幂等回执。 */
    override fun sendAck(msgId: String): Boolean =
        webSocket?.send(json.encodeToString(WsFrame(type = "ack", msgId = msgId))) ?: false

    override fun sendCommand(content: String, session: String, targetTask: String?): Boolean =
        webSocket?.send(json.encodeToString(WsFrame(type = "command", content = content,
            session = session.ifEmpty { null }, targetTask = targetTask))) ?: false

    /**
     * 作废旧连接引用并尽可能关掉它：心跳停止、状态回 Disconnected、引用置空。
     * 不置空的话后续 connect() 会因 webSocket != null 直接早退（令牌刷新后重连空转的根因）。
     * §4-8：调用方须持有 [lock]。
     */
    private fun resetSocket() {
        stopHeartbeat()
        val old = webSocket
        webSocket = null
        _state.value = WsState.Disconnected
        runCatching { old?.close(1000, "reconnect") }
    }

    /** 退后台/登出时主动断开（wantConnected=false + userClose=true 后不再自动重连）。 */
    fun disconnect() {
        synchronized(lock) {
            wantConnected = false
            userClose = true
            stopHeartbeat()
            reconnectJob?.cancel()
            webSocket?.close(1000, "client going background")
            webSocket = null
            _state.value = WsState.Disconnected
        }
    }

    private fun reconnect(scope: CoroutineScope, immediate: Boolean = false) {
        synchronized(lock) {
            if (!wantConnected || reconnectJob?.isActive == true) {
                return
            }
            reconnectJob = scope.launch {
                if (!immediate) {
                    delay(backoffSeconds * 1000)
                    backoffSeconds = (backoffSeconds * 2).coerceAtMost(30)
                }
                connect(scope)
            }
        }
    }

    /** §4-8：调用方须持有 [lock]；心跳循环体在 scope 协程内运行、不持锁（send 读 @Volatile webSocket）。 */
    private fun startHeartbeat(scope: CoroutineScope) {
        stopHeartbeat()
        heartbeatJob = scope.launch {
            while (isActive && _state.value == WsState.Open) {
                webSocket?.send(json.encodeToString(WsFrame(type = "ping")))
                delay(25_000L)
            }
        }
    }

    /** §4-8：调用方须持有 [lock]。 */
    private fun stopHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = null
    }

    companion object {
        private const val TAG = "AppWsClient"
    }
}
