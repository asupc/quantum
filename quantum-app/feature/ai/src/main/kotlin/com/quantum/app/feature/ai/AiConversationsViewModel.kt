package com.quantum.app.feature.ai

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.AiConversationDto
import com.quantum.app.core.network.dto.AiConversationSaveRequest
import com.quantum.app.core.network.dto.AiDefaultModelDto
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** AI 会话列表页：列表 + 新建（空标题，首条消息由后端取前 20 字生成标题）+ 删除 + 默认模型行。 */
@HiltViewModel
class AiConversationsViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    data class UiState(
        val loading: Boolean = true,
        val conversations: List<AiConversationDto> = emptyList(),
        val defaultModel: AiDefaultModelDto? = null,
        val creating: Boolean = false,
        val toast: String? = null
    )

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state.asStateFlow()

    init {
        load()
    }

    fun load() {
        viewModelScope.launch {
            val (convs, model) = coroutineScope {
                val convs = async { runCatching { adminApi.aiConversations().unwrap() }.getOrDefault(emptyList()) }
                val model = async { runCatching { adminApi.aiDefaultModel().unwrap() }.getOrNull() }
                convs.await() to model.await()
            }
            _state.update { it.copy(loading = false, conversations = convs, defaultModel = model) }
        }
    }

    /** 新建会话（空标题）→ 回调进对话页；标题由后端落首条用户消息时生成。 */
    fun create(onCreated: (String) -> Unit) {
        if (_state.value.creating) return
        viewModelScope.launch {
            _state.update { it.copy(creating = true, toast = null) }
            runCatching {
                adminApi.aiSaveConversation(AiConversationSaveRequest(title = "", allowEnvValues = false)).unwrap()
            }.onSuccess { conv ->
                _state.update { it.copy(creating = false, conversations = it.conversations + conv) }
                onCreated(conv.id)
            }.onFailure { e ->
                _state.update { it.copy(creating = false, toast = errorText(e)) }
            }
        }
    }

    fun delete(id: String) {
        viewModelScope.launch {
            runCatching { adminApi.aiDeleteConversations(id).unwrap() }
                .onSuccess {
                    _state.update { st -> st.copy(conversations = st.conversations.filterNot { it.id == id }, toast = "会话已删除") }
                }
                .onFailure { e -> _state.update { it.copy(toast = errorText(e)) } }
        }
    }

    fun consumeToast() = _state.update { it.copy(toast = null) }

    private fun errorText(e: Throwable): String =
        (e as? ApiException)?.message ?: e.message ?: "请求失败"
}
