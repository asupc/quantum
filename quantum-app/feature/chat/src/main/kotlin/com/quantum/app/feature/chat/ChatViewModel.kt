package com.quantum.app.feature.chat

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.media.PlayerManager
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.api.unwrapOrNull
import com.quantum.app.core.network.dto.CommandDto
import com.quantum.app.core.network.session.SessionManager
import com.quantum.app.core.network.ws.AppWsClient
import com.quantum.app.core.network.ws.WsFrame
import com.quantum.app.core.storage.db.ChatMessageEntity
import com.quantum.app.core.storage.prefs.PrefsStore
import com.quantum.app.core.storage.db.NotificationDao
import com.quantum.app.core.storage.db.NotificationEntity
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * 会话页状态：消息流（Room 直供）+ 同步/已读回执 + 快捷指令面板。
 * - 本地 2s 节流与服务端限流文案对齐；
 * - 已读回执：会话页可见且滚动到底时分批触发（节流 3s）；
 * - 未读角标：登录/回前台/收消息后刷新。
 */
@HiltViewModel
class ChatViewModel @Inject constructor(
    private val chatRepository: ChatRepository,
    private val wsClient: AppWsClient,
    private val sessionManager: SessionManager,
    private val adminApi: AdminApi,
    private val notificationDao: NotificationDao,
    private val prefsStore: PrefsStore,
    savedStateHandle: androidx.lifecycle.SavedStateHandle,
    val imageLoader: coil.ImageLoader,
    /** 端内播放内核：视频消息/可播文件点按后由 MediaViewerDialog 驱动（全局单例）。 */
    val playerManager: PlayerManager
) : ViewModel() {

    /** 会话键（路由参数 sessionId；空串/缺省 = 默认会话）。 */
    val sessionId: String = savedStateHandle.get<String>("sessionId") ?: ""

    /** 会话标题：空键固定「默认会话」；其余以会话键原文兜底（可能是共享会话名，本身可读）。 */
    private val _title = MutableStateFlow(if (sessionId.isEmpty()) "默认会话" else sessionId)
    val title: StateFlow<String> = _title.asStateFlow()

    private val pageSize = MutableStateFlow(50)

    /** 消息流：Room 分页 → UI（seq 倒序加载，UI 端反转显示）；按会话键过滤。 */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    val messages: StateFlow<List<ChatMessageEntity>> = pageSize
        .flatMapLatest { chatRepository.sessionMessages(sessionId, limit = it, offset = 0) }
        .map { list -> list.sortedBy { it.seq } }
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())

    init {
        if (sessionId.isNotEmpty()) {
            // 任务会话标题：优先任务名；任务已删除/自定义会话名时保持会话键原文兜底。
            // 会话分组后会话键可能是会话名而非任务 Id：api/Task/{键} 查无任务时服务端返回
            // Code=200 + Data=null，属合法空响应，用 unwrapOrNull 折叠成 null（unwrap 已挡 null，
            // 不再会把 null 漏成非空类型）。网络/业务失败经 runCatching 一并归 null。
            viewModelScope.launch {
                val task: com.quantum.app.core.network.dto.TaskDto? =
                    runCatching { adminApi.taskDetail(sessionId).unwrapOrNull() }.getOrNull()
                val name = task?.name
                if (!name.isNullOrBlank()) {
                    _title.value = name
                }
            }
        }
        // 进入会话即推进本地已读水位（会话列表未读角标清零）
        viewModelScope.launch {
            chatRepository.sessionMaxSeq(sessionId)?.let { prefsStore.setSessionReadSeq(sessionId, it) }
        }
    }

    /**
     * 通知元数据（按 msgId）：会话流里的通知卡片据此渲染分类/标题/jump。
     * 会话行只带合成正文（`【标题】\n内容`），分类与 jump 只存在于通知表——两表共用 msgId 是既有约定；
     * 2.3 起按需查询：只对当前会话通知类消息的 msgId 发一条 IN 查询（空集短路不发查询），
     * 不再订阅 notification 全表 Flow；关联不到时卡片降级为正文文本，不影响展示。
     */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    val notifyMeta: StateFlow<Map<String, NotificationEntity>> = notificationMetaFlow(messages) {
        notificationDao.byMsgIds(it)
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyMap())

    private val _unread = MutableStateFlow(0L)
    val unread: StateFlow<Long> = _unread.asStateFlow()

    private val _sending = MutableStateFlow(false)
    val sending: StateFlow<Boolean> = _sending.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    private val _quickCommands = MutableStateFlow<List<CommandDto>>(emptyList())
    val quickCommands: StateFlow<List<CommandDto>> = _quickCommands.asStateFlow()

    private var readReportJob: Job? = null
    // §4-5 已读回执节流：每次滚动只更新「已看到的最大 seq」，单一节流 job 统一写本地水位 + 上报，
    // 发送时若水位已前进则立即补报（旧实现「有 job 就丢弃后续调用」会让上报 seq 滞后 3s）。
    @Volatile private var pendingReadSeq = 0L
    @Volatile private var reportedReadSeq = 0L
    private var wsJob: Job? = null
    private var movesJob: Job? = null

    init {
        observeWs()
        refresh()
        loadQuickCommands()
    }

    /** 拉取快捷指令库（GET api/Command 全量，PageIndex/PageSize 风格）。 */
    fun loadQuickCommands() {
        viewModelScope.launch {
            runCatching {
                adminApi.commands(pageIndex = 1, pageSize = 200).unwrap()
            }.onSuccess { _quickCommands.value = it.data }
        }
    }

    /**
     * WS 帧入口：**帧落库由常驻 AppPushHandler 负责**（帧流是无 replay 的 SharedFlow，
     * 只挂在页面 ViewModel 上会让非会话页/后台到达的帧被永久丢弃），本页只做页面相关刷新与错误提示。
     */
    fun observeWs() {
        wsJob?.cancel()
        wsJob = viewModelScope.launch {
            wsClient.frames.collect { frame ->
                when (frame.type) {
                    "message", "notify" -> refreshUnread()
                    // 服务端拒收/限流文案（如 2s 限流）：必须让用户看见，不能当发送成功
                    "error" -> _toast.value = frame.content ?: "操作失败，请稍后重试"
                }
            }
        }
    }

    /** 登录/回到前台调用：全量同步 + 未读刷新。 */
    fun refresh() {
        viewModelScope.launch {
            runCatching { chatRepository.syncAll() }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
            refreshUnread()
        }
    }

    /**
     * 跨会话触发迁移自动切换（2026-09-18 批次）：本会话是迁移来源时回调目标会话键——
     * 由 Screen 接导航（与手点列表项同款路由，导航新建会话页实例、状态天然干净）；
     * 其余会话/列表态不回调（只搬数据不打断，D4）。
     */
    fun observeSessionMoves(onSwitch: (String) -> Unit) {
        movesJob?.cancel()
        movesJob = viewModelScope.launch {
            chatRepository.sessionMoves.collect { event ->
                if (event.from == sessionId) {
                    onSwitch(event.to)
                }
            }
        }
    }

    override fun onCleared() {
        super.onCleared()
        wsJob?.cancel()
        movesJob?.cancel()
        readReportJob?.cancel()
    }

    fun refreshUnread() {
        viewModelScope.launch {
            runCatching { chatRepository.unreadCount() }.onSuccess { _unread.value = it }
        }
    }

    /**
     * 发送指令：仓库发送（WS 优先 REST 兜底）→ 小同步回包渲染（2s 节流已移除，2026-09-19）。
     */
    fun send(content: String) {
        launchSend(content.trim())
    }

    /**
     * 共享发送核（手动输入与选项点选共用）：失败文案、未读刷新。
     * targetTask：点选代发的来源任务 Id（透传 command 帧做精确路由），手打缺省 null。
     * pickLabel：点选代发的选项 label——回显行渲染为居中系统提示「已选择「label」」，手打缺省 null。
     * 2s 发送节流已于 2026-09-19 按所有者要求移除（客户端与服务端均不限频）：
     * 仅手打保留 in-flight 发送互斥（防同一条在请求未返回时重复提交），点选代发豁免——连点不拦。
     */
    private fun launchSend(
        content: String,
        targetTask: String? = null,
        pickLabel: String? = null,
        onAccepted: (suspend () -> Unit)? = null
    ) {
        val pick = pickLabel != null
        if (content.isEmpty() || (!pick && _sending.value)) {
            return
        }
        if (!pick) {
            _sending.value = true
        }
        viewModelScope.launch {
            try {
                val failure = chatRepository.sendCommand(
                    content,
                    sessionId = sessionId,
                    targetTask = targetTask,
                    pickLabel = pickLabel
                )
                if (failure == null) {
                    onAccepted?.invoke()
                } else {
                    _toast.value = failure
                }
            } catch (e: Exception) {
                _toast.value = (e as? ApiException)?.message ?: "发送失败，请重试"
            } finally {
                // 点选未占用发送互斥，也不释放（防止连点中提前解锁手打的 in-flight）
                if (!pick) {
                    _sending.value = false
                }
                refreshUnread()
            }
        }
    }

    /**
     * 选项点选（2026-09-18 富交互；2026-09-19 起可重复点选 + 多值已选）：以选项 reply 作为指令文本回复
     * （与手打完全等价），发送成功才把 key 追加进该块已选集合（多值去重）——行内逐项标「已选」，
     * 同一块可继续点其它/同一选项（如逐首保存多首歌）。
     * label 随发送透传：回显行渲染为居中系统提示而非用户气泡。
     * taskId：载荷携带的来源任务 Id，透传 command 帧做合并会话内的点选精确路由。
     */
    fun pickOption(seq: Long, key: String, reply: String, label: String, taskId: String? = null) {
        launchSend(reply.trim(), targetTask = taskId, pickLabel = label) {
            chatRepository.addPickedKey(seq, key)
        }
    }

    /**
     * 删除会话（详情页右上角，2026-09-21 日志删除逻辑调整）：只删会话数据（服务端会话行）与
     * 本地窗口/列表显示——服务器消息记录（业务真实产生的数据）保留，删除入口收敛到日志中心；
     * 任务再次推送时会话自动重建（带全部保留历史）。
     */
    fun deleteSession() {
        viewModelScope.launch {
            runCatching { chatRepository.deleteSession(sessionId) }
                .onSuccess {
                    prefsStore.removeSessionReadSeq(sessionId)
                    _toast.value = "会话已删除（消息记录保留，任务再推送时自动重建）"
                    refreshUnread()
                }
                .onFailure { e ->
                    _toast.value = (e as? ApiException)?.message ?: "删除失败，请重试"
                }
        }
    }

    /**
     * 已读回执：滚动到底/离开时调用，3s 节流合批；同步推进本地会话已读水位（列表角标）。
     * 2026-09-21 双端同步起改调按会话端点 sessions/read（服务端权威水位 + session_read 广播，
     * 替代旧全局 messages/read 的跨会话误标）；失败不热重试语义不变。
     */
    fun reportReadUpTo(seq: Long) {
        // 只更新「已看到的最大 seq」，不立即发；发送统一由节流 job 承担（§4-5）
        if (seq > pendingReadSeq) {
            pendingReadSeq = seq
        }
        if (readReportJob?.isActive == true) {
            return
        }
        readReportJob = viewModelScope.launch {
            delay(3_000)
            // 发送窗口内若用户继续滚动（pendingReadSeq 前进），立即补报，避免滞后
            while (pendingReadSeq > reportedReadSeq) {
                val toSend = pendingReadSeq
                // 本地水位与上报并入同一节流 job（旧实现每次调用都单独 launch 写 DataStore）
                prefsStore.setSessionReadSeq(sessionId, toSend)
                val reported = runCatching { chatRepository.markSessionRead(sessionId, toSend) }
                if (reported.isFailure) {
                    break // 失败不热重试；下次滚动/离开会再次触发
                }
                reportedReadSeq = toSend
            }
        }
    }

    fun consumeToast() {
        _toast.value = null
    }

    fun loadMore() {
        pageSize.value = (pageSize.value * 2).coerceAtMost(500)
    }

    /** image/file/video 消息渲染 URL（绝对外链直通，FileId 走鉴权下载）。 */
    fun mediaUrl(content: String): String = chatRepository.mediaUrl(content)

    /** 是否自家服务端地址（落盘文件无需「保存」）。 */
    fun isOwnServerUrl(url: String): Boolean = chatRepository.isOwnServerUrl(url)

    /**
     * 保存外链媒体到服务器（试听/看完后决定收藏）：发「保存媒体 <url>」指令，
     * 由 media_saver 任务经 ctx.File 落盘并回推 AppMedia 可播气泡（无时效）。
     */
    fun saveMedia(url: String) {
        send("保存媒体 $url")
    }

    /** 先上传（AppUpload）再以 image/file 消息发送（Content=FileId），caption 为可选配文。 */
    fun uploadAndSend(
        context: android.content.Context,
        kind: com.quantum.app.feature.chat.upload.Uploads.UploadKind,
        uri: android.net.Uri,
        caption: String? = null
    ) {
        viewModelScope.launch {
            _toast.value = "上传中…"
            try {
                val fileId = chatRepository.upload(kind, context, uri)
                _toast.value = null
                val contentType = if (kind == com.quantum.app.feature.chat.upload.Uploads.UploadKind.IMAGE) {
                    ChatRepository.CONTENT_TYPE_IMAGE
                } else {
                    ChatRepository.CONTENT_TYPE_FILE
                }
                val failure = chatRepository.sendCommand(fileId, contentType, caption, sessionId)
                failure?.let { _toast.value = it }
            } catch (e: Exception) {
                _toast.value = (e as? ApiException)?.message ?: "上传失败"
            } finally {
                refreshUnread()
            }
        }
    }
}

/**
 * 通知元数据按需关联流（2.3）：输入当前会话消息流，只对其中**通知类型**消息的 msgId
 * 发一条 `IN (...)` 查询（空集返回空 Flow 不发查询），输出 msgId → 元数据映射。
 * 只取通知类型是安全的：notifyMeta 唯一消费方是通知卡片（isNotify 分支），普通消息的
 * msgId 在通知表中本就不存在（msgId 是通知镜像行的全局幂等键）；消息集变化（翻页/新消息）
 * 经 flatMapLatest 换新查询，notification 表落库由 Room 盯表自动重查。
 */
@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
internal fun notificationMetaFlow(
    messages: Flow<List<ChatMessageEntity>>,
    queryByMsgIds: (List<String>) -> Flow<List<NotificationEntity>>
): Flow<Map<String, NotificationEntity>> =
    messages.flatMapLatest { list ->
        val ids = list.asSequence()
            .filter { it.contentType == ChatRepository.NOTIFY_CONTENT_TYPE }
            .map { it.msgId }
            .distinct()
            .toList()
        if (ids.isEmpty()) {
            flowOf(emptyList<NotificationEntity>())
        } else {
            queryByMsgIds(ids)
        }
    }.map { rows: List<NotificationEntity> -> rows.associateBy { it.msgId } }
