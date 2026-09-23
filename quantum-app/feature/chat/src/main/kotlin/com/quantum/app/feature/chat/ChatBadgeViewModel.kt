package com.quantum.app.feature.chat

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.storage.prefs.PrefsStore
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import javax.inject.Inject

/**
 * 底栏「会话」tab 红点角标的状态源（QQ 式）：任意会话存在超过本地已读水位的
 * 机器人消息即点亮。判定与会话列表同一套（会话表为主的主从行 × 单条聚合查未读，2.2）；
 * 独立 ViewModel 承载——角标常驻于除会话详情外的所有页面，不能寄生在列表页的
 * 生命周期上（列表页离开合成缓存 5s 后即失效）。
 */
@HiltViewModel
class ChatBadgeViewModel @Inject constructor(
    private val chatRepository: ChatRepository,
    prefsStore: PrefsStore
) : ViewModel() {

    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    val hasUnread: StateFlow<Boolean> = combine(
        chatRepository.sessionsWithLast(),
        prefsStore.sessionReadSeqs
    ) { sessions, readSeqs ->
        sessions.map { it.sessionKey } to readSeqs
    }.flatMapLatest { (sessionKeys, readSeqs) ->
        // 未读走单条聚合查询（2.2，与会话列表同口径）：结果只含未读 ≥ 1 的会话，非空即点亮；
        // 消息落库由 Room 盯表自动重查，水位变化（进会话清零）经 flatMapLatest 换新查询。
        chatRepository.unreadCounts(sessionKeys, readSeqs).map { it.isNotEmpty() }
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), false)
}
