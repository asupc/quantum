package com.quantum.app.core.storage.prefs

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import java.util.UUID

private val Context.authDataStore by preferencesDataStore(name = "quantum_auth")
private val Context.prefsDataStore by preferencesDataStore(name = "quantum_prefs")

/**
 * 登录态三元组 + 服务器地址 + 设备标识（DataStore 持久化）。
 * 杀进程重启免登录、Access 到期前主动续期均依赖此处。
 */
class TokenStore(private val context: Context) : TokenStoreApi {

    private object Keys {
        val BASE_URL = stringPreferencesKey("base_url")
        val ACCESS_TOKEN = stringPreferencesKey("access_token")
        val REFRESH_TOKEN = stringPreferencesKey("refresh_token")
        val REFRESH_EXPIRES_AT = longPreferencesKey("refresh_expires_at")
        val ACCESS_EXPIRES_AT = longPreferencesKey("access_expires_at")
        val DEVICE_ID = stringPreferencesKey("device_id")
        val USER_NAME = stringPreferencesKey("user_name")
        val IS_MANAGER = booleanPreferencesKey("is_manager")

        // 登录页「记住账号密码」（与登录态生命周期解耦：登出/被踢都不清除，卸载清数据自然全清）
        val REMEMBER_USER_NAME = stringPreferencesKey("remember_user_name")
        val REMEMBER_PASSWORD_CIPHER = stringPreferencesKey("remember_password_cipher")
        val REMEMBER_PASSWORD = booleanPreferencesKey("remember_password")
    }

    override val baseUrl: Flow<String?> = context.authDataStore.data.map { it[Keys.BASE_URL] }
    override val accessToken: Flow<String?> = context.authDataStore.data.map { it[Keys.ACCESS_TOKEN] }
    override val loggedIn: Flow<Boolean> = context.authDataStore.data.map { !it[Keys.REFRESH_TOKEN].isNullOrBlank() }
    override val isManager: Flow<Boolean> = context.authDataStore.data.map { it[Keys.IS_MANAGER] ?: false }
    override val userName: Flow<String?> = context.authDataStore.data.map { it[Keys.USER_NAME] }

    override suspend fun currentBaseUrl(): String? = baseUrl.first()
    override suspend fun currentAccessToken(): String? = accessToken.first()
    override suspend fun currentRefreshToken(): String? = context.authDataStore.data.first()[Keys.REFRESH_TOKEN]
    override suspend fun currentDeviceId(): String =
        context.authDataStore.data.first()[Keys.DEVICE_ID] ?: newDeviceId()

    /** Access 到期时间（客户端签发时同步解出 exp），用于「到期前 10 分钟主动续期」。 */
    override suspend fun currentAccessExpiresAt(): Long =
        context.authDataStore.data.first()[Keys.ACCESS_EXPIRES_AT] ?: 0L

    override suspend fun saveBaseUrl(url: String) {
        context.authDataStore.edit { it[Keys.BASE_URL] = url.trimEnd('/') }
    }

    override suspend fun saveLogin(
        baseUrl: String,
        userName: String,
        accessToken: String,
        refreshToken: String,
        refreshExpiresAt: Long,
        accessExpiresAt: Long,
        isManager: Boolean
    ) {
        context.authDataStore.edit {
            it[Keys.BASE_URL] = baseUrl.trimEnd('/')
        it[Keys.USER_NAME] = userName
        it[Keys.ACCESS_TOKEN] = accessToken
        it[Keys.REFRESH_TOKEN] = refreshToken
        it[Keys.REFRESH_EXPIRES_AT] = refreshExpiresAt
        it[Keys.ACCESS_EXPIRES_AT] = accessExpiresAt
            it[Keys.IS_MANAGER] = isManager
        }
    }

    override suspend fun updateTokens(accessToken: String, refreshToken: String, refreshExpiresAt: Long, accessExpiresAt: Long) {
        context.authDataStore.edit {
            it[Keys.ACCESS_TOKEN] = accessToken
            it[Keys.REFRESH_TOKEN] = refreshToken
            it[Keys.REFRESH_EXPIRES_AT] = refreshExpiresAt
            it[Keys.ACCESS_EXPIRES_AT] = accessExpiresAt
        }
    }

    /** 管理员标识随令牌轮换同步更新（claim 正向读取）。 */
    override suspend fun updateManagerFlag(isManager: Boolean) {
        context.authDataStore.edit { it[Keys.IS_MANAGER] = isManager }
    }

    override suspend fun currentRefreshExpiresAt(): Long =
        context.authDataStore.data.first()[Keys.REFRESH_EXPIRES_AT] ?: 0L

    /** 登出清本地（含 Room 缓存由各 Repository 另行清理）；记住的账号密码不随登出清除。 */
    override suspend fun clear() {
        context.authDataStore.edit { all ->
            val deviceId = all[Keys.DEVICE_ID]
            val baseUrl = all[Keys.BASE_URL]
            val rememberedUserName = all[Keys.REMEMBER_USER_NAME]
            val rememberedCipher = all[Keys.REMEMBER_PASSWORD_CIPHER]
            val remembered = all[Keys.REMEMBER_PASSWORD]
            all.clear()
            if (deviceId != null) all[Keys.DEVICE_ID] = deviceId
            if (baseUrl != null) all[Keys.BASE_URL] = baseUrl
            if (rememberedUserName != null) all[Keys.REMEMBER_USER_NAME] = rememberedUserName
            if (rememberedCipher != null) all[Keys.REMEMBER_PASSWORD_CIPHER] = rememberedCipher
            if (remembered != null) all[Keys.REMEMBER_PASSWORD] = remembered
        }
    }

    // ---- 登录页「记住账号密码」（只在具体类上提供：SessionManager 等纯 JVM 依赖 TokenStoreApi 不受影响）----

    /** 登录成功后按勾选状态落盘：账号恒记；勾选存密文、取消勾选清密文。 */
    suspend fun saveRemembered(userName: String, passwordCipher: String?, remember: Boolean) {
        context.authDataStore.edit {
            it[Keys.REMEMBER_USER_NAME] = userName
            it[Keys.REMEMBER_PASSWORD] = remember
            if (passwordCipher != null) {
                it[Keys.REMEMBER_PASSWORD_CIPHER] = passwordCipher
            } else {
                it.remove(Keys.REMEMBER_PASSWORD_CIPHER)
            }
        }
    }

    /** 密文解密失败时的自愈清除（换机迁移/Keystore 密钥被删后按「未保存」处理）。 */
    suspend fun clearRememberedPassword() {
        context.authDataStore.edit { it.remove(Keys.REMEMBER_PASSWORD_CIPHER) }
    }

    fun rememberedUserNameBlocking(): String? =
        kotlinx.coroutines.runBlocking { context.authDataStore.data.first()[Keys.REMEMBER_USER_NAME] }

    fun rememberedPasswordCipherBlocking(): String? =
        kotlinx.coroutines.runBlocking { context.authDataStore.data.first()[Keys.REMEMBER_PASSWORD_CIPHER] }

    /** 勾选状态本身也记忆；首次（无值）默认勾选。 */
    fun rememberPasswordBlocking(): Boolean =
        kotlinx.coroutines.runBlocking { context.authDataStore.data.first()[Keys.REMEMBER_PASSWORD] ?: true }

    private suspend fun newDeviceId(): String {
        val id = UUID.randomUUID().toString()
        context.authDataStore.edit { it[Keys.DEVICE_ID] = id }
        return id
    }
}

/**
 * 会话已读水位存取抽象（PrefsStore 的水位子集，2026-09-21 双端同步批次）：
 * 供 ChatRepository 等纯 JVM 逻辑依赖，单测可注入内存实现（同 [TokenStoreApi] 模式）。
 * 键域 = 会话键（默认会话空串，DataStore 落盘 "_" 占位由 codec 处理）；只进语义由实现保证。
 */
interface SessionReadStore {
    /** 当前全部会话水位（键 = 会话键，默认会话空串）。 */
    suspend fun readWatermarks(): Map<String, Long>

    /** 只进推进单会话水位（低于当前值时无操作）。 */
    suspend fun advanceReadWatermark(sessionId: String, seq: Long)
}

/**
 * 客户端偏好：生物识别门禁、通知偏好镜像、语言/主题等（纯本地）。
 */
class PrefsStore(private val context: Context) : SessionReadStore {

    private object Keys {
        val BIOMETRIC_LOCK = booleanPreferencesKey("biometric_lock")
        val BIOMETRIC_ENABLED = booleanPreferencesKey("biometric_enabled")
        val TASK_PUSH = booleanPreferencesKey("pref_task_push")
        val SYSTEM_PUSH = booleanPreferencesKey("pref_system_push")
        val SECURITY_PUSH = booleanPreferencesKey("pref_security_push")
        val DND_START = stringPreferencesKey("pref_dnd_start")
        val DND_END = stringPreferencesKey("pref_dnd_end")
        val LOCAL_CHAT_REPAIR = booleanPreferencesKey("local_chat_repair_done")
        val SESSION_READ_SEQS = stringPreferencesKey("session_read_seqs")
        val SESSION_BASELINE = booleanPreferencesKey("session_baseline_done")
    }

    val biometricLock: Flow<Boolean> = context.prefsDataStore.data.map { it[Keys.BIOMETRIC_LOCK] ?: false }
    val biometricEnabled: Flow<Boolean> = context.prefsDataStore.data.map { it[Keys.BIOMETRIC_ENABLED] ?: true }

    suspend fun setBiometricLock(enabled: Boolean) = context.prefsDataStore.edit { it[Keys.BIOMETRIC_LOCK] = enabled }

    val taskPush: Flow<Boolean> = context.prefsDataStore.data.map { it[Keys.TASK_PUSH] ?: true }
    val systemPush: Flow<Boolean> = context.prefsDataStore.data.map { it[Keys.SYSTEM_PUSH] ?: true }
    val securityPush: Flow<Boolean> = context.prefsDataStore.data.map { it[Keys.SECURITY_PUSH] ?: true }
    val dndStart: Flow<String?> = context.prefsDataStore.data.map { it[Keys.DND_START] }
    val dndEnd: Flow<String?> = context.prefsDataStore.data.map { it[Keys.DND_END] }

    /**
     * 保存通知偏好镜像（服务端 t_app_notify_setting 的本地副本，[AppPushHandler] 抑制判定读这里）。
     * 免打扰时段支持清空：null/空白一律 **remove** 键——早先写的是 `dndStart?.let{...}`，
     * 传 null 直接跳过，于是「清掉免打扰」的操作在本地永远生效不了（旧时段继续抑制通知）。
     */
    suspend fun saveNotifyPrefs(task: Boolean, system: Boolean, security: Boolean, dndStart: String?, dndEnd: String?) =
        context.prefsDataStore.edit {
            it[Keys.TASK_PUSH] = task
            it[Keys.SYSTEM_PUSH] = system
            it[Keys.SECURITY_PUSH] = security
            val start = dndStart?.trim().orEmpty()
            val end = dndEnd?.trim().orEmpty()
            if (start.isEmpty()) it.remove(Keys.DND_START) else it[Keys.DND_START] = start
            if (end.isEmpty()) it.remove(Keys.DND_END) else it[Keys.DND_END] = end
        }

    /**
     * 一次性本地会话缓存重建标记：旧版本曾用自造 seq 写会话行与通知，残留行会与新数据重复。
     * 标记为 false 时由补拉入口清空本地缓存并从服务端全量重建（服务端是唯一事实源）。
     */
    val localChatRepairDone: Flow<Boolean> =
        context.prefsDataStore.data.map { it[Keys.LOCAL_CHAT_REPAIR] ?: false }

    suspend fun markLocalChatRepairDone() =
        context.prefsDataStore.edit { it[Keys.LOCAL_CHAT_REPAIR] = true }

    /**
     * 会话已读水位（sessionId → 已读到的最大 seq）：会话未读数的本地基准。
     * 编解码与推进见 [SessionWaterlineCodec]（"id:seq" 串存储；默认会话空串键以 "_" 占位）。
     */
    val sessionReadSeqs: Flow<Map<String, Long>> =
        context.prefsDataStore.data.map { prefs -> SessionWaterlineCodec.decode(prefs[Keys.SESSION_READ_SEQS]) }

    override suspend fun readWatermarks(): Map<String, Long> = sessionReadSeqs.first()

    override suspend fun advanceReadWatermark(sessionId: String, seq: Long) =
        setSessionReadSeq(sessionId, seq)

    /**
     * 推进单个会话的已读水位（只进不退），**其余会话条目原样保留**——
     * 写路径必须整表合并回写，任何按落盘键做的「清理」都会误删其他会话的已读位置。
     */
    suspend fun setSessionReadSeq(sessionId: String, seq: Long) {
        context.prefsDataStore.edit { prefs ->
            SessionWaterlineCodec.advance(prefs[Keys.SESSION_READ_SEQS], sessionId, seq)
                ?.let { prefs[Keys.SESSION_READ_SEQS] = it }
        }
    }

    /**
     * 摘除单个会话的已读水位（§4-13）：清空/删除会话时同批调用，其余会话条目原样保留。
     * 与 [setSessionReadSeq] 一样走解码域整表回写，绝不误删其他会话的已读位置。
     */
    suspend fun removeSessionReadSeq(sessionId: String) {
        context.prefsDataStore.edit { prefs ->
            SessionWaterlineCodec.prune(prefs[Keys.SESSION_READ_SEQS], sessionId)
                ?.let { prefs[Keys.SESSION_READ_SEQS] = it }
        }
    }

    /**
     * 默认会话历史消息的一次性基线标记（见 AppPushHandler）：分栏改造前所有消息都落在
     * 默认会话，按「无水位 = 全部未读」会把它整段历史算成未读（角标 99+ 的噪音来源）。
     * 首次同步完成后把默认会话水位推到当时的最大 seq，此后再有新消息照常计数。
     */
    val sessionBaselineDone: Flow<Boolean> =
        context.prefsDataStore.data.map { it[Keys.SESSION_BASELINE] ?: false }

    suspend fun markSessionBaselineDone() =
        context.prefsDataStore.edit { it[Keys.SESSION_BASELINE] = true }
}

/**
 * 会话已读水位的编解码器（DataStore Preferences 无映射类型，以 "id:seq,id:seq" 串存储）。
 * 默认会话的 sessionId 是空串，**落盘一律用 "_" 占位、读出再还原**——两侧必须对称，
 * 否则空键条目（":seq"）会在下轮解码被当作畸形丢弃，对应会话的未读数全部失效。
 */
object SessionWaterlineCodec {

    /** 默认会话（空串 sessionId）的落盘占位键。 */
    const val DEFAULT_SESSION_KEY = "_"

    /** sessionId → 落盘键（空串归一为占位键；任务 Id 为 GUID，不含分隔符）。 */
    fun storageKey(sessionId: String): String = sessionId.ifEmpty { DEFAULT_SESSION_KEY }

    /** 落盘键 → sessionId（占位键还原为空串，消费方直接以 sessionId 查表）。 */
    fun decode(raw: String?): Map<String, Long> =
        raw?.split(',')?.mapNotNull { entry ->
            val idx = entry.lastIndexOf(':')
            if (idx <= 0) {
                return@mapNotNull null
            }
            val seq = entry.substring(idx + 1).toLongOrNull() ?: return@mapNotNull null
            val stored = entry.substring(0, idx)
            (if (stored == DEFAULT_SESSION_KEY) "" else stored) to seq
        }?.toMap() ?: emptyMap()

    /** 映射 → 落盘串（空键对称归一为占位键，保证 decode 往返不丢条目）。 */
    fun encode(map: Map<String, Long>): String =
        map.entries.joinToString(",") { entry ->
            "${storageKey(entry.key)}:${entry.value}"
        }

    /**
     * 单会话水位推进（写路径唯一入口）：把 sessionId 的水位推到 seq（只进不退），
     * 返回新的落盘串；无需变更时返回 null（调用方跳过写入）。
     *
     * **必须在解码域（sessionId）里改、整表合并回写**：默认会话解码后正是空串键，
     * 写路径若顺手 `remove("")` 清理「空键残留」，就会把默认会话的已读位置一并删掉 ——
     * 表现为「看过任一任务会话 → 回列表 → 默认会话说 99+」（其整段历史被判未读）。
     * 旧格式的畸形条目（":seq"、空段）由 [decode] 直接丢弃，写路径无需再清理。
     */
    fun advance(raw: String?, sessionId: String, seq: Long): String? {
        val map = decode(raw).toMutableMap()
        if ((map[sessionId] ?: 0L) >= seq) {
            return null
        }
        map[sessionId] = seq
        return encode(map)
    }

    /**
     * 单会话水位摘除（§4-13）：清空/删除会话时同批移除其已读水位键，返回新落盘串；
     * 键不存在返回 null（无需变更，调用方跳过写入）。必须在解码域（sessionId 键）里改、
     * 整表回写——与 [advance] 对称，默认会话（空串键）同样能正确摘除。
     * 清空后不摘会留隐患：服务端删除尾部后会从剩余 MAX+1 复用号段，残留的高水位会把
     * 复用号段上的新消息误判为已读。
     */
    fun prune(raw: String?, sessionId: String): String? {
        val map = decode(raw).toMutableMap()
        if (!map.containsKey(sessionId)) {
            return null
        }
        map.remove(sessionId)
        return encode(map)
    }
}
