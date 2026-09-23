package com.quantum.app.core.media

import android.content.Context
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import okhttp3.OkHttpClient
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 播放状态快照（悬浮控制/气泡等任何 UI 可观察）：由内部唯一监听器随播放器事件推进。
 * 进度（positionMs）不在此处——沿用「UI 可见时轮询 currentPosition」的既有模式，避免全局定时器。
 */
data class PlaybackState(
    /** 已加载媒体（stop/清空/出错置 IDLE 后为 false；暂停/缓冲/已结束仍为 true）。 */
    val hasMedia: Boolean = false,
    val playing: Boolean = false,
    val buffering: Boolean = false,
    val ended: Boolean = false,
    /** 当前媒体是音频（按 URL 扩展名判定，悬浮按钮只对音频出现）。 */
    val audio: Boolean = false,
    val url: String? = null,
    val title: String? = null,
    val durationMs: Long = 0L
)

/**
 * 应用级播放内核（M1 基础 / M3 补队列）：全局唯一 ExoPlayer + 统一鉴权数据源。
 *
 * - 数据源注入 DI OkHttpClient：其 AuthInterceptor 只对自家域名附 Bearer
 *   （AppUpload 附件 / AppMedia 流媒体天然可播），外链直通、不泄露 JWT——
 *   一个 DataSource 同时覆盖「外链直链」与「服务端鉴权流」两类地址；
 * - 会话页气泡 / 全屏播放器 / 媒体库页 / MediaSession 前台服务共享同一实例，
 *   播放状态全局一致；队列元数据（标题/歌手）随 MediaItem 携带，通知栏直接可用；
 * - 后台播放（M3）由 [MediaSessionService] 以 MediaSession 包裹本实例实现；
 * - [playbackState] 供全局悬浮控制观察（音频退出播放页后续播的场景）。
 */
@Singleton
class PlayerManager @Inject constructor(
    @ApplicationContext context: Context,
    okHttpClient: OkHttpClient
) {
    private val dataSourceFactory = OkHttpDataSource.Factory(okHttpClient)

    private val _playbackState = MutableStateFlow(PlaybackState())

    /** 播放状态流：主线程推进（ExoPlayer 监听器在应用主线程回调），UI 直接收集。 */
    val playbackState: StateFlow<PlaybackState> = _playbackState.asStateFlow()

    /** 全局唯一播放器：惰性创建，进程存活期复用（主线程访问）。创建即挂状态监听。 */
    val player: ExoPlayer by lazy {
        ExoPlayer.Builder(context)
            .setMediaSourceFactory(DefaultMediaSourceFactory(dataSourceFactory))
            .build()
            .also { it.addListener(stateListener) }
    }

    /** 内部状态监听：任何事件后从播放器重读快照（幂等，避免多处拼装状态漂移）。 */
    private val stateListener = object : Player.Listener {
        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            refresh()
        }

        override fun onIsPlayingChanged(playing: Boolean) {
            refresh()
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            refresh()
        }

        override fun onPlayerError(error: androidx.media3.common.PlaybackException) {
            refresh()
        }
    }

    private fun refresh() {
        val p = player
        val url = p.currentMediaItem?.localConfiguration?.uri?.toString()
        _playbackState.value = PlaybackState(
            hasMedia = url != null,
            playing = p.isPlaying,
            buffering = p.playbackState == Player.STATE_BUFFERING,
            ended = p.playbackState == Player.STATE_ENDED,
            audio = url != null && MediaTypes.isAudio(url),
            url = url,
            title = p.currentMediaItem?.mediaMetadata?.title?.toString(),
            durationMs = p.duration.takeIf { it > 0 } ?: 0L
        )
    }

    /** 点播单个媒体：替换当前内容并立即播放（会话气泡/全屏播放器语义）。 */
    fun play(url: String, title: String? = null) {
        val item = if (title.isNullOrBlank()) {
            MediaItem.fromUri(url)
        } else {
            MediaItem.Builder()
                .setUri(url)
                .setMediaMetadata(MediaMetadata.Builder().setTitle(title).build())
                .build()
        }
        player.run {
            setMediaItem(item)
            prepare()
            playWhenReady = true
        }
    }

    /** 队列条目：url + 展示元数据（媒体库/音频气泡点播时随 MediaItem 进通知栏）。 */
    data class QueueItem(
        val url: String,
        val title: String,
        val artist: String? = null
    )

    /** 媒体库点播：以当前列表顺序构建队列（replace 语义）并从 startIndex 播放。 */
    fun playQueue(items: List<QueueItem>, startIndex: Int = 0) {
        if (items.isEmpty()) {
            return
        }
        player.run {
            setMediaItems(items.map { it.toMediaItem() }, startIndex.coerceIn(0, items.size - 1), 0L)
            prepare()
            playWhenReady = true
        }
    }

    /** 追加到队列尾部（不清空当前播放）。 */
    fun append(items: List<QueueItem>) {
        if (items.isEmpty()) {
            return
        }
        player.addMediaItems(items.map { it.toMediaItem() })
        if (player.mediaItemCount == items.size) {
            player.prepare()
            player.playWhenReady = true
        }
    }

    private fun QueueItem.toMediaItem(): MediaItem =
        MediaItem.Builder()
            .setUri(url)
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(title)
                    .setArtist(artist)
                    .build()
            )
            .build()

    /** 停止并清空内容（关闭播放浮层/悬浮控制「停止」时调用，下一次点播重新开始）。 */
    fun stop() {
        player.run {
            stop()
            clearMediaItems()
        }
        refresh()
    }

    companion object {
        /**
         * 悬浮面板展示标题的回退链：显式标题 → URL 尾段文件名（去查询串/锚点、URL 解码）→ 原样 URL。
         * 纯函数，供无元数据的点播路径复用与单测。
         */
        fun displayTitleOf(title: String?, url: String?): String {
            if (!title.isNullOrBlank()) {
                return title
            }
            if (url.isNullOrBlank()) {
                return ""
            }
            val name = url.substringBefore('#').substringBefore('?').substringAfterLast('/')
            if (name.isBlank()) {
                return url
            }
            return runCatching { java.net.URLDecoder.decode(name, "UTF-8") }.getOrDefault(name)
        }
    }
}
