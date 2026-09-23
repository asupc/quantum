package com.quantum.app.core.storage.prefs

/**
 * TokenStore 的抽象（供 SessionManager 等纯 JVM 逻辑依赖，单测可注入内存实现）。
 */
interface TokenStoreApi {
    val baseUrl: kotlinx.coroutines.flow.Flow<String?>
    val accessToken: kotlinx.coroutines.flow.Flow<String?>
    val loggedIn: kotlinx.coroutines.flow.Flow<Boolean>
    val isManager: kotlinx.coroutines.flow.Flow<Boolean>
    val userName: kotlinx.coroutines.flow.Flow<String?>

    suspend fun currentBaseUrl(): String?
    suspend fun currentAccessToken(): String?
    suspend fun currentRefreshToken(): String?
    suspend fun currentDeviceId(): String
    suspend fun currentAccessExpiresAt(): Long
    suspend fun currentRefreshExpiresAt(): Long

    suspend fun saveBaseUrl(url: String)
    suspend fun saveLogin(
        baseUrl: String,
        userName: String,
        accessToken: String,
        refreshToken: String,
        refreshExpiresAt: Long,
        accessExpiresAt: Long,
        isManager: Boolean
    )

    suspend fun updateTokens(accessToken: String, refreshToken: String, refreshExpiresAt: Long, accessExpiresAt: Long)
    suspend fun updateManagerFlag(isManager: Boolean)
    suspend fun clear()
}
