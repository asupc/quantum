package com.quantum.app.feature.mine

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.AppDeviceDto
import com.quantum.app.core.network.dto.NotifySettingDto
import com.quantum.app.core.network.session.SessionManager
import com.quantum.app.core.storage.prefs.PrefsStore
import com.quantum.app.core.storage.prefs.TokenStore
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * 我的：设备管理（远程解绑）、通知偏好（三类开关 + 免打扰，服务端 t_app_notify_setting）、
 * 生物识别门禁、扫码授权（摄像头扫码 → scan-login）、个人信息。
 * 版本与端内自更新已整体移除（升级改为手动安装新 APK）。
 */
@HiltViewModel
class MineViewModel @Inject constructor(
    private val appApi: AppApi,
    private val sessionManager: SessionManager,
    private val tokenStore: TokenStore,
    private val prefsStore: PrefsStore,
    wsClient: com.quantum.app.core.network.ws.AppWsClient
) : ViewModel() {

    /** 消息通道状态（实时刷新与会话推送的硬前提）：已连接/连接中/未连接。 */
    val wsState: StateFlow<com.quantum.app.core.network.ws.WsState> = wsClient.state

    private val _devices = MutableStateFlow<List<AppDeviceDto>>(emptyList())
    val devices: StateFlow<List<AppDeviceDto>> = _devices.asStateFlow()

    private val _notifyPrefs = MutableStateFlow(NotifySettingDto())
    val notifyPrefs: StateFlow<NotifySettingDto> = _notifyPrefs.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    val userName: StateFlow<String?> = tokenStore.userName
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null)

    val biometricLock: StateFlow<Boolean> = prefsStore.biometricLock
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), false)

    fun loadDeviceList() {
        viewModelScope.launch {
            runCatching { appApi.devices().unwrap() }
                .onSuccess { _devices.value = it }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    fun unbindDevice(deviceId: String) {
        viewModelScope.launch {
            runCatching { appApi.unbindDevice(com.quantum.app.core.network.dto.UnbindDeviceRequest(deviceId)).unwrap() }
                .onSuccess { loadDeviceList() }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    fun loadNotifyPrefs() {
        viewModelScope.launch {
            runCatching { appApi.notifySetting().unwrap() }
                .onSuccess { _notifyPrefs.value = it }
                .onFailure {
                    // 拉不到服务端时退回**本地镜像**（真正决定抑制行为的那份），
                    // 不要把界面重置成全默认——那看起来就像"设置丢了"
                    _notifyPrefs.value = NotifySettingDto(
                        taskPush = prefsStore.taskPush.first(),
                        systemPush = prefsStore.systemPush.first(),
                        securityPush = prefsStore.securityPush.first(),
                        dndStart = prefsStore.dndStart.first(),
                        dndEnd = prefsStore.dndEnd.first()
                    )
                    _toast.value = "通知偏好读取失败，已展示本地生效的设置"
                }
        }
    }

    /**
     * 保存通知偏好：**先落本地镜像再上报服务端**。
     * 抑制判定的数据源是本地 PrefsStore（[com.quantum.app.AppPushHandler.suppressedByPrefs]），
     * 早先只 PUT 服务端、本地镜像仅登录时刷新一次 → 「保存成功但免打扰不生效」，
     * 直到下次重新登录才追上（用户报的'设置保存后像丢了'）。
     */
    fun saveNotifyPrefs(prefs: NotifySettingDto) {
        _notifyPrefs.value = prefs
        viewModelScope.launch {
            prefsStore.saveNotifyPrefs(
                task = prefs.taskPush,
                system = prefs.systemPush,
                security = prefs.securityPush,
                dndStart = prefs.dndStart,
                dndEnd = prefs.dndEnd
            )
            runCatching { appApi.updateNotifySetting(prefs).unwrap() }
                .onSuccess { _toast.value = "通知偏好已保存，免打扰立即生效" }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message ?: "保存失败" }
        }
    }

    fun setBiometricLock(enabled: Boolean) {
        viewModelScope.launch { prefsStore.setBiometricLock(enabled) }
    }

    fun logout() {
        viewModelScope.launch {
            sessionManager.logout()
        }
    }

    /**
     * 扫码登录授权：票据来自扫码页摄像头识别（或手动输入兜底）。
     * 挂起返回是否成功，便于扫码页展示"已授权"并返回；失败文案写入 [toast]。
     */
    suspend fun authorizeScan(ticket: String): Boolean {
        return runCatching {
            appApi.scanLogin(com.quantum.app.core.network.dto.ScanLoginRequest(ticket)).unwrap()
        }.fold(
            onSuccess = { true },
            onFailure = { e ->
                _toast.value = (e as? ApiException)?.message ?: e.message ?: "授权失败，请重试"
                false
            }
        )
    }

    fun consumeToast() {
        _toast.value = null
    }
}
