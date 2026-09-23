package com.quantum.app.core.network.session

import kotlinx.coroutines.runBlocking
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.Interceptor
import okhttp3.Response
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 动态 BaseUrl：Retrofit 占位 base + 本拦截器按 TokenStore 配置重写 scheme/host/port。
 * 自托管形态决定 App 必须可配服务器地址（首启「服务器地址」页）。
 */
@Singleton
class BaseUrlInterceptor @Inject constructor(private val sessionManager: SessionGateway) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val original = chain.request()
        val base = sessionManager.currentBaseUrl
        if (base.isBlank()) {
            return chain.proceed(original)
        }
        val target = runCatching { "$base/".toHttpUrl() }.getOrNull()
        val isPlaceholder = original.url.host == "quantum.placeholder"
        return if (target != null && isPlaceholder) {
            val rewritten = original.url.newBuilder()
                .scheme(target.scheme)
                .host(target.host)
                .port(target.port)
                .build()
            chain.proceed(original.newBuilder().url(rewritten).build())
        } else {
            chain.proceed(original)
        }
    }
}

/** 附加 Bearer 访问令牌（仅限自家服务端：Coil 复用此客户端加载消息外链媒体，不能把 JWT 带给第三方）。 */
@Singleton
class AuthInterceptor @Inject constructor(private val sessionGateway: SessionGateway) : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val request = chain.request()
        val token = sessionGateway.accessToken() ?: return chain.proceed(request)
        val base = runCatching { sessionGateway.currentBaseUrl.toHttpUrl() }.getOrNull()
        val isOwnBase = base != null &&
            request.url.host == base.host &&
            request.url.port == base.port
        return if (isOwnBase) {
            chain.proceed(request.newBuilder().header("Authorization", "Bearer $token").build())
        } else {
            chain.proceed(request)
        }
    }
}

/**
 * 统一解包拦截器（HTTP 恒 200 的服务端特有约定）：
 * - 读取 body 的 Code：401 → 单飞刷新成功后**重放原请求一次**；
 * - 500/200 原样放行（typed 反序列化后由 unwrap() 抛业务异常给 UI）。
 * 匿名端点（AppAuth）与大响应体流式下载跳过解析。
 */
@Singleton
class EnvelopeInterceptor @Inject constructor(
    private val sessionGateway: SessionGateway
) : Interceptor {
    private val sessionManager get() = sessionGateway

    override fun intercept(chain: Interceptor.Chain): Response {
        val request = chain.request()
        val path = request.url.encodedPath
        if (SKIP_PATHS.any { path.contains(it) }) {
            return chain.proceed(request)
        }

        var response = chain.proceed(request)
        val code = peekCode(response) ?: return response

        if (code == 401) {
            // 单飞去重：若当前令牌已与本请求附带的不同，说明等待期间他人已完成刷新，
            // 直接重放即可（SessionManager.refresh 内部另有 Mutex + 世代号双检兜底真并发）
            val attached = request.header("Authorization")
            val current = sessionGateway.accessToken()?.let { "Bearer $it" }
            val refreshed = if (!attached.isNullOrBlank() && !current.isNullOrBlank() && attached != current) {
                true
            } else {
                runBlocking { sessionGateway.refresh() }
            }
            if (refreshed) {
                response.close()
                val token = sessionGateway.accessToken()
                val replayRequest = if (token.isNullOrBlank()) {
                    request
                } else {
                    request.newBuilder().header("Authorization", "Bearer $token").build()
                }
                response = chain.proceed(replayRequest)
            }
        }
        return response
    }

    /**
     * 非破坏性读取 body 首部，解析 {Code}；非 JSON / 解析失败返回 null（流式/图片/视频直出）。
     * §4-11：先按 content-type 短路——Coil 图片、ExoPlayer 视频复用同一 OkHttpClient，
     * 若不加这道门会对每帧响应 `peekBody(4096)` 拷贝 + `parseToJsonElement` 试解析（纯浪费且缓冲）。
     * 服务端鉴权失败仍回 JSON 信封 Code=401，故只放过 application/json 不影响 401 刷新重放。
     */
    private fun peekCode(response: Response): Int? {
        val subtype = response.body?.contentType()?.subtype ?: return null
        if (!subtype.contains("json", ignoreCase = true)) {
            return null
        }
        return try {
            val text = response.peekBody(PEEK_BYTES).string()
            if (text.isBlank()) {
                null
            } else {
                val element = kotlinx.serialization.json.Json.parseToJsonElement(text)
                (element as? kotlinx.serialization.json.JsonObject)?.get("Code")
                    ?.let { code -> (code as? kotlinx.serialization.json.JsonPrimitive)?.content?.toIntOrNull() }
            }
        } catch (e: Exception) {
            null
        }
    }

    companion object {
        private const val PEEK_BYTES = 4096L
        private val SKIP_PATHS = listOf("api/AppAuth/")
    }
}
