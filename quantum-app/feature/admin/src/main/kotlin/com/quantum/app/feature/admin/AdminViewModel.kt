package com.quantum.app.feature.admin

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.EnvDto
import com.quantum.app.core.network.dto.EnvResultDto
import com.quantum.app.core.network.dto.SystemSettingDto
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * 管理面（feature:admin）：环境变量 / 系统设置（单管理员体系：服务端 [ManagerOnly]，UI 恒可见）。
 * 快捷回复/自定义数据/书签/外触内执管理入口已按需移除（2026-09-18，聊天页快捷指令面板保留）；
 * 用户管理随用户体系移除；版本管理已移除（升级改为手动安装新 APK）。
 */
@HiltViewModel
class AdminViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    private val _envs = MutableStateFlow<List<EnvResultDto>>(emptyList())
    val envs: StateFlow<List<EnvResultDto>> = _envs.asStateFlow()

    private val _setting = MutableStateFlow<SystemSettingDto?>(null)
    val setting: StateFlow<SystemSettingDto?> = _setting.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    /** 系统设置保存中（驱动按钮 loading，避免重复提交）。 */
    private val _saving = MutableStateFlow(false)
    val saving: StateFlow<Boolean> = _saving.asStateFlow()

    /** 数据库收缩执行中。 */
    private val _shrinking = MutableStateFlow(false)
    val shrinking: StateFlow<Boolean> = _shrinking.asStateFlow()

    // ---- 环境变量 ----
    fun loadEnvs() {
        viewModelScope.launch {
            runCatching { adminApi.envs(pageIndex = 1, pageSize = 100).unwrap() }
                .onSuccess { _envs.value = it.data }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    fun saveEnv(env: EnvDto, isNew: Boolean) {
        viewModelScope.launch {
            runCatching { adminApi.saveEnvs(listOf(env)).unwrap() }
                .onSuccess { messages ->
                    // 服务端对「同名同值」的重复项会跳过并回提示，不能当成功静默吞掉
                    _toast.value = messages.firstOrNull()?.takeIf { it.isNotBlank() }
                        ?: if (isNew) "已新增并启用" else "已保存"
                    loadEnvs()
                }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    /** 启停单个变量（服务端 PUT api/Env/enable|disable?ids=）。 */
    fun toggleEnv(env: EnvResultDto) {
        viewModelScope.launch {
            runCatching {
                if (env.enable) adminApi.disableEnvs(env.id).unwrap()
                else adminApi.enableEnvs(env.id).unwrap()
            }.onSuccess {
                _toast.value = if (env.enable) "已停用 ${env.name}" else "已启用 ${env.name}"
                loadEnvs()
            }.onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    fun deleteEnv(id: String) {
        viewModelScope.launch {
            runCatching { adminApi.deleteEnvs(id).unwrap() }
                .onSuccess { loadEnvs() }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    // ---- 系统设置（管理员） ----
    fun loadSetting() {
        viewModelScope.launch {
            runCatching { adminApi.systemSetting().unwrap() }
                .onSuccess { _setting.value = it }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    fun saveSetting(setting: SystemSettingDto) {
        viewModelScope.launch {
            _saving.value = true
            // 不走 unwrap：启动期三件套（可信代理/跨域白名单/Swagger）变更时服务端把
            // 「需重启生效」提示放在信封 Message，成功提示按其透传
            runCatching { adminApi.updateSystemSetting(setting) }
                .onSuccess { envelope ->
                    if (envelope.code == 200) {
                        _toast.value = envelope.message
                            ?.takeIf { it.isNotBlank() && it != "Success" }
                            ?: "设置已保存"
                        // 重拉：AppKey 掩码回显，避免界面残留明文（与 Web 同款语义）
                        loadSetting()
                    } else {
                        _toast.value = envelope.message ?: "保存失败"
                    }
                }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
            _saving.value = false
        }
    }

    /** 收缩数据库（SQLite VACUUM / MySQL OPTIMIZE），与 Web 设置页同端点。 */
    fun shrinkDatabase() {
        viewModelScope.launch {
            _shrinking.value = true
            runCatching { adminApi.databaseShrink().unwrap() }
                .onSuccess { _toast.value = "数据库收缩完成" }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
            _shrinking.value = false
        }
    }

    fun consumeToast() {
        _toast.value = null
    }
}