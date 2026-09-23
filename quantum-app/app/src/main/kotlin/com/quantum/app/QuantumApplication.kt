package com.quantum.app

import android.app.Application
import com.quantum.app.core.push.SystemNotifier
import com.quantum.app.core.network.session.SessionManager
import dagger.hilt.android.HiltAndroidApp
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * App 入口：
 * - 通知渠道初始化（quantum_task/system/chat）；
 * - SessionManager 载入登录态 + Access 到期前 10 分钟主动续期循环；
 * - AppPushHandler 随 Application 创建：WS 常驻收集者（消息落库 + 后台本地通知）在此挂载。
 * （厂商离线推送已整体移除：实时性由常驻 WS 承担，后台靠系统锁定保活。）
 */
@HiltAndroidApp
class QuantumApplication : Application() {

    @Inject lateinit var sessionManager: SessionManager
    @Inject lateinit var systemNotifier: SystemNotifier
    @Inject lateinit var pushHandler: AppPushHandler

    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    override fun onCreate() {
        super.onCreate()
        systemNotifier.ensureChannels()
        // 访问即触发 Hilt 实例化：WS 常驻收集者与生命周期观察者随之生效
        check(pushHandler != null)
        appScope.launch {
            sessionManager.load()
            sessionManager.startRenewalLoop(appScope)
            // 冷启动恢复会话的 WS 补建：onStart 可能早于 load() 触发而空转，此处令牌已就绪再连一次
            pushHandler.onSessionReady()
        }
    }
}
