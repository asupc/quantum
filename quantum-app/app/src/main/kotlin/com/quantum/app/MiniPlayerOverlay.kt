package com.quantum.app

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import com.quantum.app.core.media.MediaFormat
import com.quantum.app.core.media.PlayerManager
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.delay
import javax.inject.Inject

/**
 * 悬浮播放控制（2026-09-20）：音频退出播放页后续播的全局控制入口。
 *
 * - 悬浮按钮：有已加载的音频（播放/暂停/已结束均可控）时出现在内容区右下角，
 *   点按展开/收起控制面板；视频或无媒体时不出现；
 * - 控制面板：标题（回退 URL 文件名）、可拖动进度条（松手 seek）、播放/暂停
 *   （已结束则从头重播）、停止（停止播放并整体隐藏）；
 * - 展开时全屏遮罩拦截底层页面交互，点遮罩收起；
 * - 进度沿用「面板可见时轮询 currentPosition」模式（与气泡/全屏播放器一致）。
 */
@HiltViewModel
class MiniPlayerViewModel @Inject constructor(
    val playerManager: PlayerManager
) : ViewModel()

@Composable
fun MiniPlayerOverlay(
    viewModel: MiniPlayerViewModel = hiltViewModel(),
    modifier: Modifier = Modifier,
    /** 距内容区底部的额外抬升（沉浸页避开聊天输入条用；底栏可见时传 0 即可）。 */
    bottomPadding: Dp = 0.dp
) {
    val state by viewModel.playerManager.playbackState.collectAsState()
    val player = viewModel.playerManager.player
    var expanded by remember { mutableStateOf(false) }
    var positionMs by remember { mutableStateOf(0L) }
    var durationMs by remember { mutableStateOf(0L) }
    // 拖动中显示拖动值（不跟轮询进度跳变），松手 seek 后清除
    var draggingValue by remember { mutableStateOf<Float?>(null) }

    val visible = state.hasMedia && state.audio

    // 播放停止（停止/清空/被替换为视频）时整体隐藏并收起面板
    LaunchedEffect(visible) {
        if (!visible) {
            expanded = false
        }
    }

    if (!visible) {
        return
    }

    // 面板展开期间轮询进度（250ms；含暂停态，展开瞬间即可读到当前进度）
    LaunchedEffect(expanded) {
        while (expanded) {
            positionMs = player.currentPosition.coerceAtLeast(0)
            durationMs = player.duration.takeIf { it > 0 } ?: 0L
            delay(250)
        }
    }

    val togglePlay = {
        player.run {
            if (playbackState == androidx.media3.common.Player.STATE_ENDED) {
                viewModel.playerManager.play(state.url.orEmpty(), state.title)
            } else if (isPlaying) {
                pause()
            } else {
                play()
            }
        }
    }

    Box(modifier = modifier) {
        // 展开时的遮罩：拦截底层页面交互，点按收起（无涟漪）
        if (expanded) {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .background(Color.Black.copy(alpha = 0.35f))
                    .clickable(
                        interactionSource = remember { MutableInteractionSource() },
                        indication = null
                    ) { expanded = false }
            )
        }
        Column(
            modifier = Modifier
                .align(Alignment.BottomEnd)
                .padding(end = 16.dp, bottom = bottomPadding + 12.dp),
            horizontalAlignment = Alignment.End
        ) {
            AnimatedVisibility(
                visible = expanded,
                enter = fadeIn() + expandVertically(expandFrom = Alignment.Bottom),
                exit = fadeOut() + shrinkVertically(shrinkTowards = Alignment.Bottom)
            ) {
                val dur = durationMs.takeIf { it > 0 } ?: state.durationMs
                val seekable = dur > 0
                Surface(
                    shape = RoundedCornerShape(16.dp),
                    color = MaterialTheme.colorScheme.surfaceContainerHigh,
                    tonalElevation = 4.dp,
                    shadowElevation = 8.dp,
                    modifier = Modifier.widthIn(max = 340.dp)
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.padding(horizontal = 6.dp, vertical = 6.dp)
                    ) {
                        IconButton(onClick = togglePlay, modifier = Modifier.size(44.dp)) {
                            Icon(
                                if (state.playing) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                                contentDescription = if (state.playing) "暂停" else "播放",
                                tint = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.size(30.dp)
                            )
                        }
                        Column(
                            modifier = Modifier
                                .weight(1f)
                                .padding(horizontal = 2.dp)
                        ) {
                            Text(
                                PlayerManager.displayTitleOf(state.title, state.url),
                                style = MaterialTheme.typography.titleSmall,
                                color = MaterialTheme.colorScheme.onSurface,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(
                                    MediaFormat.duration(
                                        if (draggingValue != null && seekable) (draggingValue!! * dur).toLong() else positionMs
                                    ),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                                Slider(
                                    value = if (seekable) {
                                        (draggingValue ?: positionMs.toFloat() / dur).coerceIn(0f, 1f)
                                    } else {
                                        0f
                                    },
                                    onValueChange = { if (seekable) draggingValue = it },
                                    onValueChangeFinished = {
                                        draggingValue?.let { v ->
                                            if (seekable) {
                                                player.seekTo((v * dur).toLong())
                                                positionMs = (v * dur).toLong()
                                            }
                                        }
                                        draggingValue = null
                                    },
                                    enabled = seekable,
                                    modifier = Modifier
                                        .weight(1f)
                                        .height(22.dp)
                                        .padding(horizontal = 6.dp)
                                )
                                Text(
                                    MediaFormat.duration(dur),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        }
                        IconButton(
                            onClick = { viewModel.playerManager.stop() },
                            modifier = Modifier.size(44.dp)
                        ) {
                            Icon(
                                Icons.Filled.Stop,
                                contentDescription = "停止播放",
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.size(24.dp)
                            )
                        }
                    }
                }
            }
            Spacer(modifier = Modifier.height(10.dp))
            // 悬浮按钮：播放中主色、暂停/结束弱化底；阴影浮于页面内容之上
            Box(
                modifier = Modifier
                    .shadow(6.dp, CircleShape)
                    .size(50.dp)
                    .clip(CircleShape)
                    .background(
                        if (state.playing) {
                            MaterialTheme.colorScheme.primary
                        } else {
                            MaterialTheme.colorScheme.surfaceVariant
                        }
                    )
                    .border(
                        1.dp,
                        MaterialTheme.colorScheme.outline.copy(alpha = 0.35f),
                        CircleShape
                    )
                    .clickable { expanded = !expanded },
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    Icons.Filled.MusicNote,
                    contentDescription = if (expanded) "收起播放控制" else "音频播放控制",
                    tint = if (state.playing) {
                        MaterialTheme.colorScheme.onPrimary
                    } else {
                        MaterialTheme.colorScheme.onSurfaceVariant
                    },
                    modifier = Modifier.size(24.dp)
                )
            }
        }
    }
}
