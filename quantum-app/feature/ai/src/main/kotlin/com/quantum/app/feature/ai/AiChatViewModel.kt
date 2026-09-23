package com.quantum.app.feature.ai

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.api.unwrapOrNull
import com.quantum.app.core.network.dto.AiChatRequest
import com.quantum.app.core.network.dto.AiConversationDto
import com.quantum.app.core.network.dto.AiConversationSaveRequest
import com.quantum.app.core.network.dto.AiDefaultModelDto
import com.quantum.app.core.network.dto.AiMessageDto
import com.quantum.app.core.network.dto.AiProposalPayload
import com.quantum.app.core.network.dto.AiRunDto
import com.quantum.app.core.network.dto.AiStepDto
import com.quantum.app.core.network.dto.AiTestResultPayload
import com.quantum.app.core.network.dto.ScriptSaveResultDto
import com.quantum.app.core.network.dto.flattenCsFiles
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * AI 对话页（feature:ai，2026-09-21）：轮询式（与 Web 同参数：1200ms 轮询、连续 5 次失败停、
 * 试运行观察 2s×30）；页面离开 = viewModelScope 取消 = 轮询停，不做进程级后台轮询
 * （AI 运行结束靠站内通知 quantum://ai 深链兜底提醒，已有链路）。
 */
@HiltViewModel
class AiChatViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    companion object {
        const val POLL_INTERVAL_MS = 1200L
        const val POLL_FAIL_LIMIT = 5
        const val TEST_WATCH_INTERVAL = 2000L
        const val TEST_WATCH_LIMIT = 30
    }

    /** 消息 UI 模型：Payload 已二次解析（解析失败则载荷为 null，按普通文本兜底渲染）。 */
    data class MessageUi(
        val dto: AiMessageDto,
        val proposal: AiProposalPayload?,
        val testResult: AiTestResultPayload?
    )

    /** 执行过程缓存条目（按 runId）：steps=已拉轨迹、loaded=全量已拉、expanded=展开态。 */
    data class RunStepsUi(
        val steps: List<AiStepDto> = emptyList(),
        val loaded: Boolean = false,
        val expanded: Boolean = false
    )

    data class UiState(
        val loading: Boolean = true,
        val conversation: AiConversationDto? = null,
        val messages: List<MessageUi> = emptyList(),
        val run: AiRunDto? = null,
        val defaultModel: AiDefaultModelDto? = null,
        val sending: Boolean = false,
        /** 手动试运行进行中的提案（按钮 loading；后端 POST 同步跑完，常态返回即终态）。 */
        val testingProposalId: String? = null,
        val scriptOptions: List<String> = emptyList(),
        /** 执行过程缓存（内联过程块）：运行中实时增量拉，历史块首展懒加载，run 结束转折叠态钉在结论消息上方。 */
        val stepsByRun: Map<String, RunStepsUi> = emptyMap(),
        /** 平台能力弹窗文本（null=关）。 */
        val contractText: String? = null,
        /** 应用失败三类诊断弹窗（null=关；已应用/已忽略/基线变动走信封 Code=500 → toast）。 */
        val applyDiagnostics: ScriptSaveResultDto? = null,
        val toast: String? = null
    ) {
        val runActive: Boolean get() = run?.status == AiPayloadParser.RUN_RUNNING
        val maxSeq: Long get() = messages.maxOfOrNull { it.dto.seq } ?: 0L
        /** 运行中的实时过程块（结论消息落库后由同 runId 的消息级块接管，末尾不再重复挂）。 */
        val liveSteps: RunStepsUi?
            get() = if (!runActive || run?.id == null) null else stepsByRun[run!!.id]
        val liveBlockTaken: Boolean
            get() = run?.id != null && messages.any { it.dto.runId == run!!.id && it.dto.role == AiPayloadParser.ROLE_ASSISTANT && stepsByRun.containsKey(it.dto.runId) }
    }

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state.asStateFlow()

    private var conversationId: String? = null
    private var started = false
    /** 服务端已见的最大 Seq（乐观消息 seq=0 不计入，避免增量拉跳过服务端回执）。 */
    private var lastServerSeq = 0L
    /** 发送回执的用户消息 Id：到达后顶掉乐观消息。 */
    private var pendingUserMessageId: String? = null
    private var pollJob: Job? = null

    fun start(conversationId: String) {
        if (started && this.conversationId == conversationId) return
        this.conversationId = conversationId
        started = true
        viewModelScope.launch {
            val (convs, msgs, model, tree) = coroutineScope {
                val convs = async { runCatching { adminApi.aiConversations().unwrap() }.getOrDefault(emptyList()) }
                val msgs = async { runCatching { adminApi.aiMessages(conversationId, 0, 200).unwrap() }.getOrDefault(emptyList()) }
                val model = async { runCatching { adminApi.aiDefaultModel().unwrap() }.getOrNull() }
                val tree = async { runCatching { adminApi.scriptTree().unwrap() }.getOrNull() }
                ResultData(convs.await(), msgs.await(), model.await(), tree.await())
            }
            _state.update {
                it.copy(
                    loading = false,
                    conversation = convs.firstOrNull { c -> c.id == conversationId },
                    messages = msgs.map(::toUi),
                    defaultModel = model,
                    scriptOptions = tree?.flattenCsFiles() ?: emptyList(),
                    // 历史消息里带 runId 的 assistant 消息补建过程块缓存（默认折叠、首展懒加载）
                    stepsByRun = buildHistoryStepEntries(msgs, it.stepsByRun)
                )
            }
            resumeActiveRun()
        }
    }

    private data class ResultData(
        val convs: List<AiConversationDto>,
        val msgs: List<AiMessageDto>,
        val model: AiDefaultModelDto?,
        val tree: List<com.quantum.app.core.network.dto.ScriptTreeDto>?
    )

    /**
     * 进入页面时恢复运行态（中途退出再进、或从别的端发起的运行）。
     * 权威来源是 runs/latest 端点（用户消息不带 runId、运行中又没有 assistant 消息落库，扫消息只能扫到旧 run）：
     * Running → 恢复轮询；非 Running → 仅回填 run 供状态展示；null（会话无 run）→ 清运行态。
     * unwrapOrNull 消费 Data=null；外层 runCatching 兼容「新 APK + 旧后端」（旧路由把 latest 当 runId 返回
     * Code=500），静默回退不弹错，表现为恢复逻辑不生效（服务端先行部署后自然恢复）。
     */
    private suspend fun resumeActiveRun() {
        val convId = conversationId ?: return
        val run = runCatching { adminApi.aiLatestRun(convId).unwrapOrNull() }.getOrNull() ?: run {
            _state.update { it.copy(run = null) }
            return
        }
        _state.update { it.copy(run = run) }
        val rid = run.id
        if (run.status == AiPayloadParser.RUN_RUNNING && !rid.isNullOrBlank()) {
            ensureLiveSteps(rid)
            startPolling(rid)
        }
    }

    // ---- 发送 ----

    fun send(content: String, targetFile: String?) {
        val convId = conversationId ?: return
        val trimmed = content.trim()
        if (trimmed.isEmpty()) return
        if (_state.value.sending) return
        if (_state.value.runActive) {
            _state.update { it.copy(toast = "AI 正在处理上一条消息，请稍候或点停止") }
            return
        }
        viewModelScope.launch {
            _state.update { it.copy(sending = true, toast = null) }
            // 乐观追加用户消息（id=null、seq=0；服务端回执到达后按 MessageId 顶掉）
            val optimistic = AiMessageDto(role = AiPayloadParser.ROLE_USER, kind = AiPayloadParser.KIND_TEXT, content = trimmed)
            _state.update { it.copy(messages = it.messages + MessageUi(optimistic, null, null)) }
            try {
                val started = adminApi.aiChat(
                    AiChatRequest(conversationId = convId, content = trimmed, targetFile = targetFile?.takeIf { it.isNotBlank() })
                ).unwrap()
                pendingUserMessageId = started.messageId
                started.runId?.let { rid ->
                    _state.update {
                        it.copy(
                            run = AiRunDto(id = rid, status = AiPayloadParser.RUN_RUNNING, rounds = 0, progress = "已提交"),
                            stepsByRun = it.stepsByRun + (rid to RunStepsUi(loaded = true, expanded = true))
                        )
                    }
                    startPolling(rid)
                }
            } catch (e: Exception) {
                // 发送失败回滚乐观消息，不留「假发出」
                _state.update { it.copy(messages = it.messages.filterNot { m -> m.dto.id == null }, toast = errorText(e)) }
            } finally {
                _state.update { it.copy(sending = false) }
            }
        }
    }

    // ---- 轮询（run 运行期间：并行拉 run 状态 + 消息增量 + 过程增量） ----

    private fun startPolling(runId: String) {
        if (pollJob?.isActive == true) return
        pollJob = viewModelScope.launch {
            var fails = 0
            while (isActive) {
                delay(POLL_INTERVAL_MS)
                try {
                    val run = coroutineScope {
                        val runD = async { adminApi.aiRun(runId).unwrap() }
                        val msgD = async { pullIncremental() }
                        // 过程块常驻会话流：运行期间始终增量拉（不再「展开才拉」）；失败不影响轮询计数
                        val stepsD = async { runCatching { pullStepsIncrement(runId) } }
                        runD.await().also { msgD.await(); stepsD.await() }
                    }
                    fails = 0
                    _state.update { it.copy(run = run) }
                    // WaitingConfirm=已出提案等用户确认（apply 不会恢复 run）；Succeeded/Failed/Cancelled 同停
                    if (run.status != AiPayloadParser.RUN_RUNNING) break
                } catch (e: kotlinx.coroutines.CancellationException) {
                    throw e
                } catch (e: Exception) {
                    fails++
                    if (fails >= POLL_FAIL_LIMIT) {
                        _state.update { it.copy(toast = "刷新运行状态连续失败，已停止自动刷新", run = null) }
                        break
                    }
                }
            }
            // 收尾：再拉一次增量保证终态消息落齐，过程块转折叠态钉在结论消息上方
            runCatching { pullIncremental() }
            runCatching { pullStepsIncrement(runId) }
            _state.update { st ->
                val entry = st.stepsByRun[runId] ?: RunStepsUi(loaded = true)
                st.copy(stepsByRun = st.stepsByRun + (runId to entry.copy(expanded = false)))
            }
        }
    }

    /** 增量拉（失败抛出，供轮询计数）。 */
    private suspend fun pullIncremental() {
        val convId = conversationId ?: return
        val inc = adminApi.aiMessages(convId, lastServerSeq, 200).unwrap()
        applyMessages(inc, replace = false)
    }

    /** 全量重拉（提案操作后：Payload 是服务端回写后就地更新的，必须 replace 才能刷新卡片状态）。 */
    private suspend fun reloadMessages() {
        val convId = conversationId ?: return
        val all = runCatching { adminApi.aiMessages(convId, 0, 200).unwrap() }.getOrNull() ?: return
        applyMessages(all, replace = true)
    }

    private fun applyMessages(list: List<AiMessageDto>, replace: Boolean) {
        if (list.isNotEmpty()) {
            lastServerSeq = maxOf(lastServerSeq, list.maxOf { it.seq })
        }
        _state.update { st ->
            var msgs = if (replace) emptyList() else st.messages
            // 服务端回执到达 → 顶掉乐观用户消息
            if (pendingUserMessageId != null && list.any { it.id == pendingUserMessageId }) {
                msgs = msgs.filterNot { it.dto.id == null && it.dto.role == AiPayloadParser.ROLE_USER }
            }
            val merged = if (replace) {
                list.map(::toUi)
            } else {
                // 增量：Seq 严格递增，只追加本地没有的（乐观消息 seq=0 不会误判）
                val existing = msgs.map { it.dto.seq }.toSet()
                msgs + list.filter { it.seq !in existing }.map(::toUi)
            }
            st.copy(
                messages = merged,
                // 带新 runId 的结论消息到达 → 补建历史过程块缓存（消息级块接管渲染）
                stepsByRun = buildHistoryStepEntries(merged.map { it.dto }, st.stepsByRun)
            )
        }
    }

    /** 为「带 runId 的 assistant 消息」补建过程块缓存（不覆盖已有条目——运行中实时轨道的状态由轮询维护）。 */
    private fun buildHistoryStepEntries(dtos: List<AiMessageDto>, current: Map<String, RunStepsUi>): Map<String, RunStepsUi> {
        var map = current
        for (dto in dtos) {
            val rid = dto.runId ?: continue
            if (dto.role != AiPayloadParser.ROLE_ASSISTANT) continue
            if (!map.containsKey(rid)) {
                map = map + (rid to RunStepsUi())
            }
        }
        return map
    }

    private fun toUi(dto: AiMessageDto) = MessageUi(
        dto = dto,
        proposal = AiPayloadParser.proposalOf(dto),
        testResult = if (dto.kind == AiPayloadParser.KIND_TEST_RESULT) AiPayloadParser.parseTestResult(dto.payload) else null
    )

    // ---- run 操作 ----

    fun cancelRun() {
        val runId = _state.value.run?.id ?: return
        viewModelScope.launch {
            // cancel 已结束的 run 返回 Data=false 是幂等正常态；轮询会拾取终态后自行收尾
            runCatching { adminApi.aiCancelRun(runId).unwrap() }
                .onFailure { e -> _state.update { it.copy(toast = errorText(e)) } }
        }
    }

    // ---- 提案闭环 ----

    fun testRun(proposalId: String) {
        viewModelScope.launch {
            _state.update { it.copy(testingProposalId = proposalId, toast = null) }
            try {
                adminApi.aiProposalTestRun(proposalId).unwrap()
                val beforeSeq = lastServerSeq
                reloadMessages()
                // 兜底观察（对齐 Web 2s×30：常态后端 POST 同步跑完+回写，首轮即退出）
                var tries = 0
                while (tries < TEST_WATCH_LIMIT && testStillPending(proposalId, beforeSeq)) {
                    delay(TEST_WATCH_INTERVAL)
                    reloadMessages()
                    tries++
                }
            } catch (e: Exception) {
                _state.update { it.copy(toast = errorText(e)) }
            } finally {
                _state.update { it.copy(testingProposalId = null) }
            }
        }
    }

    private fun testStillPending(proposalId: String, beforeSeq: Long): Boolean {
        val st = _state.value
        val card = st.messages.firstNotNullOfOrNull { m ->
            m.proposal?.takeIf { it.id == proposalId }
        }
        val statusPending = card == null || card.testStatus == "Running" || card.testStatus == "None"
        return statusPending && st.maxSeq <= beforeSeq
    }

    fun applyProposal(proposalId: String) {
        viewModelScope.launch {
            try {
                val result = adminApi.aiProposalApply(proposalId).unwrap()
                if (result.success) {
                    _state.update { it.copy(toast = "提案已应用，脚本已写入并记入版本历史（可回滚）") }
                    reloadMessages()
                } else {
                    // 门禁/编译不过：Success=false + 三类诊断 → 诊断弹窗
                    _state.update { it.copy(applyDiagnostics = result) }
                }
            } catch (e: Exception) {
                // 已应用/已忽略/基线变动（Stale）走信封 Code=500 → 透传服务端文案
                _state.update { it.copy(toast = errorText(e)) }
            }
        }
    }

    fun discardProposal(proposalId: String) {
        viewModelScope.launch {
            try {
                adminApi.aiProposalDiscard(proposalId).unwrap()
                _state.update { it.copy(toast = "提案已忽略") }
                reloadMessages()
            } catch (e: Exception) {
                _state.update { it.copy(toast = errorText(e)) }
            }
        }
    }

    // ---- 头部：设置 / 平台能力 / 过程面板 ----

    /** AllowEnvValues 开关：POST upsert 必须回传原 Title（服务端空标题会重置为「未命名会话」）。 */
    fun setAllowEnvValues(checked: Boolean) {
        val conv = _state.value.conversation ?: return
        viewModelScope.launch {
            runCatching {
                adminApi.aiSaveConversation(
                    AiConversationSaveRequest(id = conv.id, title = conv.title ?: "", allowEnvValues = checked)
                ).unwrap()
            }.onSuccess { saved ->
                _state.update { it.copy(conversation = saved) }
            }.onFailure { e ->
                _state.update { it.copy(toast = errorText(e)) }
            }
        }
    }

    fun openContract() {
        if (_state.value.contractText != null) return
        viewModelScope.launch {
            runCatching { adminApi.aiContract().unwrap() }
                .onSuccess { text -> _state.update { it.copy(contractText = text) } }
                .onFailure { e -> _state.update { it.copy(toast = errorText(e)) } }
        }
    }

    fun closeContract() = _state.update { it.copy(contractText = null) }

    fun closeDiagnostics() = _state.update { it.copy(applyDiagnostics = null) }

    // ---- 执行过程（内联过程块） ----

    /** 运行中的实时轨道条目（展开态，轮询增量填充）。 */
    private fun ensureLiveSteps(runId: String) {
        _state.update { st ->
            if (st.stepsByRun[runId]?.loaded == true) st
            else st.copy(stepsByRun = st.stepsByRun + (runId to RunStepsUi(loaded = true, expanded = true)))
        }
    }

    /** 过程增量拉取（afterSeq=该 run 已拉到的最大步序；失败静默，由轮询节奏重试）。 */
    private suspend fun pullStepsIncrement(runId: String) {
        val after = _state.value.stepsByRun[runId]?.steps?.lastOrNull()?.seq ?: 0
        val inc = adminApi.aiRunSteps(runId, after).unwrap()
        if (inc.isEmpty()) return
        _state.update { st ->
            val entry = st.stepsByRun[runId] ?: RunStepsUi(loaded = true, expanded = true)
            val merged = (entry.steps + inc).distinctBy { it.seq }.sortedBy { it.seq }
            st.copy(stepsByRun = st.stepsByRun + (runId to entry.copy(steps = merged)))
        }
    }

    /** 展开/折叠过程块；历史块首次展开时懒加载全量轨迹（同一 run 不重复拉）。 */
    fun toggleRunSteps(runId: String) {
        val entry = _state.value.stepsByRun[runId] ?: RunStepsUi()
        val expanding = !entry.expanded
        _state.update { it.copy(stepsByRun = it.stepsByRun + (runId to entry.copy(expanded = expanding))) }
        if (!expanding || entry.loaded) return
        viewModelScope.launch {
            runCatching { adminApi.aiRunSteps(runId, 0).unwrap() }
                .onSuccess { steps ->
                    _state.update { st ->
                        val cur = st.stepsByRun[runId] ?: return@update st
                        st.copy(stepsByRun = st.stepsByRun + (runId to cur.copy(steps = steps.sortedBy { it.seq }, loaded = true)))
                    }
                }
                .onFailure {
                    // 拉取失败收起，等下次展开重试
                    _state.update { st ->
                        val cur = st.stepsByRun[runId] ?: return@update st
                        st.copy(stepsByRun = st.stepsByRun + (runId to cur.copy(expanded = false)))
                    }
                }
        }
    }

    fun consumeToast() = _state.update { it.copy(toast = null) }

    private fun errorText(e: Throwable): String =
        (e as? ApiException)?.message ?: e.message ?: "请求失败"
}
