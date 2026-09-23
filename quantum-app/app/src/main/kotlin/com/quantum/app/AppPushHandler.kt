package com.quantum.app

import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import com.quantum.app.core.common.DndWindow
import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.network.dto.NotifySettingDto
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.ws.AppWsClient
import com.quantum.app.core.network.ws.WsFrame
import com.quantum.app.core.network.ws.WsState
import com.quantum.app.core.push.PushPayload
import com.quantum.app.core.push.SystemNotifier
import com.quantum.app.core.storage.db.NotificationEntity
import com.quantum.app.core.storage.prefs.PrefsStore
import com.quantum.app.feature.chat.ChatRepository
import com.quantum.app.feature.chat.ChatScreenTracker
import com.quantum.app.feature.notify.NotifyRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.first
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 消息到达中枢（smartbook 同款方案，厂商离线推送已整体移除）：
 * - **WS 常驻收集者**：message/notify 帧由本单例（随 Application 存活）写入本地会话表——
 *   帧流是无 replay 的 SharedFlow，只挂在页面 ViewModel 上会让非会话页/后台到达的帧永久丢失；
 * - **站内通知统一进会话流**：notify 帧双写通知表（桌面小组件/去重）与会话表（App 内唯一展示面）；
 * - **弹本地通知（按会话页粒度抑制）**：message/notify 帧到达时，除非目标会话页正被
 *   用户看着（[ChatScreenTracker]），否则弹系统通知——App 在前台其他页面也弹
 *   （2026-09-18 修正：旧逻辑对整个前台一律抑制，用户在任何页面都收不到提醒）；
 * - **补拉**：重连成功/回前台触发增量同步（帧流无 replay，断线期间只能靠补拉找回）；
 * - **退后台不断连**：WS 保持连接（服务端 25s 心跳 + 客户端指数退避重连），后台实时性
 *   依赖进程存活——用户侧以「锁定后台/自启动」保活（厂商离线推送的替代）。
 * - 免打扰时段/分类开关抑制**本地通知弹出**（落库与在线更新不受影响，打开 App 必可见）。
 */
@Singleton
class AppPushHandler @Inject constructor(
    private val appApi: AppApi,
    private val adminApi: com.quantum.app.core.network.api.AdminApi,
    private val wsClient: AppWsClient,
    private val notifyRepository: NotifyRepository,
    private val chatRepository: ChatRepository,
    private val prefsStore: PrefsStore,
    private val sessionManager: com.quantum.app.core.network.session.SessionManager,
    private val systemNotifier: SystemNotifier
) {

    @Volatile
    var foreground: Boolean = false
        private set

    /**
     * 会话缓存是否已载入（SessionManager.load 完成）。
     * 冷启动时 onStart 可能早于 load() 到达，此刻 connect() 会因 baseUrl/token 为空静默早退；
     * load 完成后由 [onSessionReady] 补建，两个入口无论谁先到都能把 WS 连上。
     */
    @Volatile
    private var sessionReady = false

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    /** §4-9：帧落库/弹通知的独立消费队列——收集者只 trySend，慢操作不反压帧流。 */
    private val frameChannel = Channel<WsFrame>(capacity = 64)

    /** 补拉节流时间戳（重连与回前台常在同一时刻触发）。§4-9：跨 Default 协程读写，补 @Volatile。 */
    @Volatile
    private var lastCatchUpAt = 0L

    init {
        // 常驻帧收集者：frames 为无 replay 的 SharedFlow，必须与 App 同生命周期
        // （本单例随 Application 创建）常驻收集，不能只挂在某个 Screen 的 ViewModel 上。
        // §4-9：收集协程只做非阻塞 trySend，把 Room 写/弹通知交给独立消费者，慢操作不再反压帧流。
        scope.launch {
            wsClient.frames.collect { frame -> frameChannel.trySend(frame) }
        }
        scope.launch {
            for (frame in frameChannel) {
                when (frame.type) {
                    // 会话消息：常驻落库（会话页 UI 由 Room Flow 自动刷新）；
                    // 除目标会话页在屏外都弹通知——脚本内容推送（热点速览/音乐搜索结果等）
                    // 都是 message 帧，不弹的话用户在非会话页/后台完全无感知
                    "message" -> runCatching {
                        chatRepository.onWsMessage(frame)
                        onMessageFrameNotify(frame)
                    }
                    "notify" -> runCatching {
                        // 通知表（桌面小组件/去重）+ 会话表（站内通知在 App 内的唯一展示面）+ 本地通知
                        onNotifyFrame(frame)
                        chatRepository.onWsNotify(frame)
                    }
                    // 跨会话触发消息迁移：搬本地行 + 发切换事件（不弹通知——迁移的是用户自己的已读行）
                    "message_moved" -> runCatching { chatRepository.onWsMessageMoved(frame) }
                    // 对端已读（2026-09-21 双端同步）：只进推进本地水位，DataStore Flow 令
                    // 会话列表角标/底栏红点自动重算；不弹通知（不产生新消息）
                    "session_read" -> runCatching { chatRepository.onSessionRead(frame.session, frame.seq) }
                }
            }
        }
        // 连接恢复即补拉：WS 断开期间服务端帧不会到达，只能靠增量同步找回
        scope.launch {
            var wasOpen = false
            wsClient.state.collect { state ->
                val open = state == WsState.Open
                if (open && !wasOpen) {
                    catchUp()
                }
                wasOpen = open
            }
        }
        // 登出（用户登出/Refresh 过期被踢）立即断 WS，防止旧账号连接残留
        scope.launch {
            sessionManager.events.collect { event ->
                if (event == com.quantum.app.core.network.session.SessionManager.Event.LoggedOut) {
                    wsClient.disconnect()
                }
            }
        }
        ProcessLifecycleOwner.get().lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) {
                foreground = true
                wsClient.connect(scope)
                // 回前台先补拉（断线/被杀期间的变更找回），再等帧实时到达
                scope.launch { catchUp() }
            }

            override fun onStop(owner: LifecycleOwner) {
                // 退后台保持 WS 连接：后台实时通知全靠这条链路（进程被杀则退化为回前台补拉）
                foreground = false
            }
        })
    }

    /**
     * 补拉入口（重连成功 / 回前台）：先做一次性本地缓存重建，再增量同步会话与通知元数据。
     * 5s 节流：重连与回前台往往几乎同时发生。
     */
    private suspend fun catchUp() {
        // §4-9：单调时钟（elapsedRealtime 自开机递增、不受校时/时区影响）；
        // 墙钟 currentTimeMillis 遇用户回拨系统时间会让差值变负、节流误判放行。
        val now = android.os.SystemClock.elapsedRealtime()
        if (now - lastCatchUpAt < CATCH_UP_THROTTLE_MS) {
            return
        }
        lastCatchUpAt = now
        repairLocalCacheOnce()
        // 补拉通知的基准水位：同步前本地最大 seq（repair 清库后为 0，下面的 >0 守卫防首装全量误报）
        val beforeSeq = chatRepository.latestSeq()
        // 基线化只在同步成功的那一次执行（见 baselineDefaultSessionOnce）
        val synced = runCatching { chatRepository.syncAll() }.isSuccess
        runCatching { notifyRepository.syncFromServer() }
        // 通知偏好镜像：抑制判定读的是本地那份，每次回前台/重连与服务端对齐一次
        // （在 Web/另一台设备改了免打扰，本机不该继续按旧时段静音）
        mirrorNotifyPrefs()
        if (synced) {
            baselineDefaultSessionOnce()
        }
        // 后台重连场景：SharedFlow 不重放，断线期间错过的 message 帧只能靠 REST 找回，
        // 这里按新增行补弹通知（前台免弹——用户正在看，入屏即见）
        if (!foreground && beforeSeq > 0) {
            runCatching {
                chatRepository.newestBotMessagePerSession(beforeSeq).forEach { (session, row) ->
                    notifySessionMessage(session, row.contentType, row.content)
                }
            }
        }
    }

    /**
     * 一次性本地缓存重建：旧版本用自造 seq 写过会话行与通知，残留行会与新数据重复/错序。
     * 仅执行一次（标记落 DataStore），清空后由本次补拉的全量同步重建（服务端是唯一事实源）。
     */
    private suspend fun repairLocalCacheOnce() {
        if (prefsStore.localChatRepairDone.first()) {
            return
        }
        chatRepository.clearLocal()
        notifyRepository.clearLocal()
        prefsStore.markLocalChatRepairDone()
    }

    /**
     * 默认会话历史一次性基线化：分栏改造前所有消息都落默认会话（存量消息、system/security 通知镜像），
     * 而「无已读水位 = 整段历史算未读」会把它们全算成未读 → 默认会话角标恒 99+（纯噪音）。
     * 首次同步完成后把默认会话水位推到当时的最大 seq（既有历史视为已读），此后新消息照常计数。
     *
     * 只在**同步成功**的那次打标记：同步失败时本地可能还是空库，此刻打标记会让随后拉回的全量
     * 历史永远停在「无水位」状态，问题照旧。任务会话不做基线化——脚本首推就是需要提醒的新消息。
     */
    private suspend fun baselineDefaultSessionOnce() {
        if (prefsStore.sessionBaselineDone.first()) {
            return
        }
        chatRepository.sessionMaxSeq("")?.let { prefsStore.setSessionReadSeq("", it) }
        prefsStore.markSessionBaselineDone()
    }

    /**
     * message 帧通知：除「该会话页正被用户看着」外一律弹系统通知——
     * 用户在任务页/管理页等其他前台页面时脚本推送同样要提醒（2026-09-18 用户实测反馈
     * 「100% 无通知」的根因是旧逻辑对整个 App 前台一律抑制）。
     * 每个会话一条（id 按 session 固定覆盖，避免财经快讯类批量推送刷屏）。
     */
    private suspend fun onMessageFrameNotify(frame: WsFrame) {
        if (frame.msgId == null) {
            return
        }
        notifySessionMessage(frame.session.orEmpty(), frame.contentType, frame.content)
    }

    /**
     * 按会话弹一条通知：标题用任务名（懒加载缓存），点按深链直达对应会话；
     * 抑制仅两条——目标会话页正在屏上（用户正看着它）、免打扰/分类开关关闭
     * （默认会话归 system、任务会话归 task，与 Web 端偏好语义一致）。
     * WS 实时帧与重连补拉两条路径共用。
     */
    private suspend fun notifySessionMessage(sessionKey: String, contentType: String?, content: String?) {
        if (ChatScreenTracker.visibleSessionId == sessionKey) {
            return
        }
        if (suppressedByPrefs(if (sessionKey.isEmpty()) "system" else "task")) {
            return
        }
        val title = sessionTitle(sessionKey)
        val preview = when (contentType) {
            "image" -> "[图片]"
            "video" -> "[视频]"
            "file" -> "[文件]"
            "audio" -> "[音频]"
            else -> content.orEmpty().lineSequence().firstOrNull().orEmpty().takeIf { it.isNotBlank() } ?: "[消息]"
        }
        systemNotifier.post(
            category = "chat",
            title = title,
            content = preview,
            jump = if (sessionKey.isEmpty()) "quantum://chat" else "quantum://session/$sessionKey",
            msgId = "session-$sessionKey"
        )
    }

    /** 会话标题：默认会话固定文案；任务会话用任务名（进程内懒加载一次，失败回退短 Id）。 */
    private suspend fun sessionTitle(sessionKey: String): String {
        if (sessionKey.isEmpty()) {
            return "默认会话"
        }
        ensureTaskNames()
        return taskNames[sessionKey] ?: "任务 ${sessionKey.take(8)}"
    }

    @Volatile
    private var taskNamesLoaded = false

    @Volatile
    private var taskNames: Map<String, String> = emptyMap()

    private suspend fun ensureTaskNames() {
        if (taskNamesLoaded) {
            return
        }
        runCatching {
            adminApi.tasks(pageIndex = 1, pageSize = 500).unwrap()
        }.onSuccess { page ->
            taskNames = page.data.associate { it.id to it.name }
            taskNamesLoaded = true
        }
    }

    /**
     * WS 在线直推 notify 帧：msgId 幂等入库；除「目标会话页正在屏上」外弹系统通知，
     * 会话页在屏时仅入库（Room Flow 自动刷新 UI）。
     */
    private suspend fun onNotifyFrame(frame: WsFrame) {
        val msgId = frame.msgId ?: return
        onPayload(
            PushPayload(
                category = frame.category ?: "system",
                title = frame.title.orEmpty(),
                content = frame.content.orEmpty(),
                jump = frame.jump,
                msgId = msgId
            ),
            session = frame.session.orEmpty()
        )
    }

    /** 登录成功后由 Shell 调用：前台态立即建连（后台态留给 onStart 兜底）。 */
    fun onLogin() {
        sessionReady = true
        if (foreground && sessionManager.accessToken() != null) {
            wsClient.connect(scope)
            // 登录即补拉：首登后主界面的会话列表/未读依赖这次同步（此前只有 onStart 才补拉，
            // 而冷启动 onStart 早于会话载入，token 为空时那次补拉是空跑）
            scope.launch { catchUp() }
        }
    }

    /**
     * 会话缓存载入完成（冷启动恢复会话，Application 的 load() 之后调用）：
     * 补上冷启动竞态中错过的建连——onStart 先到时 connect() 因令牌未载入而空转，
     * 此处 token 已就绪立即补建；若此刻仍在前台之外则留给下一次 onStart。
     */
    fun onSessionReady() {
        sessionReady = true
        if (foreground && sessionManager.accessToken() != null) {
            wsClient.connect(scope)
        }
    }

    /** notify 帧处理：幂等入库 → 会话页在屏抑制 → 偏好抑制 → 弹本地通知。返回是否已弹。 */
    private suspend fun onPayload(payload: PushPayload, session: String = ""): Boolean {
        val msgId = payload.msgId ?: return false
        val entity = NotificationEntity(
            msgId = msgId,
            title = payload.title,
            content = payload.content,
            category = payload.category,
            jump = payload.jump,
            createdAt = com.quantum.app.core.common.TimeUtil.nowServerStyle()
        )
        val isNew = notifyRepository.insertIfAbsent(entity)
        if (ChatScreenTracker.visibleSessionId == session) {
            // 目标会话页在屏（通知镜像行落该会话）：仅入库不弹
            return false
        }
        if (!isNew) {
            return false
        }
        if (suppressedByPrefs(payload.category)) {
            // 免打扰时段/分类开关关闭：抑制通知弹出，通知中心（App 内）仍可见
            return false
        }
        systemNotifier.post(payload.category, payload.title, payload.content, payload.jump, msgId)
        return true
    }

    private suspend fun suppressedByPrefs(category: String): Boolean {
        val task = prefsStore.taskPush.first()
        val system = prefsStore.systemPush.first()
        val security = prefsStore.securityPush.first()
        val allowedByCategory = when {
            category == "task" -> task
            category == "security" -> security
            else -> system
        }
        if (!allowedByCategory) {
            return true
        }
        // 免打扰判定（含跨零点、脏数据 fail-open）在 core:common 的 DndWindow 里，可单测
        return DndWindow.isActive(
            now = java.time.LocalTime.now(),
            start = prefsStore.dndStart.first(),
            end = prefsStore.dndEnd.first()
        )
    }

    /** 通知偏好镜像（登录后拉一次服务端 t_app_notify_setting，本地缓存用于离线判断）。 */
    suspend fun mirrorNotifyPrefs() {
        runCatching {
            val prefs: NotifySettingDto = appApi.notifySetting().unwrap()
            prefsStore.saveNotifyPrefs(
                task = prefs.taskPush,
                system = prefs.systemPush,
                security = prefs.securityPush,
                dndStart = prefs.dndStart,
                dndEnd = prefs.dndEnd
            )
        }
    }

    companion object {
        /** 补拉节流：重连与回前台可能紧随触发，避免同一时刻多次全量同步。 */
        private const val CATCH_UP_THROTTLE_MS = 5_000L
    }
}
