package com.quantum.app.feature.chat

import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.ws.WsFrame
import com.quantum.app.core.storage.db.ChatMessageDao
import com.quantum.app.core.storage.db.ChatMessageEntity
import com.quantum.app.core.storage.db.ChatSessionDao
import com.quantum.app.core.storage.db.OutboxDao
import com.quantum.app.core.storage.db.OutboxEntity
import com.quantum.app.core.storage.db.SessionLastMessageRow
import com.quantum.app.core.storage.db.SyncCursorDao
import com.quantum.app.core.storage.db.SyncCursorEntity
import com.quantum.app.core.storage.db.UnreadCountsQuery
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import okhttp3.MediaType.Companion.toMediaType
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 会话仓库 = 同步引擎 + 发送链路（计划 §5.3/§5.4）：
 * - **同步**：cursor=sync_cursor.maxSeq；补拉唯一通道是 REST（500/页翻页直到本页 < limit）——
 *   服务端 `sync`/`sync_done` 帧路径已停用（客户端从不发 sync，服务端保留以兼容旧版本）；
 * - **落库**：WS 直推帧（message/notify）由**常驻**消费方（AppPushHandler）写入，按 msgId 幂等、
 *   按服务端真实 seq 落库；本仓库不再自造 seq（自造会覆写真实消息行）；
 * - **发送**：WS command 帧优先、REST 兜底；自己发的指令服务端只落「接收」行**不推回**，
 *   本地 outbox 过渡态，发送成功后立刻小同步把该行拉回渲染为右侧气泡，outbox 即删；
 * - 失败（限流/网络）→ outbox 标记失败可重发。
 */
@Singleton
class ChatRepository @Inject constructor(
    private val appApi: AppApi,
    private val wsClient: com.quantum.app.core.network.ws.WsGateway,
    private val sessionManager: com.quantum.app.core.network.session.SessionGateway,
    private val chatMessageDao: ChatMessageDao,
    private val chatSessionDao: ChatSessionDao,
    private val syncCursorDao: SyncCursorDao,
    private val outboxDao: OutboxDao,
    private val readStore: com.quantum.app.core.storage.prefs.SessionReadStore
) {
    companion object {
        const val SYNC_PAGE_SIZE = 500
        const val CONTENT_TYPE_IMAGE = "image"
        const val CONTENT_TYPE_FILE = "file"
        const val CONTENT_TYPE_VIDEO = "video"

        /** 服务端通知镜像行的内容类型标记（批二据此渲染通知卡片）。 */
        const val NOTIFY_CONTENT_TYPE = "notify"

        /** 音频消息（M2）：Content 为直链或服务端 AppMedia 相对地址。 */
        const val CONTENT_TYPE_AUDIO = "audio"

        /** movesMap 上界：防异常序列把内存映射撑爆；超界丢最旧（插入序）。 */
        private const val MOVES_MAP_LIMIT = 500

        /** 上传大小上限（对齐服务端 AppUploadService.MaxImageBytes/MaxFileBytes：图片 10MB / 文件 50MB）。 */
        const val MAX_IMAGE_UPLOAD_BYTES = 10L * 1024 * 1024

        /** 同上：普通文件 50MB（服务端 [RequestSizeLimit] 另含 64KB multipart 信封余量）。 */
        const val MAX_FILE_UPLOAD_BYTES = 50L * 1024 * 1024

        /** 发送后 echo 兜底轮询（2.5）：按会话增量拉取的间隔序列（递增）与会话分页页大小。 */
        val ECHO_POLL_INTERVALS_MS = longArrayOf(250L, 500L, 1000L)

        /** 会话内分页单页上限（服务端 SessionPageAsync clamp 1..200，多取 1 免 COUNT）。 */
        const val SESSION_PAGE_SIZE = 200

        /**
         * session_read 帧字段 → 仓库动作参数（纯函数，JVM 单测）：会话键归一（null/空白 → 默认会话空串）
         * + 水位有效性（非正数视为无效帧忽略——服务端只发递增正值，防御旧/异常服务端）。
         */
        fun normalizeSessionRead(session: String?, seq: Long?): Pair<String, Long>? {
            if (seq == null || seq <= 0L) {
                return null
            }
            return session?.trim().orEmpty() to seq
        }

        /**
         * overview 响应 → 待吸收的本地水位条目（纯函数，JVM 单测）：只吸收比本地新的权威水位
         * （离线追赶——对端在离线期间的已读进度）；服务端更低（本地超前，如上报失败的追赶）不回拨本地。
         */
        fun absorbedReadWatermarks(
            local: Map<String, Long>,
            remote: List<Pair<String, Long>>
        ): List<Pair<String, Long>> =
            remote.mapNotNull { (key, seq) ->
                val current = local[key] ?: 0L
                if (seq > current) key to seq else null
            }
    }

    /** 跨会话触发迁移事件（message_moved 帧 → UI 自动切换窗口用）：from 空串 = 默认会话。 */
    data class SessionMoveEvent(val from: String, val to: String)

    /**
     * 跨会话触发迁移事件流：仅当前停留在来源会话的页面收集后导航切换，
     * 其余设备/页面不收集（只搬数据不打断，契约 §4.4 D4）。
     */
    private val _sessionMoves = MutableSharedFlow<SessionMoveEvent>(extraBufferCapacity = 16)
    val sessionMoves: SharedFlow<SessionMoveEvent> = _sessionMoves.asSharedFlow()

    /**
     * 乱序防御（msgId → 目标会话键）：迁移帧先于 echo/REST 补拉到达时行不在本地，先记映射；
     * [onWsMessage] 落行前用其覆盖帧携带的会话键（echo 的 session 仍是来源，直接落库会把
     * 已迁移行 REPLACE 回旧会话），命中即消费。仅内存态，重进/清缓存自愈（服务端行已是新键）。
     */
    private val movesMap = LinkedHashMap<String, String>()

    private fun rememberMove(msgId: String, to: String) {
        movesMap[msgId] = to
        if (movesMap.size > MOVES_MAP_LIMIT) {
            val iterator = movesMap.keys.iterator()
            iterator.next()
            iterator.remove()
        }
    }

    fun messages(limit: Int = 50, offset: Int = 0): Flow<List<ChatMessageEntity>> =
        chatMessageDao.page(limit, offset)

    /** 指定会话的分页消息（会话详情页数据源；空串 = 默认会话）。 */
    fun sessionMessages(sessionId: String, limit: Int = 50, offset: Int = 0): Flow<List<ChatMessageEntity>> =
        chatMessageDao.sessionPage(sessionId, limit, offset)

    /**
     * 会话列表（会话表为主 LEFT JOIN 每会话最后一条消息，Room 落库即刷新）：
     * 空会话（清空后保留）返回行、last* 为 null——会话不再随消息行删除而消失（2026-09-18 会话生命周期）。
     */
    fun sessionsWithLast(): Flow<List<SessionLastMessageRow>> = chatSessionDao.sessionsWithLast()

    /** 会话内未读数（相对本地已读水位）。 */
    suspend fun sessionUnread(sessionId: String, afterSeq: Long): Int =
        chatMessageDao.sessionUnread(sessionId, afterSeq)

    /**
     * 未读聚合一（2.2）：一条 SQL 得到全部会话的未读数（sessionId → unread，miss = 0），
     * 与逐会话 [sessionUnread] 完全同口径（水位合并缺省 0），替代列表/红点刷新的 N+1 COUNT。
     * Room 盯 chat_message 失效：消息落库/删除即重查；水位变化由上层 flatMapLatest 换新查询。
     */
    fun unreadCounts(sessionKeys: List<String>, readSeqs: Map<String, Long>): Flow<Map<String, Int>> =
        chatMessageDao.unreadCounts(UnreadCountsQuery.waterlines(sessionKeys, readSeqs))

    /** 会话内最大 seq（进入会话时推进已读水位）。 */
    suspend fun sessionMaxSeq(sessionId: String): Long? = chatMessageDao.sessionMaxSeq(sessionId)

    /** 全局最大 seq（补拉前基准：区分「本次同步新增」的行）。 */
    suspend fun latestSeq(): Long = chatMessageDao.latest()?.seq ?: 0L

    /** afterSeq 之后新入库的机器人消息按会话归并，各取最新一条（后台补拉通知用）。 */
    suspend fun newestBotMessagePerSession(afterSeq: Long): Map<String, ChatMessageEntity> =
        chatMessageDao.botMessagesAfter(afterSeq)
            .groupBy { it.sessionId }
            .mapValues { (_, rows) -> rows.first() }

    /** 未读数（服务端事实源：Direction=1 未读）。 */
    suspend fun unreadCount(): Long = appApi.unreadCount().unwrap()

    /**
     * §4-4：全量同步串行锁。登录/重连/切网可能几乎同时各发起一次 syncAll，
     * 二者交错读「cursor→翻页→写回」会重复入库并竞写共享游标。Mutex 保证同一实例
     * 上的全量同步串行：后到者拿到锁时游标已被前一个追平，首页即空 → 立即返回，
     * 不会重复整轮补拉（msgId 幂等再兜一道）。
     * 未采纳「共享 in-flight Deferred」去重：那需把 Deferred 绑到首个调用者的协程作用域，
     * 该作用域一旦取消会把结果连带传给所有等待方（误伤其余同步），Singleton 常驻作用域又无从
     * 随生命周期回收；Mutex 已消除竞态，收益仅省一个空页请求，不值当引入这套生命周期风险。
     */
    private val syncMutex = Mutex()

    /**
     * 点选代发匹配队列（内存，非持久化）：[sendCommand] 带 pickLabel 且提交成功后入队，
     * [persistMessages] 里按 content 匹配回显「接收」行。进程重启即清，未匹配的行回落普通气泡。
     */
    private val pendingPicks = ArrayDeque<PendingPick>()

    /** 点选代发待匹配项：content = 发出的 reply 文本，label = 选项展示文案。 */
    private data class PendingPick(val content: String, val label: String)

    /**
     * 全量同步入口（登录/重连/切网统一调用）。返回本次同步到的最大 seq。
     * 分页终止条件用「本页条数 < limit」而非 maxSeq 比对（maxSeq 是游标不是总数信号）；
     * **空页不推进游标**——服务端契约已保证空页回原游标，客户端再守一道：若对端是旧版本
     * （空页回落服务端 MAX），写进游标会越过"并发落库但本页未返回"的消息，那条消息此后永远拉不回。
     */
    suspend fun syncAll(): Long {
        val cursor = syncMutex.withLock {
            var current = syncCursorDao.get()?.maxSeq ?: 0L
            while (true) {
                val page = appApi.messages(afterSeq = current, limit = SYNC_PAGE_SIZE).unwrap()
                val messages = page.data
                if (messages.isEmpty()) {
                    break
                }
                persistMessages(messages.map { it.toEntity() })
                current = page.maxSeq
                syncCursorDao.set(SyncCursorEntity(maxSeq = current))
                if (messages.size < SYNC_PAGE_SIZE) {
                    break
                }
            }
            current
        }
        // 双端同步（2026-09-21）：补拉完成后与服务器交换已读水位（本地上报 + 权威吸收），
        // 放锁外——水位交换与消息游标互不依赖，且内部 runCatching 静默不污染同步语义
        syncReadWatermarks()
        return cursor
    }

    /**
     * 发送指令/消息：先写 outbox（发送中气泡），WS command 帧优先、REST 兜底；
     * 成功后立即小同步拉回服务端「接收」行并删除 outbox。
     * image/file 消息：content=FileId、contentType=image/file（服务端封装落库）。
     * targetTask：点选代发的来源任务 Id（合并会话内点选的精确路由依据），手打/缺省 null；WS 与 REST 两路均透传。
     * pickLabel：点选代发的选项 label，非空时回显「接收」行落库前经 [pendingPicks] 匹配打上标记，
     * 渲染为居中系统提示「已选择「label」」而非用户气泡（本端单端记忆，见 docs/选项已选态改造计划.md §3）。
     * 返回 null=成功；非 null=失败文案（限流等）。
     */
    suspend fun sendCommand(
        content: String,
        contentType: String = "text",
        contentText: String? = null,
        sessionId: String = "",
        targetTask: String? = null,
        pickLabel: String? = null
    ): String? {
        val outboxId = outboxDao.add(OutboxEntity(content = content))
        // WS 指令是异步投递：记录发送前的最大 seq，用于判断服务端是否已落「接收」行
        val seqBeforeSend = chatMessageDao.latest()?.seq ?: 0L
        val viaWs = if (contentType == "text") {
            wsClient.sendCommand(content, sessionId, targetTask)
        } else {
            false
        }
        val accepted = viaWs || runCatching {
            appApi.command(
                com.quantum.app.core.network.dto.CommandRequest(
                    content = content,
                    contentType = contentType,
                    contentText = contentText?.trim()?.takeIf { it.isNotEmpty() },
                    session = sessionId.ifEmpty { null },
                    targetTaskId = targetTask
                )
            ).unwrap().accepted
        }.getOrDefault(false)

        if (!accepted) {
            outboxDao.markFailed(outboxId)
            return "发送失败，请检查网络后重试"
        }
        // 提交成功才入匹配队列（须早于下方 echo 轮询：回显行就在轮询/WS 推送里到达）
        if (pickLabel != null) {
            pendingPicks.addLast(PendingPick(content, pickLabel))
        }
        if (viaWs) {
            // WS 发送不同于 REST：REST 响应返回时接收行必已落库，而 WS 帧投递后服务端才
            // 异步落「接收」行——立即拉取会扑空，自己的气泡就此丢失（返回重进才出现）。
            // 2.5 降级：机器人回复等在线推送本来就走 WS message 帧由常驻消费方落库
            // （会话 Flow 自然刷新），这里只为找回「接收」行兜底——按会话增量轮询
            // （只拉该会话分页第 1 页，本地过滤 seq > seqBeforeSend），间隔 250/500/1000ms
            // 递增、上限 3 次，出现接收行即停；不再做 500/页的全局 syncAll。
            // 超时留给重连/回前台兜底补拉。
            for (delayMs in ECHO_POLL_INTERVALS_MS) {
                kotlinx.coroutines.delay(delayMs)
                if (pullSessionEchoOnce(sessionId, seqBeforeSend)) {
                    break
                }
            }
        } else {
            // 小同步：把服务端落库的「接收」行拉回渲染（多设备天然一致）。
            // REST 响应返回时接收行必已落库，单次按会话增量拉取即可（2.5，不动全局游标）。
            pullSessionEchoOnce(sessionId, seqBeforeSend)
        }
        outboxDao.remove(outboxId)
        return null
    }

    /**
     * 按会话增量拉取一次（2.5）：GET api/App/session/messages 第 1 页（服务端 Seq 倒序取页、
     * 升序返回、limit ≤ 200），本地只落 seq > seqBeforeSend 的新行；返回是否已见到
     * 「接收」行（自己那条回显，direction=2）。
     * **只 upsert 消息行与会话行，不读不写 sync_cursor**——全局游标推进仍由 [syncAll]
     * 独占（此处拿到的行若超过全局游标，下轮全量同步按 msgId 幂等重拉，不回拨不乱推进）。
     */
    private suspend fun pullSessionEchoOnce(sessionId: String, seqBeforeSend: Long): Boolean {
        val page = runCatching {
            appApi.sessionMessages(
                session = sessionId.ifEmpty { null },
                beforeSeq = 0,
                limit = SESSION_PAGE_SIZE
            ).unwrap()
        }.getOrNull() ?: return false
        val fresh = page.messages.filter { it.seq > seqBeforeSend }
        if (fresh.isEmpty()) {
            return false
        }
        persistMessages(fresh.map { it.toEntity() })
        return fresh.any { it.direction == ChatMessageEntity.DIRECTION_FROM_USER }
    }

    /**
     * WS 直推 message 帧：入库（方向按帧携带，缺省机器人下发）并 ack。
     * 帧缺 seq 时不自造——自造的 seq 会与真实行撞主键并整行覆写（旧缺陷），改为触发一次 REST 补拉。
     */
    suspend fun onWsMessage(frame: com.quantum.app.core.network.ws.WsFrame) {
        val msgId = frame.msgId ?: return
        if (chatMessageDao.byMsgId(msgId) != null) {
            // 幂等：已入库仅补 ack
            wsClient.sendAck(msgId)
            return
        }
        val seq = frame.seq
        if (seq == null) {
            // 帧缺 seq：REST 补拉取回真实行（绝不自造 seq），并补 ack——
            // 早先不 ack 会让服务端按未确认一直重投同一条（§4-4）
            syncAll()
            wsClient.sendAck(msgId)
            return
        }
        persistMessages(
            listOf(
                ChatMessageEntity(
                    seq = seq,
                    msgId = msgId,
                    direction = frame.direction ?: ChatMessageEntity.DIRECTION_FROM_BOT,
                    content = frame.content.orEmpty(),
                    contentType = frame.contentType ?: "text",
                    contentText = frame.contentText,
                    payload = frame.payload,
                    status = ChatMessageEntity.STATUS_DELIVERED,
                    createTime = frame.createTime.orEmpty(),
                    // movesMap 命中优先：迁移帧先到时 echo 的 session 还是来源会话，直接落库会把行写回旧会话
                    sessionId = movesMap.remove(msgId) ?: frame.session.orEmpty()
                )
            )
        )
        wsClient.sendAck(msgId)
    }

    /**
     * WS message_moved 帧（2026-09-18 跨会话触发消息迁移）：服务端已把该用户气泡行改键
     * from → to（仅「接收」行、seq 不变）。本地按 msgId 更新行 sessionId 并推进目标会话行
     * （列表是本地 Room 聚合，不建行则目标会话不出现）；行不存在则只记 movesMap
     * （echo/REST 补拉落行时覆盖自愈）。发 [sessionMoves] 事件供当前停留在来源会话的页面
     * 自动切换。不弹本地通知（迁移的是用户自己的已读行）。from 缺省 = 默认会话；from == to 忽略。
     */
    suspend fun onWsMessageMoved(frame: WsFrame) {
        val msgId = frame.msgId ?: return
        val to = frame.to ?: return
        val from = frame.from.orEmpty()
        if (from == to) {
            return
        }
        rememberMove(msgId, to)
        chatMessageDao.moveSession(msgId, to)
        // 搬完读回推进目标会话行（advanceAll：建行/lastSeq 取大）；源会话行保留不回拨（排序容忍）
        chatMessageDao.byMsgId(msgId)?.let { chatSessionDao.advanceAll(listOf(it)) }
        _sessionMoves.tryEmit(SessionMoveEvent(from, to))
    }

    /**
     * WS 直推 notify 帧：站内通知统一进会话流（会话流是通知在 App 内的唯一展示面）。
     * 服务端 notify 帧带镜像行的真实 seq，与随后 REST 补拉到的行同 seq，upsert 命中同一行、不产生重复。
     * 帧缺 seq（旧服务端）时按 REST 补拉取回真实行，绝不自造 seq。
     */
    suspend fun onWsNotify(frame: com.quantum.app.core.network.ws.WsFrame) {
        val msgId = frame.msgId ?: return
        if (chatMessageDao.byMsgId(msgId) != null) {
            return
        }
        val seq = frame.seq
        if (seq == null) {
            syncAll()
            return
        }
        val title = frame.title.orEmpty()
        val content = frame.content.orEmpty()
        val text = if (title.isEmpty()) content else "【$title】\n$content"
        persistMessages(
            listOf(
                ChatMessageEntity(
                    seq = seq,
                    msgId = msgId,
                    direction = ChatMessageEntity.DIRECTION_FROM_BOT,
                    content = text,
                    contentType = frame.contentType ?: NOTIFY_CONTENT_TYPE,
                    status = ChatMessageEntity.STATUS_DELIVERED,
                    createTime = frame.createTime.orEmpty(),
                    sessionId = frame.session.orEmpty()
                )
            )
        )
    }

    /**
     * 消息落库统一入口（同步补拉/WS 收帧等全部入库路径共用）：
     * 消息行 upsert 后同批推进会话行（建行/推进 lastSeq）——会话表是列表主数据源，
     * 消息落库不建会话行会话就不在列表出现。
     * 入库前先经 [matchPendingPick]（点选代发的「接收」回显行打 pickLabel）与
     * [mergeLocalMarks]（保留本地富交互标记列）——同一条回显行常被两条路径先后送达
     * （WS echo 帧先到、发送后的 REST 轮询/全局同步后到），后到实体不带本地列，
     * 直接 upsert 会把 pickLabel/pickedKeys 抹掉（2026-09-19 发布实测踩中）。
     */
    private suspend fun persistMessages(messages: List<ChatMessageEntity>) {
        val resolved = mergeLocalMarks(messages.map { matchPendingPick(it) })
        chatMessageDao.upsertAll(resolved)
        chatSessionDao.advanceAll(resolved)
    }

    /**
     * 点选代发回显匹配（内存队列，进程重启即清）：「接收」行 content 命中即打 pickLabel 并出队；
     * 手打同文本竞态先到先匹配，误标后果仅是显示形态不同（计划 §3）。未匹配的留待后续回流行。
     */
    private fun matchPendingPick(entity: ChatMessageEntity): ChatMessageEntity {
        if (entity.direction != ChatMessageEntity.DIRECTION_FROM_USER || entity.pickLabel != null) {
            return entity
        }
        val hit = pendingPicks.firstOrNull { it.content == entity.content } ?: return entity
        pendingPicks.remove(hit)
        return entity.copy(pickLabel = hit.label)
    }

    /**
     * 保留本地富交互标记列：已入库行的 pickLabel/pickedKeys 只在本机产生（单端语义），
     * 服务端重推的实体（echo 轮询/补拉/全局同步）恒不带这两列，upsert 前按 msgId 回填本地值，
     * 非空本地值优先——重推绝不抹标记；标记的清除只随行删除（清空/删除会话）。
     */
    private suspend fun mergeLocalMarks(messages: List<ChatMessageEntity>): List<ChatMessageEntity> {
        val ids = messages.map { it.msgId }.distinct()
        if (ids.isEmpty()) {
            return messages
        }
        val local = chatMessageDao.byMsgIds(ids).associateBy { it.msgId }
        if (local.isEmpty()) {
            return messages
        }
        return messages.map { incoming ->
            val existing = local[incoming.msgId] ?: return@map incoming
            incoming.copy(
                pickLabel = existing.pickLabel ?: incoming.pickLabel,
                pickedKeys = existing.pickedKeys ?: incoming.pickedKeys
            )
        }
    }

    /** 历史检索（P1）：GET api/App/history，Seq 倒序。 */
    suspend fun searchHistory(keyword: String, pageSize: Int = 50): List<com.quantum.app.core.network.dto.ChatMessageDto> =
        appApi.history(keyword = keyword, pageSize = pageSize).unwrap()

    /**
     * 已读回执：会话页可见且滚动到底时上报（节流由 ViewModel 控）。
     * 旧全局端点 [markReadUpTo] 保留给旧链路回归，新客户端走本按会话端点（不跨会话误标）。
     */
    suspend fun markSessionRead(sessionId: String, seq: Long) {
        appApi.sessionsRead(
            com.quantum.app.core.network.dto.SessionsOverviewRequest(mapOf(sessionId to seq))
        ).unwrap()
    }

    /** 旧全局已读回执（messages/read，按全局 Seq 推进 Status）：仅供回归/降级，新代码勿用。 */
    suspend fun markReadUpTo(seq: Long): Long = appApi.markRead(upToSeq = seq).unwrap()

    /**
     * WS session_read 帧（2026-09-21 双端同步）：对端推进了某会话的服务端权威水位——
     * 键归一后只进推进本地水位（DataStore Flow 令会话列表角标/底栏红点自动重算）。
     * 会话详情页 UI 不消费水位值，与本端在屏阅读无冲突；本端随后的上报只进、不会回退。
     */
    suspend fun onSessionRead(session: String?, seq: Long?) {
        val (key, value) = normalizeSessionRead(session, seq) ?: return
        readStore.advanceReadWatermark(key, value)
    }

    /**
     * 已读水位交换（挂 [syncAll] 尾部，启动/重连/回前台必经）：上报本地全部会话水位
     * （键域 = 会话键，默认会话空串——sessionReadSeqs Flow 已解码归一，勿读 DataStore 原始串），
     * 吸收服务端权威 ReadSeq 只进推进本地（对端离线期间的已读进度追赶）。
     * 整体 runCatching 静默：失败不阻断 syncAll 语义，下次 syncAll 再交换兜底。
     */
    private suspend fun syncReadWatermarks() {
        runCatching {
            val local = readStore.readWatermarks()
            val result = appApi.sessionsOverview(
                com.quantum.app.core.network.dto.SessionsOverviewRequest(local)
            ).unwrap()
            absorbedReadWatermarks(local, result.sessions.map { it.sessionKey to it.readSeq })
                .forEach { (key, seq) -> readStore.advanceReadWatermark(key, seq) }
        }
    }

    /** 点选成功后把 key 追加进该选项块已选集合（多值去重；本地语义，多设备各自维护）。 */
    suspend fun addPickedKey(seq: Long, key: String) = chatMessageDao.addPickedKey(seq, key)

    /**
     * 删除会话（会话列表长按与详情页右上角共用，2026-09-21 日志删除逻辑调整）：服务端只删
     * 会话实体（t_chat_session 行 = 会话数据），**消息行（业务真实产生的记录）保留**——消息删除
     * 入口收敛到日志中心；本地删会话行与消息行（窗口/列表显示一并清除）。响应 MaxSeq 恒为当前
     * 全局最大（消息不删 → Seq 号段不复用），既有游标回拨 min(当前, MaxSeq) 退化为 no-op，防御保留
     * （只回拨不下推，避免跳过尚未同步的其他会话行，重拉由 msgId 幂等去重）。该会话来源任务再推送
     * 消息时会话自动重建（带全部保留历史）。
     */
    suspend fun deleteSession(sessionId: String) {
        val result = appApi.deleteSession(sessionId.ifEmpty { null }).unwrap()
        chatSessionDao.delete(sessionId)
        chatMessageDao.deleteSession(sessionId)
        val cursor = syncCursorDao.get()?.maxSeq ?: 0L
        syncCursorDao.set(SyncCursorEntity(maxSeq = minOf(cursor, result.maxSeq)))
    }

    /** 重发失败的 outbox 指令。 */
    suspend fun resendFailed(): String? {
        val failed = outboxDao.firstPending() ?: return null
        return sendCommand(failed.content)
    }

    /**
     * 上传图片/文件（POST api/AppUpload/image|file，multipart 字段 file）。
     * 返回 FileId（消息封装约定：contentType=image/file、Content=FileId）。
     * 2.4：流式请求体（InputStreamRequestBody 直读 Uri 分块写网络，不整文件进内存）；
     * 发送前大小上限对齐服务端 AppUploadService（MaxImageBytes=10MB / MaxFileBytes=50MB，
     * 控制器 [RequestSizeLimit] 另留 64KB multipart 信封余量），超限直接抛业务异常不上传
     * （上游 uploadAndSend 统一转 toast 提示）；大小查不到（-1）不做预检，交给服务端硬限。
     */
    suspend fun upload(kind: com.quantum.app.feature.chat.upload.Uploads.UploadKind, context: android.content.Context, uri: android.net.Uri): String {
        val displayName = com.quantum.app.feature.chat.upload.Uploads.displayName(context, uri, kind)
        val mime = com.quantum.app.feature.chat.upload.Uploads.mimeType(context, uri)
        val size = com.quantum.app.feature.chat.upload.InputStreamRequestBody.querySize(context.contentResolver, uri)
        if (kind == com.quantum.app.feature.chat.upload.Uploads.UploadKind.IMAGE) {
            if (size > MAX_IMAGE_UPLOAD_BYTES) {
                throw com.quantum.app.core.common.ApiException(500, "图片不能超过 10MB，请压缩后重试")
            }
        } else if (size > MAX_FILE_UPLOAD_BYTES) {
            throw com.quantum.app.core.common.ApiException(500, "文件不能超过 50MB")
        }
        val body = com.quantum.app.feature.chat.upload.InputStreamRequestBody(
            contentResolver = context.contentResolver,
            uri = uri,
            mediaType = mime.toMediaType(),
            contentLength = size
        )
        val part = okhttp3.MultipartBody.Part.createFormData(
            name = "file",
            filename = displayName,
            body = body
        )
        val result = if (kind == com.quantum.app.feature.chat.upload.Uploads.UploadKind.IMAGE) {
            appApi.uploadImage(part).unwrap()
        } else {
            appApi.uploadFile(part).unwrap()
        }
        return result.fileId
    }

    suspend fun clearLocal() {
        chatMessageDao.clear()
        chatSessionDao.clear()
        outboxDao.clear()
        syncCursorDao.set(SyncCursorEntity(maxSeq = 0))
    }

    /**
     * image/file/video 消息渲染 URL：Content 是绝对 http(s) 地址（服务端脚本直发外链，
     * 如随机图片 API）时原样返回，否则视为 FileId 走 GET api/AppUpload/{fileId} 鉴权下载。
     */
    fun mediaUrl(content: String): String {
        val trimmed = content.trim()
        return when {
            trimmed.startsWith("http://") || trimmed.startsWith("https://") -> trimmed
            // 服务端媒体通道相对地址（api/AppMedia/file?path=…）：按当前登录服务器补全——
            // LAN/公网切换自动跟随，无需服务端配置公网基地址
            trimmed.startsWith("api/", ignoreCase = true) -> "${sessionManager.currentBaseUrl}/$trimmed"
            else -> "${sessionManager.currentBaseUrl}/api/AppUpload/$trimmed"
        }
    }

    /** 是否自家服务端地址（AppUpload/AppMedia 落盘文件无需再「保存」）。 */
    fun isOwnServerUrl(url: String): Boolean {
        val base = sessionManager.currentBaseUrl
        return base.isNotBlank() && url.startsWith(base, ignoreCase = true)
    }
}

/** ChatMessageDto → 本地实体（SessionKey 空/缺省归默认会话）。 */
fun com.quantum.app.core.network.dto.ChatMessageDto.toEntity(): ChatMessageEntity =
    ChatMessageEntity(
        seq = seq,
        msgId = msgId,
        direction = direction,
        content = content,
        contentType = contentType,
        contentText = contentText,
        payload = payload,
        status = status,
        createTime = createTime,
        sessionId = sessionKey.orEmpty()
    )
