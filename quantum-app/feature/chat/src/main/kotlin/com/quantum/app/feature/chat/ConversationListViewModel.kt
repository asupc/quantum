package com.quantum.app.feature.chat

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.storage.prefs.PrefsStore
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * 会话列表（按脚本分会话的入口页）：
 * - 数据源 = 会话表为主 LEFT JOIN 每会话最后一条消息（Room Flow，WS/补拉落库即刷新）；
 *   会话有独立存在性（2026-09-18 会话生命周期）：清空记录后会话保留（无预览/不显示时间）；
 * - 未读 = 会话内机器人下发且超过本地已读水位的条数（进入会话即清零）；
 * - 标题 = 任务名（任务列表接口映射）；未知/已删任务/共享会话名直接显示会话键原文；
 * - 长按会话项可删除会话（服务端+本地一起删，任务再推送时自动重建）。
 */
@HiltViewModel
class ConversationListViewModel @Inject constructor(
    private val chatRepository: ChatRepository,
    private val adminApi: AdminApi,
    private val prefsStore: PrefsStore
) : ViewModel() {

    /**
     * 会话列表行模型：标题按任务名映射（miss 回退会话键原文）；preview/time 为 null
     * 表示空会话（清空后保留：只显示会话名，无预览、不显示时间）；未读按水位计算。
     */
    data class ConversationUi(
        val sessionId: String,
        val title: String,
        val preview: String?,
        val time: String?,
        val unread: Int
    )

    private val taskNames = MutableStateFlow<Map<String, String>>(emptyMap())

    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    val conversations: StateFlow<List<ConversationUi>> =
        combine(
            chatRepository.sessionsWithLast(),
            prefsStore.sessionReadSeqs,
            taskNames
        ) { sessions, readSeqs, names ->
            Triple(sessions, readSeqs, names)
        }.flatMapLatest { (sessions, readSeqs, names) ->
            // 未读走单条聚合查询（2.2）：一条 SQL 得到全部会话未读，替代逐会话 COUNT 的 N+1；
            // 消息落库由 Room 盯表自动重查，水位变化经 flatMapLatest 换新查询。
            chatRepository.unreadCounts(sessions.map { it.sessionKey }, readSeqs).map { unread ->
                sessions.map { row ->
                    ConversationUi(
                        sessionId = row.sessionKey,
                        title = titleOf(row.sessionKey, names),
                        preview = row.lastContent?.let { previewOf(row.lastContentType, it) },
                        time = row.lastCreateTime,
                        unread = unread[row.sessionKey] ?: 0
                    )
                }
            }
        }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())

    init {
        loadTaskNames()
    }

    /** 任务名映射：会话键 = 任务 Id；列表一次拉全（PageSize 上限内），未知 Id 回退会话键原文。 */
    fun loadTaskNames() {
        viewModelScope.launch {
            runCatching { adminApi.tasks(pageIndex = 1, pageSize = 500).unwrap() }
                .onSuccess { page -> taskNames.value = page.data.associate { it.id to it.name } }
        }
    }

    /** 标题：默认会话固定文案；其余 miss 任务名时直接显示会话键原文（自定义会话名本身可读）。 */
    private fun titleOf(sessionId: String, names: Map<String, String>): String =
        when {
            sessionId.isEmpty() -> "默认会话"
            else -> names[sessionId] ?: sessionId
        }

    /** 列表预览：通知取标题行、媒体取类型占位、文本取首行。 */
    private fun previewOf(contentType: String?, content: String): String = when (contentType) {
        ChatRepository.NOTIFY_CONTENT_TYPE -> content.lineSequence().firstOrNull()
            ?.removePrefix("【")?.substringBefore("】")?.ifBlank { null }
            ?: content.lineSequence().firstOrNull().orEmpty()
        ChatRepository.CONTENT_TYPE_IMAGE -> "[图片]"
        ChatRepository.CONTENT_TYPE_VIDEO -> "[视频]"
        ChatRepository.CONTENT_TYPE_FILE -> "[文件]"
        else -> content.lineSequence().firstOrNull().orEmpty()
    }

    /** 删除会话（长按列表项确认后调用）：服务端+本地一起删，列表随 Room Flow 自动移除。 */
    fun deleteSession(sessionId: String) {
        viewModelScope.launch {
            runCatching { chatRepository.deleteSession(sessionId) }
                .onSuccess { prefsStore.removeSessionReadSeq(sessionId) }
        }
    }
}
