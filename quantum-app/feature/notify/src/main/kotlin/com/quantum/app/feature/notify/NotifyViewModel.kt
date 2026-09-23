package com.quantum.app.feature.notify

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.storage.db.NotificationEntity
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

/** 通知中心（A5.3）：分类 tab、全部/单条已读、角标刷新、点按深链。 */
@HiltViewModel
class NotifyViewModel @Inject constructor(
    private val notifyRepository: NotifyRepository
) : ViewModel() {

    val notifications: StateFlow<List<NotificationEntity>> =
        notifyRepository.page(limit = 200)
            .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())

    private val _filter = MutableStateFlow("all")
    val filter: StateFlow<String> = _filter.asStateFlow()

    private val _unread = MutableStateFlow(0)
    val unread: StateFlow<Int> = _unread.asStateFlow()

    init {
        viewModelScope.launch {
            notifyRepository.unreadCount().collect { _unread.value = it }
        }
        refresh()
    }

    fun refresh() {
        viewModelScope.launch { runCatching { notifyRepository.syncFromServer() } }
    }

    fun filter(category: String) {
        _filter.value = category
    }

    fun visibleItems(all: List<NotificationEntity>): List<NotificationEntity> =
        if (_filter.value == "all") all else all.filter { it.category == _filter.value }

    fun markRead(entity: NotificationEntity) {
        viewModelScope.launch { notifyRepository.markRead(entity.msgId) }
    }

    fun markAllRead() {
        viewModelScope.launch { notifyRepository.markAllRead() }
    }
}
