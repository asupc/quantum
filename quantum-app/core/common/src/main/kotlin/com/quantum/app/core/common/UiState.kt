package com.quantum.app.core.common

/** 通用 UI 状态机。 */
sealed interface UiState<out T> {
    data object Idle : UiState<Nothing>
    data object Loading : UiState<Nothing>
    data class Success<T>(val data: T) : UiState<T>
    data class Failed(val message: String) : UiState<Nothing>
}
