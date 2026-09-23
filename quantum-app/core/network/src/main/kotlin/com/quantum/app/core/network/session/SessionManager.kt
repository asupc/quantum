package com.quantum.app.core.network.session

import android.util.Base64
import com.quantum.app.core.network.api.EnvelopeDto
import com.quantum.app.core.storage.prefs.TokenStoreApi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import javax.inject.Inject
import javax.inject.Singleton

/** 会话抽象：拦截器/仓库等依赖此接口，单测可注入内存实现。 */
interface SessionGateway {
    val currentBaseUrl: String
    fun accessToken(): String?
    suspend fun refresh(): Boolean
}

/**
 * 会话中枢：
 * - 缓存当前 accessToken/baseUrl（OkHttp 拦截器同步读取用）；
 * - **单飞刷新**：并发 401 只发一次 refresh（Mutex + 等待方复用结果），轮换即旧 Refresh 失效；
 * - Access 到期前 10 分钟主动续期（[startRenewalLoop] 由 Application 启动）；
 * - 刷新失败分流（2026-09-19 长效化改造）：**Rejected**（到达服务器且确定性拒绝：信封 401 /
 *   旧后端 500+「重新登录」文案）→ 清本地并广播 [Event.LoggedOut]；**Transient**（网络异常/
 *   超时/服务端临时故障）→ **保留本地令牌**，按 1..10 分钟线性退避稍后重试——服务端重启、
 *   nginx 抖动不再丢登录态；
 * - Refresh 本地预检过期 → 清本地并广播 [Event.LoggedOut]（UI 跳登录页）。
 */
@Singleton
class SessionManager @Inject constructor(
    private val tokenStore: TokenStoreApi,
    private val rawAuthHttp: RawAuthHttp
) : SessionGateway {
    sealed interface Event {
        data object LoggedOut : Event
        data object TokensRefreshed : Event
    }

    private val _events = MutableSharedFlow<Event>(extraBufferCapacity = 8)
    val events: SharedFlow<Event> = _events.asSharedFlow()

    @Volatile
    override var currentBaseUrl: String = ""
        private set

    @Volatile
    private var cachedAccessToken: String? = null

    @Volatile
    private var cachedAccessExpiresAt: Long = 0L

    private val refreshMutex = Mutex()
    private var renewalStarted = false

    /** 连续暂时性失败次数与退避下界（currentTimeMillis）：防服务端不可达期间每分钟空打。 */
    @Volatile
    private var transientFailures = 0

    @Volatile
    private var transientRetryNotBefore = 0L

    /** Application 启动时调用：从 DataStore 载入缓存。 */
    suspend fun load() {
        currentBaseUrl = tokenStore.currentBaseUrl().orEmpty()
        cachedAccessToken = tokenStore.currentAccessToken()
        cachedAccessExpiresAt = tokenStore.currentAccessExpiresAt()
    }

    override fun accessToken(): String? = cachedAccessToken

    /** 「服务器地址」页保存后同步刷新拦截器缓存。 */
    fun updateBaseUrl(url: String) {
        currentBaseUrl = url.trimEnd('/')
    }

    suspend fun onLoginSuccess(
        baseUrl: String,
        userName: String,
        accessToken: String,
        refreshToken: String,
        refreshExpiresAtRaw: String
    ) {
        currentBaseUrl = baseUrl.trimEnd('/')
        cachedAccessToken = accessToken
        cachedAccessExpiresAt = JwtUtil.expOf(accessToken) ?: 0L
        tokenStore.saveLogin(
            baseUrl = baseUrl,
            userName = userName,
            accessToken = accessToken,
            refreshToken = refreshToken,
            refreshExpiresAt = ServerTime.parseServer(refreshExpiresAtRaw),
            accessExpiresAt = cachedAccessExpiresAt,
            isManager = JwtUtil.claimOf(accessToken, "Manager") == "true"
        )
    }

    /**
     * 单飞刷新：并发调用只有一个真正发请求，其余等待结果。
     * 返回 true 表示拿到新令牌（EnvelopeInterceptor 据此重放原请求）。
     */
    @Volatile
    private var refreshGeneration = 0

    /**
     * 单飞刷新：并发调用只有一个真正发请求，其余等待后**直接复用结果**
     * （世代号双检：等待期间他人刷新成功则不再发第二次请求，符合「并发 401 只发一次」契约）。
     * 返回 true 表示拿到新令牌（EnvelopeInterceptor 据此重放原请求）。
     */
    override suspend fun refresh(): Boolean {
        val myGeneration = refreshGeneration
        return refreshMutex.withLock {
            if (refreshGeneration != myGeneration) {
                // 等锁期间他人已完成刷新
                return@withLock cachedAccessToken != null
            }
            // 暂时性失败退避窗口内：不发请求（会话保留，窗口过后由 renewal loop/下次 401 重试）
            if (System.currentTimeMillis() < transientRetryNotBefore) {
                return@withLock false
            }
            val refreshToken = tokenStore.currentRefreshToken()
            if (refreshToken.isNullOrBlank() || tokenStore.currentRefreshExpiresAt() < System.currentTimeMillis()) {
                awaitLogout()
                return@withLock false
            }
            when (val result = rawAuthHttp.refresh(currentBaseUrl, refreshToken, tokenStore.currentDeviceId())) {
                is RefreshResult.Success -> {
                    transientFailures = 0
                    transientRetryNotBefore = 0L
                    cachedAccessToken = result.accessToken
                    cachedAccessExpiresAt = JwtUtil.expOf(result.accessToken) ?: 0L
                    tokenStore.updateTokens(
                        accessToken = result.accessToken,
                        refreshToken = result.refreshToken,
                        refreshExpiresAt = result.refreshExpiresAtEpoch,
                        accessExpiresAt = cachedAccessExpiresAt
                    )
                    refreshGeneration++
                    _events.emit(Event.TokensRefreshed)
                    true
                }
                RefreshResult.Rejected -> {
                    transientFailures = 0
                    transientRetryNotBefore = 0L
                    awaitLogout()
                    false
                }
                RefreshResult.Transient -> {
                    transientFailures++
                    transientRetryNotBefore = System.currentTimeMillis() + minOf(transientFailures, 10) * 60_000L
                    false
                }
            }
        }
    }

    /** Access 到期前 10 分钟到期，则主动刷新；返回是否持有有效会话。 */
    suspend fun maybeRenewProactively(now: Long = System.currentTimeMillis()): Boolean {
        if (cachedAccessToken == null) {
            return false
        }
        if (now < cachedAccessExpiresAt - 10 * 60 * 1000L) {
            return true
        }
        return refresh()
    }

    suspend fun logout(remoteRevoke: (suspend () -> Unit)? = null) {
        try {
            remoteRevoke?.invoke()
        } catch (ignored: Exception) {
            // 远端吊销失败不阻断本地登出
        }
        awaitLogout()
    }

    fun startRenewalLoop(scope: CoroutineScope) {
        if (renewalStarted) {
            return
        }
        renewalStarted = true
        scope.launch {
            while (true) {
                runCatching { maybeRenewProactively() }
                kotlinx.coroutines.delay(60_000L)
            }
        }
    }

    private suspend fun awaitLogout() {
        cachedAccessToken = null
        tokenStore.clear()
        _events.emit(Event.LoggedOut)
    }
}

/**
 * 刷新通道结果（不经 Retrofit：避免拦截器↔会话循环依赖）。
 * 失败必须分两态（2026-09-19 长效化改造）：把网络抖动当确定性拒绝清会话，是「服务端重启就被登出」的根因。
 */
sealed interface RefreshResult {
    data class Success(
        val accessToken: String,
        val refreshToken: String,
        val refreshExpiresAtEpoch: Long
    ) : RefreshResult

    /** 确定性拒绝（到达服务器且信封 401 / 旧后端 500+「重新登录」文案）：清本地跳登录页。 */
    data object Rejected : RefreshResult

    /** 暂时性失败（网络异常/超时/HTTP 层错误/服务端临时故障）：保留本地令牌稍后重试。 */
    data object Transient : RefreshResult
}

/** 裸 OkHttp 刷新通道（无鉴权拦截器）。 */
@Singleton
class RawAuthHttp @Inject constructor(private val json: Json) {
    private val client = okhttp3.OkHttpClient.Builder()
        .connectTimeout(java.time.Duration.ofSeconds(15))
        .readTimeout(java.time.Duration.ofSeconds(30))
        .build()

    fun refresh(baseUrl: String, refreshToken: String, deviceId: String): RefreshResult {
        if (baseUrl.isBlank()) {
            return RefreshResult.Transient
        }
        return try {
            val body = json.encodeToString(
                com.quantum.app.core.network.dto.RefreshRequest(refreshToken, deviceId)
            )
            val request = okhttp3.Request.Builder()
                .url("$baseUrl/api/AppAuth/refresh")
                .post(body.toRequestBody("application/json".toMediaType()))
                .build()
            client.newCall(request).execute().use { response ->
                val text = response.body?.string().orEmpty()
                val envelope = runCatching {
                    json.decodeFromString<EnvelopeDto<com.quantum.app.core.network.dto.LoginResult>>(text)
                }.getOrNull()
                when {
                    envelope?.code == 200 && envelope.data != null -> RefreshResult.Success(
                        accessToken = envelope.data.accessToken,
                        refreshToken = envelope.data.refreshToken,
                        refreshExpiresAtEpoch = ServerTime.parseServer(envelope.data.refreshExpiresAt)
                    )
                    envelope?.code == 401 -> RefreshResult.Rejected
                    // 旧后端兼容（部署顺序「后端→APK」的窗口期）：无 401 语义时，
                    // 确定性拒绝以 500+「重新登录」文案表达；临时故障文案不含该词
                    envelope?.code == 500 && envelope.message.orEmpty().contains("重新登录") -> RefreshResult.Rejected
                    else -> RefreshResult.Transient
                }
            }
        } catch (e: Exception) {
            RefreshResult.Transient
        }
    }
}

/** JWT 工具：解 exp 与自定义 claim（客户端不校验签名——签名由服务端拦截器校验）。 */
object JwtUtil {
    fun expOf(token: String): Long? = runCatching {
        decodePayload(token).optLong("exp", -1L).takeIf { it > 0 }?.times(1000L)
    }.getOrNull()

    fun claimOf(token: String, name: String): String? = runCatching {
        decodePayload(token).optString(name).takeIf { it.isNotEmpty() }
    }.getOrNull()

    private fun decodePayload(token: String): org.json.JSONObject {
        val parts = token.split(".")
        require(parts.size >= 2) { "bad jwt" }
        val bytes = Base64.decode(parts[1], Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
        return org.json.JSONObject(String(bytes, Charsets.UTF_8))
    }
}

/** 服务器时间串（yyyy-MM-dd HH:mm:ss）转 epoch——统一走 [com.quantum.app.core.common.TimeUtil] 固定服务端时区，与设备时区无关。 */
object ServerTime {
    fun parseServer(raw: String?): Long =
        com.quantum.app.core.common.TimeUtil.parse(raw) ?: 0L
}
