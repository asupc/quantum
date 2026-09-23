package com.quantum.app.feature.chat

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.rememberTransformableState
import androidx.compose.foundation.gestures.transformable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.isImeVisible
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.hilt.navigation.compose.hiltViewModel
import coil.compose.AsyncImage
import com.quantum.app.core.common.TimeUtil
import com.quantum.app.core.common.R as CommonR
import com.quantum.app.core.common.ui.theme.QuantumRadius
import com.quantum.app.core.common.ui.theme.QuantumSize
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import com.quantum.app.core.media.MediaTypes
import com.quantum.app.core.storage.db.ChatMessageEntity
import com.quantum.app.core.storage.db.NotificationEntity
import com.quantum.app.feature.chat.upload.Uploads
import java.io.File
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

/**
 * 会话页（A5.2 + QQ 风格对齐）：头像 + 气泡 + 时间分隔 + 发送状态位，
 * 站内通知以服务号卡片呈现（分类标签/标题/正文/点按走 jump 深链）；
 * 输入面板含相册/拍照/文件（先 AppUpload 再发消息）、快捷指令面板（含「Q 退出」提示）、
 * 已读回执（可见且滚动到底，节流在 ViewModel）。
 *
 * 消息文本可选：气泡正文、文件名、通知卡片标题/正文均包在 [SelectionContainer] 内，
 * 长按进入系统文本选择（拖选 + 复制/全选悬浮菜单）；普通点击不受影响，通知卡片仍能点按跳转。
 *
 * @param onOpenJump 通知卡片点按回调：由 Shell 解析 jump 深链（本页不做路由解析）
 * @param onSwitchSession 跨会话触发迁移自动切换回调：本会话是迁移来源时回调目标会话键
 *（由 Shell 接导航，与手点列表项同款路由；其余会话/列表态不触发）
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun ChatScreen(
    onOpenJump: (String) -> Unit = {},
    onBack: (() -> Unit)? = null,
    onSwitchSession: (String) -> Unit = {},
    viewModel: ChatViewModel = hiltViewModel()
) {
    val context = LocalContext.current
    val imageLoader = viewModel.imageLoader
    val sessionTitle by viewModel.title.collectAsState()
    val messages by viewModel.messages.collectAsState()
    val notifyMeta by viewModel.notifyMeta.collectAsState()
    var showPanel by remember { mutableStateOf(false) }
    var showQuickCommands by remember { mutableStateOf(false) }
    var cameraFile by remember { mutableStateOf<File?>(null) }
    // 待发送附件：选中后先进预览对话框（可填配文），点发送才上传+发送
    var pendingAttachment by remember { mutableStateOf<Pair<Uploads.UploadKind, Uri>?>(null) }
    var captionInput by remember { mutableStateOf("") }
    // 全屏大图查看：点按气泡图片置入待查看地址，非空时浮层展示
    var viewingImage by remember { mutableStateOf<String?>(null) }
    // 全屏端内播放（M1）：点按视频消息/可播文件置入 (地址, 标题, 封面)，非空时浮层播放
    var playingMedia by remember { mutableStateOf<Triple<String, String, String?>?>(null) }
    // 删除会话确认弹窗（详情页右上角入口，2026-09-21 起与列表长按删除同语义）
    var showDeleteConfirm by remember { mutableStateOf(false) }
    val listState = rememberLazyListState()
    // 在屏会话上报：通知抑制按「该会话页正被用户看着」判定（而非 App 整体前台），
    // 离开本页/切会话自动清空或改写（AppPushHandler 收帧时读取）
    DisposableEffect(viewModel.sessionId) {
        ChatScreenTracker.visibleSessionId = viewModel.sessionId
        onDispose { ChatScreenTracker.visibleSessionId = null }
    }

    val takePicture = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { ok ->
        val file = cameraFile
        if (ok && file != null) {
            pendingAttachment = Uploads.UploadKind.IMAGE to Uri.fromFile(file)
        }
    }
    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        uri?.let { pendingAttachment = Uploads.UploadKind.IMAGE to it }
    }
    val pickFile = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        uri?.let { pendingAttachment = Uploads.UploadKind.FILE to it }
    }

    LaunchedEffect(Unit) {
        viewModel.refresh()
    }
    // 跨会话触发迁移自动切换（2026-09-18 批次）：本会话是迁移来源时导航到目标会话；
    // §4-2：collector 由 ChatViewModel.observeSessionMoves 持有（movesJob），再次调用会先 cancel 旧 job、
    // onCleared 一并取消，故重复进入/重组不会叠加多个 collector、一次迁移只切一次
    LaunchedEffect(Unit) {
        viewModel.observeSessionMoves { target -> onSwitchSession(target) }
    }
    // 首屏定位必须无动画直接落底（瞬时跳转）：首帧数据从空到满，动画版会每次播放
    // 「从顶滚到底」的长滚动，观感即"打开会话页消息从上往下滑"。
    var initialPositioned by remember { mutableStateOf(false) }
    LaunchedEffect(messages.size) {
        if (messages.isNotEmpty()) {
            if (!initialPositioned) {
                initialPositioned = true
                listState.scrollToItem(messages.size - 1)
            } else {
                // 后续新消息只在用户本就靠近底部时自动跟随；上翻读历史/加载更多时不拽走
                val info = listState.layoutInfo
                val lastVisible = info.visibleItemsInfo.lastOrNull()?.index ?: -1
                if (lastVisible >= info.totalItemsCount - 3) {
                    listState.animateScrollToItem(messages.size - 1)
                }
            }
            // 已读回执：会话页可见且滚动到底（服务端只推进 Direction=发送 行）
            messages.lastOrNull()?.let { viewModel.reportReadUpTo(it.seq) }
        }
    }
    // 键盘弹起时把最新消息顶回可视区：imePadding 压缩了列表高度，但 LazyColumn 的滚动锚点不动，
    // 不主动滚到底的话最新消息会被键盘盖住（用户视角即「内容没有向上弹起」）
    val imeVisible = WindowInsets.isImeVisible
    LaunchedEffect(imeVisible) {
        if (imeVisible && messages.isNotEmpty()) {
            listState.scrollToItem(messages.size - 1)
        }
    }

    Column(modifier = Modifier.fillMaxSize().imePadding()) {
        // 会话详情头：从会话列表进入时展示（底部导航已隐藏，返回靠此头）；null = 旧嵌入形态
        if (onBack != null) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 2.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(onClick = onBack) {
                    Icon(
                        Icons.AutoMirrored.Filled.ArrowBack,
                        contentDescription = "返回会话列表",
                        tint = MaterialTheme.colorScheme.onBackground
                    )
                }
                Text(
                    sessionTitle,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.padding(start = 4.dp)
                )
                Spacer(modifier = Modifier.weight(1f))
                // 删除会话（2026-09-21 日志删除逻辑调整）：只删会话数据与本地窗口显示，
                // 服务器消息记录保留（删除入口收敛到日志中心），确认弹窗防误触
                IconButton(onClick = { showDeleteConfirm = true }) {
                    Icon(
                        Icons.Outlined.Delete,
                        contentDescription = "删除会话",
                        tint = MaterialTheme.colorScheme.onBackground
                    )
                }
            }
        }
        // 会话内最新「带选项」消息的 seq：更早的选项消息渲染为已过期（脚本重发新列表即作废旧列表）
        // §4-7：移出 LazyColumn content lambda、以 messages 为键 remember，避免每次重组重复 filter+max
        val latestOptionsSeq = remember(messages) {
            messages
                .filter { it.direction == ChatMessageEntity.DIRECTION_FROM_BOT && it.payload?.contains("\"options\"") == true }
                .maxOfOrNull { it.seq } ?: 0L
        }
        // §4-7：消息行回调记忆化为稳定引用（写本地浮层状态/调 viewModel 方法，均不随每次重组新建）
        val openImage: (String) -> Unit = remember { { viewingImage = it } }
        val playMedia: (String, String, String?) -> Unit =
            remember { { url, title, poster -> playingMedia = Triple(url, title, poster) } }
        val saveMedia: (String) -> Unit = remember(viewModel) { { url -> viewModel.saveMedia(url) } }
        LazyColumn(
            state = listState,
            modifier = Modifier.weight(1f).fillMaxWidth(),
            contentPadding = PaddingValues(12.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            // key 用 seq（本地表主键，天然唯一）：msgId 作 key 时，同一通知若被 WS 与 REST 两条
            // 途径以不同 seq 落库就会出现重复 key，LazyColumn 会直接抛异常
            itemsIndexed(messages, key = { _, message -> message.seq }) { index, message ->
                val previous = messages.getOrNull(index - 1)
                val isNotify = message.contentType == ChatRepository.NOTIFY_CONTENT_TYPE
                // 时间分隔：与上一条间隔超过 QQ 阈值（5 分钟）才插；首条必插。
                // 通知卡片自带时间行，不再重复插分隔标签
                if (!isNotify && needsTimeLabel(previous?.createTime, message.createTime)) {
                    TimeSeparator(message.createTime)
                }
                if (isNotify) {
                    NotificationCard(
                        message = message,
                        meta = notifyMeta[message.msgId],
                        onOpenJump = onOpenJump
                    )
                } else if (message.direction == ChatMessageEntity.DIRECTION_FROM_USER && !message.pickLabel.isNullOrEmpty()) {
                    // 点选代发行（本端单端标记）：居中系统提示，不进 MessageRow（无头像无气泡）
                    PickHint(message)
                } else {
                    MessageRow(
                        message, imageLoader, viewModel,
                        latestOptionsSeq = latestOptionsSeq,
                        onOpenImage = openImage,
                        onPlayMedia = playMedia,
                        onSaveMedia = saveMedia
                    )
                }
            }
        }

        // 底部输入条（§4-6：下沉为独立组件，打字只重组输入条、不触发整页含消息列表的重组）
        ChatInputBar(
            onTogglePanel = { showPanel = !showPanel },
            onSend = { viewModel.send(it) }
        )
        if (showPanel) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(12.dp),
                horizontalArrangement = Arrangement.SpaceEvenly
            ) {
                IconButton(onClick = {
                    pickImage.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                    showPanel = false
                }) { Icon(Icons.Filled.Image, contentDescription = "相册") }
                IconButton(onClick = {
                    val file = Uploads.newCameraFile(context)
                    cameraFile = file
                    takePicture.launch(Uploads.cameraUri(context, file))
                    showPanel = false
                }) { Icon(Icons.Filled.PhotoCamera, contentDescription = "拍照") }
                IconButton(onClick = {
                    pickFile.launch("*/*")
                    showPanel = false
                }) { Icon(Icons.Filled.Description, contentDescription = "文件") }
                IconButton(onClick = {
                    showQuickCommands = true
                    showPanel = false
                }) { Text("指令") }
            }
        }
    }

    // 附件发送预览：图片可见 + 可选配文，确认后才上传发送（一条消息同时呈现图片与文字）
    pendingAttachment?.let { (kind, uri) ->
        val isImage = kind == Uploads.UploadKind.IMAGE
        AlertDialog(
            onDismissRequest = {
                pendingAttachment = null
                captionInput = ""
            },
            title = { Text(if (isImage) "发送图片" else "发送文件") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    if (isImage) {
                        AsyncImage(
                            model = uri,
                            contentDescription = "待发送图片",
                            modifier = Modifier.fillMaxWidth().heightIn(max = 240.dp),
                            contentScale = ContentScale.FillWidth
                        )
                    }
                    OutlinedTextField(
                        value = captionInput,
                        onValueChange = { captionInput = it },
                        modifier = Modifier.fillMaxWidth(),
                        placeholder = { Text("配文（可选）…") },
                        maxLines = 3
                    )
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    viewModel.uploadAndSend(
                        context, kind, uri,
                        captionInput.trim().takeIf { it.isNotEmpty() }
                    )
                    pendingAttachment = null
                    captionInput = ""
                }) { Text("发送") }
            },
            dismissButton = {
                TextButton(onClick = {
                    pendingAttachment = null
                    captionInput = ""
                }) { Text("取消") }
            }
        )
    }

    if (showQuickCommands) {
        ModalBottomSheet(onDismissRequest = { showQuickCommands = false }) {
            val commands by viewModel.quickCommands.collectAsState()
            val sheetKeyboard = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
            Column(modifier = Modifier.padding(bottom = 24.dp)) {
                commands.forEach { command ->
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clickable {
                                showQuickCommands = false
                                viewModel.send(command.key)
                                // 快捷指令发送后同样收起键盘，与回车发送行为一致
                                sheetKeyboard?.hide()
                            }
                            .padding(horizontal = 20.dp, vertical = 12.dp)
                    ) {
                        Text(command.key, style = MaterialTheme.typography.titleMedium)
                        Text(
                            command.message,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
                Text(
                    text = "多步任务进行中可发送「Q」退出",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(20.dp)
                )
            }
        }
    }

    // 删除会话确认（2026-09-21 日志删除逻辑调整）：只删会话数据与会话窗口显示，
    // 服务器消息记录保留（删除消息记录请前往日志中心），删除后返回会话列表
    if (showDeleteConfirm) {
        AlertDialog(
            onDismissRequest = { showDeleteConfirm = false },
            title = { Text("删除会话") },
            text = { Text("将删除「$sessionTitle」会话与窗口显示，消息记录保留在服务器，任务再次推送时自动重建。") },
            confirmButton = {
                TextButton(onClick = {
                    showDeleteConfirm = false
                    viewModel.deleteSession()
                    onBack?.invoke()
                }) { Text("删除", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = {
                TextButton(onClick = { showDeleteConfirm = false }) { Text("取消") }
            }
        )
    }

    // 图片消息点按后的全屏查看器（单击关闭、双击切换缩放、双指捏合缩放拖动）
    viewingImage?.let { url ->
        ImageViewerDialog(url = url, imageLoader = imageLoader, onDismiss = { viewingImage = null })
    }

    // 视频消息/可播文件点按后的全屏端内播放器（单击控制条、双击播放暂停；视频可带封面盖层）
    playingMedia?.let { (url, title, poster) ->
        MediaViewerDialog(
            url = url,
            title = title,
            playerManager = viewModel.playerManager,
            poster = poster,
            imageLoader = imageLoader,
            onDismiss = { playingMedia = null }
        )
    }
}

/**
 * 底部输入条（§4-6 从 ChatScreen 下沉为独立组件）：`input`/`inputFocused` 状态内聚于此，
 * 逐字符打字只触发本组件重组，不再连带整页（含 LazyColumn 消息流）重组。
 * 回车即发送（空输入忽略），发送成功后清空并收起软键盘（hide + clearFocus，防部分输入法回车后顶回键盘）。
 * 视觉：文字与「+」合并在同一圆角容器（filled 底 + 1dp outlineVariant 边，聚焦换 primary 描边），
 * 圆角对齐 QuantumTextField.Md；「+」按钮只负责切换附件面板（面板本体与 showPanel 仍由父级持有）。
 */
@Composable
private fun ChatInputBar(
    onTogglePanel: () -> Unit,
    onSend: (String) -> Unit
) {
    var input by remember { mutableStateOf("") }
    var inputFocused by remember { mutableStateOf(false) }
    val keyboardController = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
    val focusManager = androidx.compose.ui.platform.LocalFocusManager.current
    val submitInput = {
        if (input.isNotBlank()) {
            onSend(input.trim())
            input = ""
            focusManager.clearFocus()
            keyboardController?.hide()
        }
    }
    Row(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp),
        verticalAlignment = Alignment.Bottom
    ) {
        val scheme = MaterialTheme.colorScheme
        val barShape = RoundedCornerShape(QuantumRadius.Md)
        Row(
            modifier = Modifier
                .weight(1f)
                .clip(barShape)
                .background(scheme.surfaceContainerHigh)
                .border(1.dp, if (inputFocused) scheme.primary else scheme.outlineVariant, barShape),
            verticalAlignment = Alignment.Bottom
        ) {
            BasicTextField(
                value = input,
                onValueChange = { input = it },
                modifier = Modifier
                    .weight(1f)
                    .padding(start = QuantumSpacing.Lg, top = 10.dp, bottom = 10.dp)
                    .onFocusChanged { inputFocused = it.isFocused },
                textStyle = MaterialTheme.typography.bodyLarge.copy(color = scheme.onSurface),
                cursorBrush = SolidColor(scheme.primary),
                maxLines = 4,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                keyboardActions = KeyboardActions(onSend = { submitInput() }),
                decorationBox = { innerTextField ->
                    Box(contentAlignment = Alignment.CenterStart) {
                        if (input.isEmpty()) {
                            Text(
                                text = "输入指令或消息…",
                                style = MaterialTheme.typography.bodyLarge,
                                color = scheme.onSurfaceVariant.copy(alpha = 0.65f),
                                maxLines = 1
                            )
                        }
                        innerTextField()
                    }
                }
            )
            // QQ 行为：容器内右侧固定「+」展开附件面板；聚焦描边与按钮同处一个容器
            Box(
                modifier = Modifier
                    .padding(start = QuantumSpacing.Sm, end = QuantumSpacing.Xs, bottom = QuantumSpacing.Xs)
                    .size(QuantumSize.ControlHeightSm)
                    .clip(CircleShape)
                    .clickable { onTogglePanel() },
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = Icons.Filled.Add,
                    contentDescription = "更多",
                    tint = scheme.onSurfaceVariant,
                    modifier = Modifier.size(QuantumSize.IconMd)
                )
            }
        }
    }
}

/** 消息行：头像 + 气泡，机器人靠左、自己靠右（QQ 布局），头像与气泡顶部对齐。 */
@Composable
private fun MessageRow(
    message: ChatMessageEntity,
    imageLoader: coil.ImageLoader,
    viewModel: ChatViewModel,
    latestOptionsSeq: Long,
    onOpenImage: (String) -> Unit,
    onPlayMedia: (String, String, String?) -> Unit,
    onSaveMedia: (String) -> Unit
) {
    val fromBot = message.direction == ChatMessageEntity.DIRECTION_FROM_BOT
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = if (fromBot) Arrangement.Start else Arrangement.End,
        verticalAlignment = Alignment.Top
    ) {
        if (fromBot) {
            BotAvatar()
            Spacer(modifier = Modifier.width(8.dp))
            MessageBubble(message, imageLoader, viewModel, fromBot = true, latestOptionsSeq = latestOptionsSeq, onOpenImage = onOpenImage, onPlayMedia = onPlayMedia, onSaveMedia = onSaveMedia)
        } else {
            // 送达/失败状态属于「我发出的消息」，置于气泡外侧（QQ 语义）
            SendState(message)
            Spacer(modifier = Modifier.width(6.dp))
            MessageBubble(message, imageLoader, viewModel, fromBot = false, latestOptionsSeq = latestOptionsSeq, onOpenImage = onOpenImage, onPlayMedia = onPlayMedia, onSaveMedia = onSaveMedia)
            Spacer(modifier = Modifier.width(8.dp))
            ChatAvatar(text = "我", color = MaterialTheme.colorScheme.secondary)
        }
    }
}

/** 服务端（机器人）头像：品牌 Logo（资源在 core:common，与登录页同源）。 */
@Composable
private fun BotAvatar() {
    Image(
        painter = painterResource(id = CommonR.drawable.ic_quantum_logo),
        contentDescription = "量子助手",
        contentScale = ContentScale.Crop,
        modifier = Modifier
            .size(38.dp)
            .clip(CircleShape)
    )
}

/** 文字头像（不引入图片资源）：圆形半透明底 + 单字。 */
@Composable
private fun ChatAvatar(text: String, color: Color) {
    Box(
        modifier = Modifier
            .size(38.dp)
            .clip(CircleShape)
            .background(color.copy(alpha = 0.18f)),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text,
            style = MaterialTheme.typography.titleSmall,
            color = color,
            fontWeight = FontWeight.Bold
        )
    }
}

/**
 * 发送状态位：目前仅「发送中」可见。QQ 不在收到的消息里展示投递状态，
 * 用户侧失败重发依赖发送链路修复（乐观气泡/outbox 渲染，见改造计划 §4.9）。
 */
@Composable
private fun SendState(message: ChatMessageEntity) {
    if (message.status == ChatMessageEntity.STATUS_SENT_PENDING) {
        CircularProgressIndicator(
            modifier = Modifier.size(14.dp),
            strokeWidth = 2.dp,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/** 时间分隔标签（QQ 阈值：与上一条间隔超过 5 分钟才插）。 */
@Composable
private fun TimeSeparator(createTime: String) {
    val label = formatTimeLabel(parseServerTime(createTime)) ?: return
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
        horizontalArrangement = Arrangement.Center
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/**
 * 点选代发系统提示（QQ IM 系统消息样式）：居中小字灰、无头像无气泡。
 * pickLabel 是本端单端记忆（docs/选项已选态改造计划.md §3），其它端该行仍显示为普通用户气泡。
 */
@Composable
private fun PickHint(message: ChatMessageEntity) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            "已选择「${message.pickLabel}」",
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/**
 * 站内通知卡片（QQ 服务号形态）：分类标签 + 时间、加粗标题、正文，点按走 jump 深链。
 * 分类/标题/jump 由本地通知表按 msgId 关联（会话行只带合成正文）；关联不到时降级为
 * 直接渲染正文文本——不解析合成串、不猜结构，也不影响"通知一定出现在会话流里"。
 */
@Composable
private fun NotificationCard(
    message: ChatMessageEntity,
    meta: NotificationEntity?,
    onOpenJump: (String) -> Unit
) {
    val category = meta?.category ?: "system"
    val accent = when (category) {
        "task" -> MaterialTheme.colorScheme.primary
        "security" -> MaterialTheme.colorScheme.error
        else -> MaterialTheme.colorScheme.tertiary
    }
    val label = when (category) {
        "task" -> "任务"
        "security" -> "安全"
        else -> "系统"
    }
    val jump = meta?.jump
    val body = meta?.content ?: message.content
    Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Start) {
        Surface(
            color = MaterialTheme.colorScheme.surfaceVariant,
            shape = RoundedCornerShape(16.dp),
            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
            modifier = Modifier
                .fillMaxWidth(0.86f)
                .then(
                    if (jump.isNullOrBlank()) {
                        Modifier
                    } else {
                        Modifier.clip(RoundedCornerShape(16.dp)).clickable { onOpenJump(jump) }
                    }
                )
        ) {
            Column(modifier = Modifier.padding(12.dp)) {
                Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Box(
                        modifier = Modifier
                            .size(6.dp)
                            .clip(CircleShape)
                            .background(accent)
                    )
                    Spacer(modifier = Modifier.width(6.dp))
                    Text(
                        label,
                        style = MaterialTheme.typography.labelMedium,
                        color = accent,
                        fontWeight = FontWeight.Medium
                    )
                    Spacer(modifier = Modifier.weight(1f))
                    formatTimeLabel(parseServerTime(message.createTime))?.let {
                        Text(
                            it,
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
                // 标题与正文放在同一个 SelectionContainer 内：可跨行拖选、一次性复制整段。
                // 正文超过 3 行会被省略号截断，截断部分不可选——全文在通知中心（点按卡片跳转）
                SelectionContainer {
                    Column {
                        meta?.title?.takeIf { it.isNotBlank() }?.let {
                            Text(
                                it,
                                style = MaterialTheme.typography.titleSmall,
                                fontWeight = FontWeight.Bold,
                                modifier = Modifier.padding(top = 6.dp)
                            )
                        }
                        Text(
                            body,
                            style = MaterialTheme.typography.bodyMedium,
                            maxLines = 3,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.padding(top = 4.dp)
                        )
                    }
                }
                if (!jump.isNullOrBlank()) {
                    Text(
                        "点按查看 ›",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier
                            .padding(top = 6.dp)
                            .align(Alignment.End)
                    )
                }
            }
        }
    }
}

@Composable
private fun MessageBubble(
    message: ChatMessageEntity,
    imageLoader: coil.ImageLoader,
    viewModel: ChatViewModel,
    fromBot: Boolean,
    latestOptionsSeq: Long,
    onOpenImage: (String) -> Unit,
    onPlayMedia: (String, String, String?) -> Unit,
    onSaveMedia: (String) -> Unit
) {
    Surface(
        color = if (fromBot) MaterialTheme.colorScheme.surfaceVariant else MaterialTheme.colorScheme.primaryContainer,
        shape = RoundedCornerShape(
            topStart = 16.dp,
            topEnd = 16.dp,
            // 靠头像侧的下角收窄：QQ 气泡的"尾巴"观感
            bottomStart = if (fromBot) 4.dp else 16.dp,
            bottomEnd = if (fromBot) 16.dp else 4.dp
        ),
        modifier = Modifier.widthIn(max = 300.dp)
    ) {
        Column(modifier = Modifier.padding(horizontal = 12.dp, vertical = 9.dp)) {
            when (message.contentType) {
                // 图片/文件消息可带配文（ContentText）：一条气泡内媒体+文字，类似微信图片配字
                ChatRepository.CONTENT_TYPE_IMAGE -> {
                    // 左对齐：AsyncImage 默认 Alignment.Center，小于气泡宽度的图片会被画在
                    // 宽盒子的正中（两侧留白）；改为按内容自适应宽度 + 起始对齐，超高限 420dp
                    val imageUrl = viewModel.mediaUrl(message.content)
                    AsyncImage(
                        model = imageUrl,
                        contentDescription = "图片消息",
                        imageLoader = imageLoader,
                        alignment = Alignment.CenterStart,
                        contentScale = ContentScale.Fit,
                        modifier = Modifier
                            .heightIn(max = 420.dp)
                            .clip(RoundedCornerShape(QuantumRadius.Sm))
                            .clickable { onOpenImage(imageUrl) }
                    )
                    CaptionText(message.contentText)
                }
                ChatRepository.CONTENT_TYPE_VIDEO -> {
                    // M1：视频消息恒走端内播放（服务端已声明媒体意图，直链无扩展名也能播）；
                    // 格式不支持/链接过期由播放浮层内给重试与系统播放器兜底。
                    // 批次3 富交互：带封面（Payload.poster）时以封面卡片作播放预览，点按才加载播放。
                    // 外链视频带「保存」小按钮：看完满意一键收藏到服务器（media_saver 任务落盘）
                    val videoUrl = viewModel.mediaUrl(message.content)
                    val posterUrl = remember(message.payload) {
                        com.quantum.app.core.common.MessagePayloadParser.parse(message.payload)?.poster
                            ?.takeIf { it.isNotBlank() }
                    }?.let { viewModel.mediaUrl(it) }
                    val savable = videoUrl.startsWith("http") && !viewModel.isOwnServerUrl(videoUrl)
                    val videoTitle = message.contentText?.takeIf { it.isNotBlank() } ?: "视频消息"
                    if (posterUrl != null) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .heightIn(max = 210.dp)
                                .clip(RoundedCornerShape(QuantumRadius.Sm))
                                .clickable { onPlayMedia(videoUrl, videoTitle, posterUrl) }
                        ) {
                            AsyncImage(
                                model = posterUrl,
                                contentDescription = "视频封面",
                                imageLoader = imageLoader,
                                contentScale = ContentScale.Crop,
                                modifier = Modifier.fillMaxWidth().heightIn(max = 210.dp)
                            )
                            // 居中播放钮遮罩（黑色半透明，与播放器浮层遮罩同语言）
                            Box(
                                modifier = Modifier
                                    .align(Alignment.Center)
                                    .size(52.dp)
                                    .clip(CircleShape)
                                    .background(Color.Black.copy(alpha = 0.5f)),
                                contentAlignment = Alignment.Center
                            ) {
                                Icon(
                                    Icons.Filled.PlayArrow,
                                    contentDescription = "播放",
                                    tint = Color.White,
                                    modifier = Modifier.size(34.dp)
                                )
                            }
                            if (savable) {
                                Box(modifier = Modifier.align(Alignment.TopEnd).padding(4.dp)) {
                                    SaveMediaIcon(onSave = { onSaveMedia(videoUrl) })
                                }
                            }
                        }
                        CaptionText(message.contentText)
                    } else {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier.clickable { onPlayMedia(videoUrl, videoTitle, null) }
                        ) {
                            Icon(Icons.Filled.PlayArrow, contentDescription = null)
                            Column(modifier = Modifier.padding(start = 8.dp).weight(1f)) {
                                Text(videoTitle, style = MaterialTheme.typography.bodyMedium)
                                Text(
                                    "点按播放",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                            if (savable) {
                                SaveMediaIcon(onSave = { onSaveMedia(videoUrl) })
                            }
                        }
                    }
                }
                ChatRepository.CONTENT_TYPE_FILE -> {
                    // 可播扩展名的文件消息（如音频直链）点按直接端内播放，其余保持纯展示
                    val fileUrl = viewModel.mediaUrl(message.content)
                    val playable = MediaTypes.isPlayable(fileUrl)
                    val savable = playable && fileUrl.startsWith("http") && !viewModel.isOwnServerUrl(fileUrl)
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = if (playable) {
                            Modifier.clickable { onPlayMedia(fileUrl, message.contentText ?: "媒体播放", null) }
                        } else {
                            Modifier
                        }
                    ) {
                        Icon(Icons.Filled.Description, contentDescription = null)
                        Column(modifier = Modifier.padding(start = 8.dp).weight(1f)) {
                            SelectionContainer {
                                Text(message.content, style = MaterialTheme.typography.bodyMedium)
                            }
                            Text(
                                if (playable) "点按播放" else "点按下载查看",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        if (savable) {
                            SaveMediaIcon(onSave = { onSaveMedia(fileUrl) })
                        }
                    }
                    CaptionText(message.contentText)
                }
                ChatRepository.CONTENT_TYPE_AUDIO -> {
                    val audioUrl = viewModel.mediaUrl(message.content)
                    AudioBubble(
                        url = audioUrl,
                        title = message.contentText?.takeIf { it.isNotBlank() }
                            ?: message.content.substringBefore('?').substringAfterLast('/').ifBlank { "音频" },
                        playerManager = viewModel.playerManager,
                        onSave = if (audioUrl.startsWith("http") && !viewModel.isOwnServerUrl(audioUrl)) {
                            { onSaveMedia(audioUrl) }
                        } else {
                            null
                        }
                    )
                }
                // 正文可长按拖选复制（系统选择菜单）；富文本标记（彩色文字/胶囊标签）、
                // URL 可点链接与超长折叠在 [RichMessageText] 内处理
                else -> SelectionContainer {
                    RichMessageText(message.content, style = MaterialTheme.typography.bodyMedium)
                }
            }
            // 可点选项块（机器人消息携带 options 时渲染在内容下方；点按即等价回复指令）
            if (fromBot) {
                OptionsBlock(message = message, latestOptionsSeq = latestOptionsSeq, viewModel = viewModel)
            }
            // 机器人侧不再显示"发送中/已送达/已读"：投递状态是服务端内部语义，
            // 用户视角只关心"我发的消息有没有送到"，因此状态位只在用户侧（见 SendState）
        }
    }
}

/** 图片/文件气泡内的配文：空串不占位，长文案可长按复制；同样支持富文本标记。 */
@Composable
private fun CaptionText(contentText: String?) {
    if (contentText.isNullOrBlank()) {
        return
    }
    Spacer(modifier = Modifier.height(6.dp))
    SelectionContainer {
        RichMessageText(contentText, style = MaterialTheme.typography.bodyMedium)
    }
}

/** 媒体气泡上的「保存到服务器」小按钮：试听/看完满意一键收藏（media_saver 任务落盘并回推可播气泡）。 */
@Composable
private fun SaveMediaIcon(onSave: () -> Unit) {
    Icon(
        Icons.Outlined.Download,
        contentDescription = "保存到服务器",
        tint = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier
            .size(30.dp)
            .clip(CircleShape)
            .clickable { onSave() }
            .padding(5.dp)
    )
}

/**
 * 全屏图片查看器：双指捏合缩放与拖动、双击在原尺寸/2.5x 间切换、单击或系统返回关闭。
 * 图片与气泡内缩略图同 URL，Coil 直接命中内存缓存，打开即显。
 * 缩放回 1x 时偏移归零，避免复位后图片停在屏幕外。
 */
@Composable
private fun ImageViewerDialog(url: String, imageLoader: coil.ImageLoader, onDismiss: () -> Unit) {
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        var scale by remember { mutableStateOf(1f) }
        var offset by remember { mutableStateOf(Offset.Zero) }
        val transformState = rememberTransformableState { zoomChange, panChange, _ ->
            scale = (scale * zoomChange).coerceIn(1f, 6f)
            offset = if (scale > 1f) offset + panChange else Offset.Zero
        }
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = 0.95f))
                .transformable(transformState)
                .pointerInput(Unit) {
                    detectTapGestures(
                        onTap = { onDismiss() },
                        onDoubleTap = {
                            scale = if (scale > 1f) 1f else 2.5f
                            offset = Offset.Zero
                        }
                    )
                },
            contentAlignment = Alignment.Center
        ) {
            AsyncImage(
                model = url,
                contentDescription = "查看大图",
                imageLoader = imageLoader,
                contentScale = ContentScale.Fit,
                modifier = Modifier
                    .fillMaxSize()
                    .graphicsLayer {
                        scaleX = scale
                        scaleY = scale
                        translationX = offset.x
                        translationY = offset.y
                    }
            )
        }
    }
}

/** 服务端时间串 yyyy-MM-dd HH:mm:ss → LocalDateTime；解析失败返回 null（不显示时间，也不显示原始串）。 */
private fun parseServerTime(raw: String?): LocalDateTime? =
    raw?.takeIf { it.isNotBlank() }
        ?.let { runCatching { LocalDateTime.parse(it, SERVER_TIME_FORMATTER) }.getOrNull() }

/** QQ 时间格式：当天 HH:mm、昨天「昨天 HH:mm」、同年 MM-dd HH:mm、跨年 yyyy-MM-dd HH:mm。 */
private fun formatTimeLabel(time: LocalDateTime?): String? {
    if (time == null) {
        return null
    }
    val today = LocalDate.now(TimeUtil.SERVER_ZONE)
    val date = time.toLocalDate()
    return when {
        date == today -> time.format(HH_MM)
        date == today.minusDays(1) -> "昨天 ${time.format(HH_MM)}"
        date.year == today.year -> time.format(MM_DD_HH_MM)
        else -> time.format(YYYY_MM_DD_HH_MM)
    }
}

/** 是否需要插时间分隔：首条（无上一条）或与上一条间隔超过 QQ 阈值 5 分钟。 */
private fun needsTimeLabel(previousRaw: String?, currentRaw: String): Boolean {
    val current = parseServerTime(currentRaw) ?: return false
    val previous = parseServerTime(previousRaw) ?: return true
    return java.time.Duration.between(previous, current).toMinutes() > TIME_LABEL_GAP_MINUTES
}

/** 时间分隔阈值（分钟）：与 QQ 一致，超过才插分隔标签。 */
private const val TIME_LABEL_GAP_MINUTES = 5L

private val SERVER_TIME_FORMATTER: DateTimeFormatter = DateTimeFormatter.ofPattern(TimeUtil.SERVER_PATTERN)
private val HH_MM: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm")
private val MM_DD_HH_MM: DateTimeFormatter = DateTimeFormatter.ofPattern("MM-dd HH:mm")
private val YYYY_MM_DD_HH_MM: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm")
