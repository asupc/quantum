package com.quantum.app.core.media

import android.content.Intent
import android.annotation.SuppressLint
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.session.MediaSession
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject

/**
 * 媒体前台服务（M3）：MediaSession 包裹全局 PlayerManager 播放器。
 * - 退后台/锁屏仍播，通知栏与锁屏可控（播放/暂停/上下曲），耳机线控经系统路由到同一 Session；
 * - media3 自动管理前台通知与生命周期（无媒体项/暂停超时自动停止前台）；
 * - Android 14+ 要求 manifest 声明 foregroundServiceType="mediaPlayback"（见 app 模块清单）；
 * - 队列/点播全部经 PlayerManager 单例驱动，服务只负责「把播放器挂进系统媒体框架」。
 */
@AndroidEntryPoint
@SuppressLint("Instantiatable") // lint 解析不到 media3 MediaSessionService 的 Service 继承链（library 模块），release lintVital 误报
class MediaSessionService : androidx.media3.session.MediaSessionService() {

    @Inject
    lateinit var playerManager: PlayerManager

    private var mediaSession: MediaSession? = null

    override fun onCreate() {
        super.onCreate()
        val player = playerManager.player
        // 音频属性：音乐内容类型 + USAGE_MEDIA（音频焦点/音量路由交给系统按媒体处理）
        player.setAudioAttributes(
            AudioAttributes.Builder()
                .setUsage(C.USAGE_MEDIA)
                .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                .build(),
            /* handleAudioFocus = */ true
        )
        mediaSession = MediaSession.Builder(this, player).build()
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = mediaSession

    override fun onTaskRemoved(rootIntent: Intent?) {
        val player = mediaSession?.player
        if (player == null || !player.playWhenReady || player.mediaItemCount == 0) {
            stopSelf()
        }
    }

    override fun onDestroy() {
        // 只释放 MediaSession 与服务自身资源（2.6）：player 是 PlayerManager 的进程级
        // lazy 单例（会话页气泡/全屏播放器/媒体库共享），media3 暂停超时/任务移除会频繁
        // 走到这里——此处 release 共享 player 会把它打成 RELEASED 态，后续任何点播
        // 直接 IllegalStateException，必须常驻（播放器生命周期由进程承载）。
        mediaSession?.release()
        mediaSession = null
        super.onDestroy()
    }
}
