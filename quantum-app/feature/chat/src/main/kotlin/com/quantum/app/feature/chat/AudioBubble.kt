package com.quantum.app.feature.chat

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import com.quantum.app.core.media.PlayerManager
import kotlinx.coroutines.delay

/**
 * 音频气泡（M2）：迷你条 = 播放/暂停圆钮 + 标题 + 可拖动进度条（点按条内位置即跳转）。
 * 播放走全局 PlayerManager（与视频浮层/媒体库共享同一实例）；
 * 多个音频气泡并存时按「当前正在播的媒体项 uri」判定本气泡是否激活，
 * 点按未激活气泡即切歌（点播语义）；离开会话页播放不停（悬浮按钮接管控制）。
 * 失败不静默：条内提示可重试。[onSave] 非空时（外链试听气泡）展示「保存到服务器」小按钮。
 */
@Composable
fun AudioBubble(
    url: String,
    title: String,
    playerManager: PlayerManager,
    onSave: (() -> Unit)? = null
) {
    val player = playerManager.player
    var active by remember { mutableStateOf(false) }
    var isPlaying by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(false) }
    var positionMs by remember { mutableStateOf(0L) }
    var durationMs by remember { mutableStateOf(0L) }
    // 拖动中显示拖动值（不跟轮询进度跳变），松手 seek 后清除
    var draggingValue by remember { mutableStateOf<Float?>(null) }

    DisposableEffect(url) {
        fun refresh() {
            val current = player.currentMediaItem?.localConfiguration?.uri?.toString()
            active = current == url
            isPlaying = player.isPlaying
            durationMs = player.duration.takeIf { it > 0 } ?: 0L
            positionMs = player.currentPosition.coerceAtLeast(0)
        }
        refresh()
        val listener = object : Player.Listener {
            override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                refresh()
                failed = false
            }

            override fun onIsPlayingChanged(playing: Boolean) {
                isPlaying = playing
                if (playing) {
                    refresh()
                }
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                durationMs = player.duration.takeIf { it > 0 } ?: 0L
                if (playbackState == Player.STATE_ENDED) {
                    isPlaying = false
                }
            }

            override fun onPlayerError(error: PlaybackException) {
                if (active) {
                    failed = true
                }
            }
        }
        player.addListener(listener)
        onDispose { player.removeListener(listener) }
    }

    // 激活且播放中才轮询进度（多气泡并存时只有当前条在跑协程）
    LaunchedEffect(active, isPlaying) {
        while (active && isPlaying) {
            positionMs = player.currentPosition.coerceAtLeast(0)
            delay(250)
        }
    }

    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clip(CircleShape)
            .clickable {
                when {
                    failed -> {
                        failed = false
                        playerManager.play(url, title)
                    }
                    active && isPlaying -> player.pause()
                    active -> player.play()
                    else -> playerManager.play(url, title)
                }
            }
            .padding(vertical = 2.dp)
    ) {
        Box(
            modifier = Modifier
                .size(36.dp)
                .clip(CircleShape)
                .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.14f)),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                if (active && isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                contentDescription = if (active && isPlaying) "暂停" else "播放",
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(20.dp)
            )
        }
        Spacer(modifier = Modifier.width(10.dp))
        Column(modifier = Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Filled.MusicNote,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(14.dp)
                )
                Spacer(modifier = Modifier.width(4.dp))
                Text(
                    title,
                    style = MaterialTheme.typography.bodyMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f)
                )
            }
            if (failed) {
                Text(
                    "播放失败（链接可能已失效），点按重试",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.error,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            } else {
                val seekable = active && durationMs > 0
                // 紧凑滑条：高度压到 20dp（默认 48dp 会撑高气泡），未激活/未知时长时置灰展示 0
                Slider(
                    value = if (seekable) {
                        (draggingValue ?: positionMs.toFloat() / durationMs).coerceIn(0f, 1f)
                    } else {
                        0f
                    },
                    onValueChange = { if (seekable) draggingValue = it },
                    onValueChangeFinished = {
                        draggingValue?.let { v ->
                            player.seekTo((v * durationMs).toLong())
                            positionMs = (v * durationMs).toLong()
                        }
                        draggingValue = null
                    },
                    enabled = seekable,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(20.dp)
                        .padding(top = 2.dp)
                )
            }
        }
        if (onSave != null) {
            Spacer(modifier = Modifier.width(4.dp))
            Icon(
                Icons.Outlined.Download,
                contentDescription = "保存到服务器",
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier
                    .size(28.dp)
                    .clip(CircleShape)
                    .clickable { onSave() }
                    .padding(5.dp)
            )
        }
    }
}
