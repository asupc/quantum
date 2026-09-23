package com.quantum.app.feature.ai

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.hilt.navigation.compose.hiltViewModel
import com.quantum.app.core.common.ui.components.ConfirmDialog
import com.quantum.app.core.common.ui.components.QuantumBackHeader
import com.quantum.app.core.common.ui.components.QuantumButton
import com.quantum.app.core.common.ui.components.QuantumButtonSize
import com.quantum.app.core.common.ui.components.QuantumButtonVariant
import com.quantum.app.core.common.ui.components.QuantumDialog
import com.quantum.app.core.common.ui.components.QuantumIconAction
import com.quantum.app.core.common.ui.components.QuantumListRow
import com.quantum.app.core.common.ui.components.QuantumSwitchRow
import com.quantum.app.core.common.ui.components.QuantumTextField
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.common.ui.theme.QuantumSpacing
import com.quantum.app.core.network.dto.AiMessageDto
import com.quantum.app.core.network.dto.AiProposalPayload
import com.quantum.app.core.network.dto.AiRunDto
import com.quantum.app.core.network.dto.AiTestResultPayload

/**
 * AI 对话主页：消息流（用户/助手/状态行/提案卡/试运行卡）+ 运行状态条 + 过程面板 + 输入区。
 * 移动端 Enter=换行，发送只走按钮；run 进行中禁发（先停止或等结束）。
 */
@Composable
fun AiChatScreen(
    conversationId: String,
    onBack: () -> Unit,
    onOpenDiff: (String) -> Unit,
    onOpenLog: (String) -> Unit,
    viewModel: AiChatViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsState()

    LaunchedEffect(conversationId) { viewModel.start(conversationId) }
    // 操作反馈限时展示（与 AdminScreens 同款）
    LaunchedEffect(state.toast) {
        if (state.toast != null) {
            kotlinx.coroutines.delay(4_000)
            viewModel.consumeToast()
        }
    }

    var input by remember { mutableStateOf("") }
    var targetFile by remember { mutableStateOf<String?>(null) }
    var showSettings by remember { mutableStateOf(false) }
    var showScriptPicker by remember { mutableStateOf(false) }
    var applyConfirmId by remember { mutableStateOf<String?>(null) }
    var discardConfirmId by remember { mutableStateOf<String?>(null) }

    Column(modifier = Modifier.fillMaxSize().imePadding()) {
        QuantumBackHeader(
            title = state.conversation?.title?.takeIf { it.isNotBlank() } ?: "AI 对话",
            onBack = onBack,
            subtitle = AiPayloadParser.defaultModelSubtitle(state.defaultModel),
            actions = {
                QuantumIconAction(
                    icon = Icons.Outlined.Description,
                    contentDescription = "平台能力",
                    onClick = { viewModel.openContract() }
                )
                Spacer(modifier = Modifier.width(6.dp))
                QuantumIconAction(
                    icon = Icons.Outlined.Settings,
                    contentDescription = "会话设置",
                    onClick = { showSettings = true }
                )
            }
        )

        state.toast?.let {
            Text(
                it,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(horizontal = QuantumPage.Padding, vertical = QuantumSpacing.Xs)
            )
        }

        RunStatusBar(run = state.run, onCancel = { viewModel.cancelRun() })

        MessageList(
            messages = state.messages,
            loading = state.loading,
            testingProposalId = state.testingProposalId,
            stepsByRun = state.stepsByRun,
            run = state.run,
            liveSteps = state.liveSteps,
            liveBlockTaken = state.liveBlockTaken,
            onToggleSteps = { viewModel.toggleRunSteps(it) },
            onTestRun = { viewModel.testRun(it) },
            onApply = { applyConfirmId = it },
            onDiscard = { discardConfirmId = it },
            onOpenDiff = onOpenDiff,
            onOpenLog = onOpenLog,
            modifier = Modifier.weight(1f)
        )

        InputArea(
            value = input,
            onValueChange = { input = it },
            targetFile = targetFile,
            onPickTarget = { showScriptPicker = true },
            onClearTarget = { targetFile = null },
            sending = state.sending,
            runActive = state.runActive,
            onSend = {
                viewModel.send(input, targetFile)
                input = ""
            }
        )
    }

    // ---- 弹层组 ----

    if (showSettings) {
        QuantumDialog(
            title = "会话设置",
            onDismiss = { showSettings = false },
            body = {
                Column {
                    QuantumSwitchRow(
                        title = "允许读取环境变量值",
                        subtitle = "开启后 AI 可在本会话读取环境变量的实际值（默认仅名称）",
                        checked = state.conversation?.allowEnvValues == true,
                        onCheckedChange = { viewModel.setAllowEnvValues(it) }
                    )
                }
            },
            confirmText = "关闭",
            onConfirm = { showSettings = false }
        )
    }

    state.contractText?.let { contract ->
        val clipboard = LocalClipboardManager.current
        QuantumDialog(
            title = "平台能力契约",
            onDismiss = { viewModel.closeContract() },
            body = {
                Box(
                    modifier = Modifier
                        .heightIn(max = 420.dp)
                        .verticalScroll(rememberScrollState())
                        .horizontalScroll(rememberScrollState())
                ) {
                    SelectionContainer {
                        Text(
                            contract,
                            style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            },
            confirmText = "复制全文",
            onConfirm = { clipboard.setText(AnnotatedString(contract)) },
            dismissText = "关闭"
        )
    }

    if (showScriptPicker) {
        QuantumDialog(
            title = "选择目标脚本",
            onDismiss = { showScriptPicker = false },
            body = {
                Column(
                    modifier = Modifier
                        .heightIn(max = 440.dp)
                        .verticalScroll(rememberScrollState())
                ) {
                    if (state.scriptOptions.isEmpty()) {
                        Text(
                            "未获取到脚本清单（可留空由 AI 自行判断）",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    } else {
                        state.scriptOptions.forEach { path ->
                            QuantumListRow(
                                title = path.substringAfterLast('/'),
                                subtitle = path,
                                showChevron = false,
                                onClick = {
                                    targetFile = path
                                    showScriptPicker = false
                                }
                            )
                        }
                    }
                }
            },
            confirmText = "留空",
            onConfirm = {
                targetFile = null
                showScriptPicker = false
            },
            dismissText = "关闭"
        )
    }

    applyConfirmId?.let { proposalId ->
        ConfirmDialog(
            title = "应用修复提案",
            content = "将把提案内容写入脚本并记入版本历史（可回滚）。应用会重新过安全门禁与编译检查。",
            confirmText = "确认应用",
            onConfirm = { viewModel.applyProposal(proposalId) },
            onDismiss = { applyConfirmId = null }
        )
    }

    discardConfirmId?.let { proposalId ->
        ConfirmDialog(
            title = "忽略修复提案",
            content = "忽略后该提案不再可用（脚本不受影响）；需要时请让 AI 重新生成。",
            onConfirm = { viewModel.discardProposal(proposalId) },
            onDismiss = { discardConfirmId = null }
        )
    }

    state.applyDiagnostics?.let { diag ->
        QuantumDialog(
            title = "应用未通过（${diag.blocked.size + diag.errors.size} 条诊断）",
            onDismiss = { viewModel.closeDiagnostics() },
            body = {
                Column(
                    modifier = Modifier
                        .heightIn(max = 420.dp)
                        .verticalScroll(rememberScrollState())
                ) {
                    DiagnosticsSection("门禁拦截", diag.blocked, Rose)
                    DiagnosticsSection("编译错误", diag.errors, Rose)
                    DiagnosticsSection("警告", diag.warnings, Amber)
                }
            },
            confirmText = "知道了",
            onConfirm = { viewModel.closeDiagnostics() }
        )
    }
}

// ---- 消息流 ----

@Composable
private fun MessageList(
    messages: List<AiChatViewModel.MessageUi>,
    loading: Boolean,
    testingProposalId: String?,
    stepsByRun: Map<String, AiChatViewModel.RunStepsUi>,
    run: AiRunDto?,
    liveSteps: AiChatViewModel.RunStepsUi?,
    liveBlockTaken: Boolean,
    onToggleSteps: (String) -> Unit,
    onTestRun: (String) -> Unit,
    onApply: (String) -> Unit,
    onDiscard: (String) -> Unit,
    onOpenDiff: (String) -> Unit,
    onOpenLog: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    val listState = rememberLazyListState()
    // 用户在底部附近才跟随滚动（上翻回看不打扰），对齐会话页惯例
    val nearBottom by remember {
        derivedStateOf {
            val last = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            listState.layoutInfo.totalItemsCount > 0 && last >= listState.layoutInfo.totalItemsCount - 3
        }
    }
    LaunchedEffect(messages.size) {
        if (messages.isNotEmpty() && nearBottom) {
            listState.animateScrollToItem(listState.layoutInfo.totalItemsCount - 1)
        }
    }
    // 首屏直接落底（不等动画）
    LaunchedEffect(loading, messages.isNotEmpty()) {
        if (!loading && messages.isNotEmpty() && listState.layoutInfo.totalItemsCount > 0) {
            listState.scrollToItem(listState.layoutInfo.totalItemsCount - 1)
        }
    }

    if (loading) {
        Box(modifier = modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator(
                modifier = Modifier.size(22.dp),
                strokeWidth = 2.dp,
                color = MaterialTheme.colorScheme.primary
            )
        }
        return
    }

    LazyColumn(
        state = listState,
        modifier = modifier.fillMaxWidth(),
        contentPadding = PaddingValues(horizontal = QuantumPage.Padding, vertical = 8.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        items(messages, key = { "${it.dto.seq}-${it.dto.id ?: "local"}" }) { message ->
            // 执行过程块：挂在带 runId 的 assistant 消息（结论文案）上方，默认折叠、首展懒加载
            val runId = message.dto.runId
            val entry = runId?.let { stepsByRun[it] }
            if (runId != null && message.dto.role == AiPayloadParser.ROLE_ASSISTANT && entry != null) {
                InlineStepsCard(
                    entry = entry,
                    running = runId == run?.id && run?.status == AiPayloadParser.RUN_RUNNING,
                    progress = null,
                    onToggle = { onToggleSteps(runId) }
                )
            }
            MessageItem(
                message = message,
                testing = testingProposalId,
                onTestRun = onTestRun,
                onApply = onApply,
                onDiscard = onDiscard,
                onOpenDiff = onOpenDiff,
                onOpenLog = onOpenLog
            )
        }
        // 运行中的实时过程块：消息流末尾常驻（结论消息落库后由同 runId 的消息级块接管）
        val liveRunId = run?.id
        if (liveSteps != null && !liveBlockTaken && liveRunId != null) {
            item(key = "live-steps-$liveRunId") {
                InlineStepsCard(
                    entry = liveSteps,
                    running = true,
                    progress = run?.progress,
                    onToggle = { onToggleSteps(liveRunId) }
                )
            }
        }
    }
}

@Composable
private fun MessageItem(
    message: AiChatViewModel.MessageUi,
    testing: String?,
    onTestRun: (String) -> Unit,
    onApply: (String) -> Unit,
    onDiscard: (String) -> Unit,
    onOpenDiff: (String) -> Unit,
    onOpenLog: (String) -> Unit
) {
    val dto = message.dto
    when {
        // Role=status：居中灰字状态行（不进气泡）
        dto.role == AiPayloadParser.ROLE_STATUS -> Box(modifier = Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
            Text(
                dto.content.orEmpty(),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        dto.kind == AiPayloadParser.KIND_PROPOSAL && message.proposal != null ->
            ProposalCard(
                payload = message.proposal,
                testing = testing == message.proposal.id,
                onTestRun = onTestRun,
                onApply = onApply,
                onDiscard = onDiscard,
                onOpenDiff = onOpenDiff
            )
        dto.kind == AiPayloadParser.KIND_TEST_RESULT ->
            TestResultCard(payload = message.testResult, fallbackText = dto.content.orEmpty(), onOpenLog = onOpenLog)
        dto.role == AiPayloadParser.ROLE_USER -> Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
            SelectionContainer {
                Text(
                    dto.content.orEmpty(),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier
                        .clip(RoundedCornerShape(topStart = 16.dp, topEnd = 16.dp, bottomStart = 16.dp, bottomEnd = 4.dp))
                        .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.10f))
                        .padding(horizontal = 12.dp, vertical = 9.dp)
                )
            }
        }
        dto.kind == AiPayloadParser.KIND_ERROR -> MessageBubble(
            content = dto.content.orEmpty(),
            error = true
        )
        else -> MessageBubble(content = dto.content.orEmpty(), error = false)
    }
}

/** 助手气泡：正文走 Markdown-lite（代码块/行内代码/URL 链接/长文折叠）；error 走玫红边框。 */
@Composable
private fun MessageBubble(content: String, error: Boolean) {
    val shape = RoundedCornerShape(topStart = 4.dp, topEnd = 16.dp, bottomStart = 16.dp, bottomEnd = 16.dp)
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(MaterialTheme.colorScheme.surfaceContainer)
            .then(
                if (error) {
                    Modifier.border(1.dp, Rose.copy(alpha = 0.5f), shape)
                } else {
                    Modifier
                }
            )
            .padding(horizontal = 12.dp, vertical = 9.dp)
    ) {
        if (error) {
            Text(
                "运行失败",
                style = MaterialTheme.typography.labelSmall,
                color = Rose,
                modifier = Modifier.padding(bottom = 4.dp)
            )
        }
        SelectionContainer {
            AiMarkdownText(content)
        }
    }
}

// ---- Markdown-lite 渲染 ----

@Composable
private fun AiMarkdownText(text: String, modifier: Modifier = Modifier) {
    val blocks = remember(text) { AiMarkdown.parse(text) }
    val totalLines = remember(blocks) { AiMarkdown.estimateLines(blocks) }
    var expanded by remember(text) { mutableStateOf(totalLines <= AiMarkdown.FOLD_LINE_LIMIT) }
    val shown = if (expanded) blocks else truncateBlocks(blocks)

    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        shown.forEach { block ->
            when (block) {
                is AiMarkdown.Block.Code -> CodeBlock(language = block.language, text = block.text)
                is AiMarkdown.Block.Paragraph -> ParagraphText(block)
            }
        }
        if (!expanded && blocks.size > shown.size) {
            TextButton(onClick = { expanded = true }, modifier = Modifier.height(30.dp)) {
                Text("展开全文（约 $totalLines 行）")
            }
        }
    }
}

private fun truncateBlocks(blocks: List<AiMarkdown.Block>): List<AiMarkdown.Block> {
    var acc = 0
    val out = ArrayList<AiMarkdown.Block>()
    for (block in blocks) {
        val lines = AiMarkdown.estimateLines(listOf(block))
        if (acc + lines > AiMarkdown.FOLD_LINE_LIMIT) break
        out += block
        acc += lines
    }
    return out
}

@Composable
private fun CodeBlock(language: String, text: String) {
    val clipboard = LocalClipboardManager.current
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(MaterialTheme.colorScheme.surfaceContainerHighest)
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(8.dp))
            .padding(10.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Text(
                language.ifBlank { "code" },
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f)
            )
            TextButton(
                onClick = { clipboard.setText(AnnotatedString(text)) },
                contentPadding = PaddingValues(horizontal = 4.dp, vertical = 0.dp),
                modifier = Modifier.height(26.dp)
            ) {
                Text("复制", style = MaterialTheme.typography.labelSmall)
            }
        }
        Text(
            text,
            style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.horizontalScroll(rememberScrollState())
        )
    }
}

@Composable
private fun ParagraphText(paragraph: AiMarkdown.Block.Paragraph) {
    val uriHandler = LocalUriHandler.current
    val linkColor = MaterialTheme.colorScheme.primary
    val annotated = buildAnnotatedString {
        paragraph.spans.forEach { span ->
            when (span) {
                is AiMarkdown.Span.Plain -> append(span.text)
                is AiMarkdown.Span.InlineCode -> withStyle(
                    SpanStyle(
                        fontFamily = FontFamily.Monospace,
                        background = MaterialTheme.colorScheme.surfaceContainerHighest
                    )
                ) { append(span.text) }
                is AiMarkdown.Span.Url -> withLink(
                    LinkAnnotation.Url(
                        span.url,
                        TextLinkStyles(SpanStyle(color = linkColor, textDecoration = TextDecoration.Underline))
                    ) { uriHandler.openUri(span.url) }
                ) { append(span.text) }
                AiMarkdown.Span.NewLine -> append("\n")
            }
        }
    }
    Text(annotated, style = MaterialTheme.typography.bodyMedium)
}

// ---- 提案卡 / 试运行卡 ----

@Composable
private fun ProposalCard(
    payload: AiProposalPayload,
    testing: Boolean,
    onTestRun: (String) -> Unit,
    onApply: (String) -> Unit,
    onDiscard: (String) -> Unit,
    onOpenDiff: (String) -> Unit
) {
    val status = payload.status
    val actionable = AiPayloadParser.proposalActionable(status)
    val diagnostics = remember(payload.diagnostics) { AiPayloadParser.parseDiagnostics(payload.diagnostics) }
    val diagCount = (diagnostics?.blocked?.size ?: 0) + (diagnostics?.errors?.size ?: 0)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(MaterialTheme.colorScheme.surfaceContainer)
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(12.dp))
            .padding(12.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Text(
                "脚本修复提案 · ${payload.fileName ?: "未知脚本"}",
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.weight(1f)
            )
            StatusPill(
                text = AiPayloadParser.proposalStatusLabel(status),
                color = when (status) {
                    "Pending" -> Amber
                    "Applied" -> Emerald
                    "Stale" -> Rose
                    else -> MaterialTheme.colorScheme.onSurfaceVariant
                }
            )
        }

        payload.summary?.takeIf { it.isNotBlank() }?.let {
            Spacer(modifier = Modifier.height(6.dp))
            SelectionContainer {
                Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }

        Spacer(modifier = Modifier.height(8.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            StatusPill(
                text = AiPayloadParser.testStatusLabel(payload.testStatus),
                color = when (payload.testStatus) {
                    "Passed" -> Emerald
                    "Failed" -> Rose
                    "Running", "Timeout" -> Amber
                    else -> MaterialTheme.colorScheme.onSurfaceVariant
                }
            )
            if (diagCount > 0) {
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    "生成时门禁/编译诊断 $diagCount 条",
                    style = MaterialTheme.typography.labelSmall,
                    color = Rose
                )
            }
        }

        Spacer(modifier = Modifier.height(10.dp))
        when {
            actionable -> Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                QuantumButton(
                    text = "试运行",
                    onClick = { payload.id?.let(onTestRun) },
                    variant = QuantumButtonVariant.Tonal,
                    size = QuantumButtonSize.Sm,
                    loading = testing,
                    enabled = !testing
                )
                QuantumButton(
                    text = "应用",
                    onClick = { payload.id?.let(onApply) },
                    variant = QuantumButtonVariant.Primary,
                    size = QuantumButtonSize.Sm
                )
                QuantumButton(
                    text = "忽略",
                    onClick = { payload.id?.let(onDiscard) },
                    variant = QuantumButtonVariant.Ghost,
                    size = QuantumButtonSize.Sm
                )
                QuantumButton(
                    text = "查看差异",
                    onClick = { payload.id?.let(onOpenDiff) },
                    variant = QuantumButtonVariant.Outlined,
                    size = QuantumButtonSize.Sm
                )
            }
            status == "Applied" -> Text(
                "已应用 · 版本 ${AiPayloadParser.versionShort(payload.appliedVersionId) ?: "-"}",
                style = MaterialTheme.typography.labelMedium,
                color = Emerald
            )
            status == "Stale" -> Text(
                "脚本已被改动，提案已过期——请让 AI 基于最新脚本重新生成",
                style = MaterialTheme.typography.labelMedium,
                color = Rose
            )
            // Discarded：无按钮无提示
        }
    }
}

@Composable
private fun TestResultCard(payload: AiTestResultPayload?, fallbackText: String, onOpenLog: (String) -> Unit) {
    var logExpanded by remember { mutableStateOf(false) }
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(MaterialTheme.colorScheme.surfaceContainer)
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(12.dp))
            .padding(12.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            StatusPill(
                text = AiPayloadParser.testStatusLabel(payload?.status),
                color = when (payload?.status) {
                    "Passed" -> Emerald
                    "Failed" -> Rose
                    "Running", "Timeout" -> Amber
                    else -> MaterialTheme.colorScheme.onSurfaceVariant
                }
            )
            Spacer(modifier = Modifier.width(8.dp))
            Text(
                payload?.message ?: fallbackText,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.weight(1f)
            )
            if (payload != null && payload.durationMs > 0) {
                Text(
                    "${payload.durationMs / 1000.0}s",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }

        payload?.logTail?.takeIf { it.isNotBlank() }?.let { tail ->
            Spacer(modifier = Modifier.height(8.dp))
            Text(
                tail,
                style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = if (logExpanded) Int.MAX_VALUE else 6,
                overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(8.dp))
                    .background(MaterialTheme.colorScheme.surfaceContainerHighest)
                    .padding(8.dp)
            )
            TextButton(onClick = { logExpanded = !logExpanded }, modifier = Modifier.height(28.dp)) {
                Text(if (logExpanded) "收起日志" else "展开日志", style = MaterialTheme.typography.labelSmall)
            }
        }

        payload?.logId?.takeIf { it.isNotBlank() }?.let { logId ->
            QuantumButton(
                text = "查看完整日志",
                onClick = { onOpenLog(logId) },
                variant = QuantumButtonVariant.Ghost,
                size = QuantumButtonSize.Sm
            )
        }
    }
}

// ---- 运行状态条 / 过程面板 / 输入区 ----

@Composable
private fun RunStatusBar(run: AiRunDto?, onCancel: () -> Unit) {
    // 仅 Running 期间显示；WaitingConfirm/终态由消息流（提案卡/结论/状态行）表达
    if (run?.status != AiPayloadParser.RUN_RUNNING) return
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = QuantumPage.Padding, vertical = 4.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.08f))
            .padding(horizontal = 10.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        CircularProgressIndicator(
            modifier = Modifier.size(14.dp),
            strokeWidth = 2.dp,
            color = MaterialTheme.colorScheme.primary
        )
        Spacer(modifier = Modifier.width(8.dp))
        Text(
            "${run.progress?.takeIf { it.isNotBlank() } ?: "处理中"} · 第 ${run.rounds} 轮",
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.primary,
            modifier = Modifier.weight(1f)
        )
        QuantumButton(
            text = "停止",
            onClick = onCancel,
            variant = QuantumButtonVariant.Ghost,
            size = QuantumButtonSize.Sm
        )
    }
}

/**
 * 会话流内联执行过程块（取代原底部 StepsPanel）：可折叠卡片，行=工具名/耗时/单行摘要。
 * 运行中（running=true）头带 spinner 与当前动作，结束转折叠态钉在结论消息上方；纯文本渲染不引 HTML。
 */
@Composable
private fun InlineStepsCard(
    entry: AiChatViewModel.RunStepsUi,
    running: Boolean,
    progress: String?,
    onToggle: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(MaterialTheme.colorScheme.surfaceContainer.copy(alpha = 0.72f))
            .border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(12.dp))
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable(onClick = onToggle)
                .padding(horizontal = 10.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            if (running) {
                CircularProgressIndicator(
                    modifier = Modifier.size(13.dp),
                    strokeWidth = 2.dp,
                    color = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(8.dp))
            }
            Text(
                buildString {
                    append(if (running) "执行中" else "执行过程")
                    append("（${entry.steps.size} 步）")
                    if (running && !progress.isNullOrBlank()) append(" · $progress")
                },
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.primary.takeIf { running } ?: MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f)
            )
            if (!entry.loaded && !running) {
                Text(
                    "展开加载",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(modifier = Modifier.width(6.dp))
            }
            Icon(
                Icons.AutoMirrored.Filled.KeyboardArrowRight,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier
                    .size(18.dp)
                    .let { if (entry.expanded) it.rotateDown() else it }
            )
        }
        if (entry.expanded) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 300.dp)
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 10.dp, vertical = 2.dp)
            ) {
                if (entry.steps.isEmpty()) {
                    Text(
                        if (running) "正在等待第一个动作…" else "暂无过程记录",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(vertical = 6.dp)
                    )
                }
                entry.steps.forEach { step ->
                    Row(modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp), verticalAlignment = Alignment.Top) {
                        Text(
                            step.name ?: step.kind ?: "-",
                            style = MaterialTheme.typography.labelSmall.copy(fontFamily = FontFamily.Monospace),
                            color = MaterialTheme.colorScheme.primary,
                            modifier = Modifier.width(110.dp)
                        )
                        Text(
                            "${step.durationMs}ms",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.width(52.dp)
                        )
                        Text(
                            (step.result ?: step.arguments ?: "").take(80),
                            style = MaterialTheme.typography.labelSmall.copy(fontFamily = FontFamily.Monospace),
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 2,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f)
                        )
                    }
                }
                if (entry.steps.size >= 500) {
                    Text(
                        "已达单次运行步骤上限（500），超出部分已截断",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(vertical = 4.dp)
                    )
                }
            }
        }
    }
}

/** 展开箭头旋转 90° 指向下：只影响绘制不影响测量。 */
private fun Modifier.rotateDown(): Modifier = this.rotate(90f)

@Composable
private fun InputArea(
    value: String,
    onValueChange: (String) -> Unit,
    targetFile: String?,
    onPickTarget: () -> Unit,
    onClearTarget: () -> Unit,
    sending: Boolean,
    runActive: Boolean,
    onSend: () -> Unit
) {
    Column(modifier = Modifier.fillMaxWidth().padding(horizontal = QuantumPage.Padding, vertical = 8.dp)) {
        targetFile?.let { file ->
            Row(
                modifier = Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .background(MaterialTheme.colorScheme.surfaceContainerHigh)
                    .padding(horizontal = 8.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    "目标脚本：$file",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f)
                )
                Icon(
                    Icons.Filled.Close,
                    contentDescription = "清除目标脚本",
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier
                        .size(16.dp)
                        .clickable(
                            interactionSource = remember { MutableInteractionSource() },
                            indication = null
                        ) { onClearTarget() }
                )
            }
            Spacer(modifier = Modifier.height(6.dp))
        }
        Row(verticalAlignment = Alignment.Bottom) {
            QuantumIconAction(
                icon = Icons.Outlined.Description,
                contentDescription = "选择目标脚本",
                onClick = onPickTarget
            )
            Spacer(modifier = Modifier.width(8.dp))
            QuantumTextField(
                value = value,
                onValueChange = onValueChange,
                modifier = Modifier.weight(1f),
                placeholder = "描述要修复/编写的脚本需求…",
                singleLine = false,
                maxLines = 5,
                // 移动端 Enter=换行，发送只走按钮（防误触）
                imeAction = androidx.compose.ui.text.input.ImeAction.Default
            )
            Spacer(modifier = Modifier.width(8.dp))
            QuantumButton(
                text = "发送",
                onClick = onSend,
                size = QuantumButtonSize.Md,
                enabled = value.isNotBlank() && !sending && !runActive,
                loading = sending
            )
        }
    }
}

// ---- 通用小组件 ----

/** 彩色状态胶囊（QuantumStatusBadge 只有 active/inactive 两态，这里需要琥珀/玫红等语义色）。 */
@Composable
private fun StatusPill(text: String, color: Color) {
    Text(
        text,
        style = MaterialTheme.typography.labelSmall,
        color = color,
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(color.copy(alpha = 0.12f))
            .border(1.dp, color.copy(alpha = 0.35f), RoundedCornerShape(50))
            .padding(horizontal = 8.dp, vertical = 2.dp)
    )
}

@Composable
private fun DiagnosticsSection(title: String, issues: List<com.quantum.app.core.network.dto.ScriptIssueDto>, color: Color) {
    if (issues.isEmpty()) return
    Text(
        "$title（${issues.size}）",
        style = MaterialTheme.typography.labelMedium,
        color = color,
        modifier = Modifier.padding(top = 8.dp, bottom = 4.dp)
    )
    issues.forEach { issue ->
        Text(
            buildString {
                if (issue.line > 0) append("L${issue.line} ")
                issue.code?.let { append("[$it] ") }
                append(issue.message)
            },
            style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(bottom = 2.dp)
        )
    }
}
