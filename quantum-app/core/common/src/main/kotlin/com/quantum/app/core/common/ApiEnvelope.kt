package com.quantum.app.core.common

/**
 * 服务端统一响应信封：HTTP 恒 200，成败只看 [code]。
 * 200 成功 / 500 业务失败（message 为可展示文案）/ 401 认证或权限失败。
 */
data class ApiEnvelope<T>(
    val code: Int,
    val message: String? = null,
    val data: T? = null
) {
    val isSuccess: Boolean get() = code == 200
}

/** 业务失败（Code=500）：message 可直接 toast。 */
open class ApiException(val code: Int, message: String?) : Exception(message ?: "请求失败")

/** 认证失败（Code=401）：触发刷新/登出流程。 */
class AuthException(message: String?) : ApiException(401, message)
