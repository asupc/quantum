package com.quantum.app.feature.chat

import android.content.Intent
import android.net.Uri
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import com.quantum.app.core.media.MediaFormat
import com.quantum.app.core.media.MediaSeek
import com.quantum.app.core.media.MediaTypes
import com.quantum.app.core.media.PlayerManager
import com.quantum.app.core.media.SeekZone
import kotlinx.coroutines.delay

/**
 * 全屏端内播放器（M1）：视频消息、可播文件/音频直链共用一个浮层。
 *
 * 交互对齐 ImageViewerDialog：单击切换控制条；双击分区——左右 1/3 快退/快进 10 秒、
 * 中间播放/暂停；系统返回或点关闭退出。
 * 进入即播；退出语义按媒体分流——视频退出即停，音频退出续播（全局悬浮按钮接管控制）。
 * - 音频源无画面：中央音符图标 + 标题占位；
 * - poster（2026-09-18 批次3）：视频封面盖层，起播出画面前显示（消除缓冲期黑屏）；
 * - 播放失败（GD 直链过期/格式不支持）不静默：中央给出重试与「系统播放器」兜底。
 */
@Composable
fun MediaViewerDialog(
    url: String,
    title: String?,
    playerManager: PlayerManager,
    poster: String? = null,
    imageLoader: coil.ImageLoader? = null,
    onDismiss: () -> Unit
) {
    val player = playerManager.player
    val context = LocalContext.current
    var controlsVisible by remember { mutableStateOf(true) }
    var isPlaying by remember { mutableStateOf(false) }
    var isBuffering by remember { mutableStateOf(true) }
    var ended by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(false) }
    var firstFrameShown by remember { mutableStateOf(false) }
    var positionMs by remember { mutableStateOf(0L) }
    var durationMs by remember { mutableStateOf(0L) }
    // 双击快进/快退提示（BACKWARD/FORWARD，短促自清）——控制条隐藏时也能确认手势已生效
    var seekHint by remember { mutableStateOf<SeekZone?>(null) }
    val isAudio = MediaTypes.isAudio(url)

    LaunchedEffect(seekHint) {
        if (seekHint != null) {
            delay(650)
            seekHint = null
        }
    }

    DisposableEffect(url) {
        failed = false
        ended = false
        firstFrameShown = false
        playerManager.play(url, title)
        val listener = object : Player.Listener {
            override fun onIsPlayingChanged(playing: Boolean) {
                isPlaying = playing
                // 起播即视为首帧将至（media3 各版本 onRenderedFirstFrame 签名不稳，用播放态近似）
                if (playing) {
                    firstFrameShown = true
                }
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                isBuffering = playbackState == Player.STATE_BUFFERING
                ended = playbackState == Player.STATE_ENDED
                durationMs = player.duration.takeIf { it > 0 } ?: 0L
            }

            override fun onPlayerError(error: PlaybackException) {
                failed = true
                isBuffering = false
            }
        }
        player.addListener(listener)
        onDispose {
            player.removeListener(listener)
            // 退出语义按媒体类型分流：视频返回即停；音频续播（全局单例继续放，
            // 悬浮按钮接管控制）。失败态的音频一并停止——不可播的内容不留在后台。
            if (!isAudio || failed) {
                playerManager.stop()
            }
        }
    }

    // 播放中轮询进度（200ms 足够顺滑，也避免每帧重组）
    LaunchedEffect(isPlaying, isBuffering) {
        while (isPlaying || isBuffering) {
            positionMs = player.currentPosition.coerceAtLeast(0)
            durationMs = player.duration.takeIf { it > 0 } ?: 0L
            delay(200)
        }
    }

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(
            usePlatformDefaultWidth = false,
            // 全屏沉浸声明：部分设备（横屏平板等）dialog window 延伸到系统栏下时，
            // 顶部控制条的 statusBarsPadding 让开状态栏。注意本机（vivo Android 16）
            // 实测 dialog 内系统 inset 恒为 0，此声明在该机型无可见效果，底部抬升
            // 不依赖 inset（见底部控制条注释）。
            decorFitsSystemWindows = false
        )
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
                .pointerInput(Unit) {
                    detectTapGestures(
                        onTap = { controlsVisible = !controlsVisible },
                        // 双击分区（bilibili 风格）：左 1/3 快退 10s、右 1/3 快进 10s、中间播放/暂停
                        onDoubleTap = { offset ->
                            when (MediaSeek.zoneOf(offset.x, size.width.toFloat())) {
                                SeekZone.BACKWARD -> {
                                    if (seekBy(player, -SEEK_STEP_MS)) {
                                        positionMs = player.currentPosition
                                        seekHint = SeekZone.BACKWARD
                                    }
                                }
                                SeekZone.FORWARD -> {
                                    if (seekBy(player, +SEEK_STEP_MS)) {
                                        positionMs = player.currentPosition
                                        seekHint = SeekZone.FORWARD
                                    }
                                }
                                SeekZone.CENTER -> togglePlay(playerManager, url, title)
                            }
                        }
                    )
                }
        ) {
            if (isAudio) {
                // 音频占位：无画面，中央音符 + 标题
                Column(
                    modifier = Modifier.align(Alignment.Center).padding(horizontal = 32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Icon(
                        Icons.Filled.MusicNote,
                        contentDescription = null,
                        tint = Color.White.copy(alpha = 0.85f),
                        modifier = Modifier.size(72.dp)
                    )
                    if (!title.isNullOrBlank()) {
                        Spacer(modifier = Modifier.height(12.dp))
                        Text(
                            title,
                            color = Color.White.copy(alpha = 0.9f),
                            style = MaterialTheme.typography.titleMedium,
                            textAlign = TextAlign.Center,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                }
            } else {
                AndroidView(
                    factory = { context ->
                        androidx.media3.ui.PlayerView(context).apply {
                            useController = false
                            this.player = player
                        }
                    },
                    onRelease = { it.player = null },
                    modifier = Modifier.fillMaxSize()
                )
                // 封面盖层（批次3）：起播出画面前显示，消除缓冲期黑屏；失败态仍在最上层提示
                if (poster != null && !firstFrameShown && !failed) {
                    coil.compose.AsyncImage(
                        model = poster,
                        contentDescription = "视频封面",
                        imageLoader = imageLoader ?: coil.Coil.imageLoader(context),
                        contentScale = androidx.compose.ui.layout.ContentScale.Crop,
                        modifier = Modifier.fillMaxSize()
                    )
                }
            }

            if (failed) {
                Column(
                    modifier = Modifier.align(Alignment.Center).padding(horizontal = 32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text(
                        "播放失败，链接可能已过期或格式不支持",
                        color = Color.White,
                        style = MaterialTheme.typography.bodyLarge,
                        textAlign = TextAlign.Center
                    )
                    Spacer(modifier = Modifier.height(12.dp))
                    Row {
                        TextButton(onClick = {
                            failed = false
                            playerManager.play(url, title)
                        }) { Text("重试") }
                        Spacer(modifier = Modifier.width(8.dp))
                        TextButton(onClick = { openWithSystemPlayer(context = context, url = url) }) {
                            Text("系统播放器")
                        }
                    }
                }
            } else if (isBuffering) {
                CircularProgressIndicator(
                    color = Color.White,
                    modifier = Modifier.align(Alignment.Center).size(40.dp)
                )
            }

            // 双击快进/快退提示浮标（0.65s 自清；控制条隐藏时也能确认手势已生效）
            seekHint?.let { zone ->
                Text(
                    if (zone == SeekZone.FORWARD) "快进 10 秒" else "快退 10 秒",
                    color = Color.White,
                    style = MaterialTheme.typography.labelLarge,
                    modifier = Modifier
                        .align(if (zone == SeekZone.BACKWARD) Alignment.CenterStart else Alignment.CenterEnd)
                        .padding(horizontal = 36.dp)
                        .background(Color.Black.copy(alpha = 0.55f), RoundedCornerShape(50))
                        .padding(horizontal = 14.dp, vertical = 8.dp)
                )
            }

            // 控制条：顶部关闭 + 标题，底部播放/暂停 + 进度
            // statusBarsPadding：decorFitsSystemWindows=false 后 window 真全屏，顶部须让开状态栏
            // （背景画在 padding 内侧，状态栏区域透出视频，与主流播放器一致）
            AnimatedVisibility(visible = controlsVisible, modifier = Modifier.align(Alignment.TopStart)) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .statusBarsPadding()
                        .background(Color.Black.copy(alpha = 0.5f))
                        .padding(horizontal = 4.dp, vertical = 2.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = onDismiss) {
                        Icon(
                            Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = "关闭播放器",
                            tint = Color.White
                        )
                    }
                    if (!title.isNullOrBlank()) {
                        Text(
                            title,
                            color = Color.White,
                            style = MaterialTheme.typography.titleSmall,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                }
            }
            AnimatedVisibility(visible = controlsVisible, modifier = Modifier.align(Alignment.BottomStart)) {
                var draggingValue by remember { mutableStateOf<Float?>(null) }
                val sliderValue = draggingValue ?: if (durationMs > 0) {
                    positionMs.toFloat() / durationMs
                } else {
                    0f
                }
                Row(
                    // 进度条离底固定抬升（2026-09-21 三次修复，放弃 inset 路线）：
                    // 本 Dialog 内 WindowInsets.navigationBars/tappableElement 在部分真机
                    // （vivo Android 16）恒为 0，dialog window 不与系统栏重叠，inset 类
                    // padding 是空操作（09-20 navigationBarsPadding、09-21 tappableElement
                    // 两轮均实测无效果）。改为纯固定 56dp：控制条底边离 dialog 底边足够远
                    // （32dp 实测仍偏低，56dp 一次到位），手指拖动不落进小白条手势区。
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(bottom = 56.dp)
                        .background(Color.Black.copy(alpha = 0.5f))
                        .padding(horizontal = 12.dp, vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = { togglePlay(playerManager, url, title) }) {
                        Icon(
                            if (isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                            contentDescription = if (isPlaying) "暂停" else "播放",
                            tint = Color.White
                        )
                    }
                    Text(
                        MediaFormat.duration(if (draggingValue != null && durationMs > 0) (draggingValue!! * durationMs).toLong() else positionMs),
                        color = Color.White.copy(alpha = 0.85f),
                        style = MaterialTheme.typography.labelMedium
                    )
                    Slider(
                        value = sliderValue,
                        onValueChange = { draggingValue = it },
                        onValueChangeFinished = {
                            draggingValue?.let { v ->
                                if (durationMs > 0) {
                                    player.seekTo((v * durationMs).toLong())
                                    positionMs = (v * durationMs).toLong()
                                }
                            }
                            draggingValue = null
                        },
                        modifier = Modifier.weight(1f).padding(horizontal = 8.dp)
                    )
                    Text(
                        MediaFormat.duration(durationMs),
                        color = Color.White.copy(alpha = 0.85f),
                        style = MaterialTheme.typography.labelMedium
                    )
                }
            }
        }
    }
}

/** 双击快进/快退步长。 */
private const val SEEK_STEP_MS = 10_000L

/** 按步长 seek（duration 未知时不动）；返回是否生效。 */
private fun seekBy(player: ExoPlayer, deltaMs: Long): Boolean {
    val target = MediaSeek.targetPositionOf(player.currentPosition, player.duration, deltaMs) ?: return false
    player.seekTo(target)
    return true
}

/** 播放/暂停切换；已结束时从头重播。 */
private fun togglePlay(playerManager: PlayerManager, url: String, title: String?) {
    playerManager.player.run {
        if (playbackState == Player.STATE_ENDED) {
            playerManager.play(url, title)
        } else if (isPlaying) {
            pause()
        } else {
            play()
        }
    }
}

/** 兜底：交给系统播放器（无可处理应用时静默忽略，与旧 openExternally 行为一致）。 */
private fun openWithSystemPlayer(context: android.content.Context, url: String) {
    runCatching {
        context.startActivity(
            Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }
}
