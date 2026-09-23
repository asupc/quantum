package com.quantum.app.feature.ai

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.common.ui.components.QuantumBackHeader
import com.quantum.app.core.common.ui.components.QuantumButton
import com.quantum.app.core.common.ui.components.QuantumButtonSize
import com.quantum.app.core.common.ui.components.QuantumButtonVariant
import com.quantum.app.core.common.ui.components.QuantumIconAction
import com.quantum.app.core.common.ui.theme.QuantumPage
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.AiProposalContentDto
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** 提案差异页 ViewModel：拉基线+候选全文，diff 在端本地算（见 AiLineDiff）。 */
@HiltViewModel
class AiDiffViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    data class UiState(
        val loading: Boolean = true,
        val content: AiProposalContentDto? = null,
        val error: String? = null
    )

    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state.asStateFlow()

    fun start(proposalId: String) {
        viewModelScope.launch {
            runCatching { adminApi.aiProposalContent(proposalId).unwrap() }
                .onSuccess { content -> _state.update { it.copy(loading = false, content = content) } }
                .onFailure { e ->
                    _state.update {
                        it.copy(loading = false, error = (e as? ApiException)?.message ?: e.message ?: "加载失败")
                    }
                }
        }
    }
}

/** 渲染上限（对齐 Web：AI 页 DIFF_MAX_ROWS 与脚本版本页 MaxDiffRows 均 3000，超出提示截断）。 */
private const val DIFF_MAX_ROWS = 3000

/** 提案差异查看：双侧行号 + 符号列 + Monospace；add 绿底 / del 玫红底；复制候选全文。 */
@Composable
fun AiDiffScreen(
    proposalId: String,
    onBack: () -> Unit,
    viewModel: AiDiffViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsState()
    LaunchedEffect(proposalId) { viewModel.start(proposalId) }

    val content = state.content
    val diff = remember(content) {
        AiLineDiff.diffLines(content?.baseContent.orEmpty(), content?.newContent.orEmpty())
    }
    val capped = remember(diff) { diff.rows.take(DIFF_MAX_ROWS) }
    val clipboard = LocalClipboardManager.current

    Column(modifier = Modifier.fillMaxSize()) {
        QuantumBackHeader(
            title = "差异 · ${content?.fileName ?: "提案"}",
            onBack = onBack,
            subtitle = content?.summary,
            actions = {
                QuantumIconAction(
                    icon = Icons.Filled.ContentCopy,
                    contentDescription = "复制候选全文",
                    onClick = { content?.newContent?.let { clipboard.setText(AnnotatedString(it)) } }
                )
            }
        )

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = QuantumPage.Padding, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                "+${diff.added}",
                style = MaterialTheme.typography.labelMedium,
                color = Emerald,
                modifier = Modifier
                    .clip(RoundedCornerShape(50))
                    .background(Emerald.copy(alpha = 0.12f))
                    .padding(horizontal = 8.dp, vertical = 2.dp)
            )
            Spacer(modifier = Modifier.width(8.dp))
            Text(
                "-${diff.removed}",
                style = MaterialTheme.typography.labelMedium,
                color = Rose,
                modifier = Modifier
                    .clip(RoundedCornerShape(50))
                    .background(Rose.copy(alpha = 0.12f))
                    .padding(horizontal = 8.dp, vertical = 2.dp)
            )
            Spacer(modifier = Modifier.width(8.dp))
            Text(
                "共 ${diff.rows.size} 行",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        if (diff.truncated) {
            Text(
                "差异过大，已按整块替换展示",
                style = MaterialTheme.typography.labelSmall,
                color = Rose,
                modifier = Modifier.padding(horizontal = QuantumPage.Padding)
            )
        }
        if (diff.rows.size > DIFF_MAX_ROWS) {
            Text(
                "仅展示前 $DIFF_MAX_ROWS 行差异（共 ${diff.rows.size} 行），完整内容请复制候选全文查看",
                style = MaterialTheme.typography.labelSmall,
                color = Rose,
                modifier = Modifier.padding(horizontal = QuantumPage.Padding)
            )
        }

        when {
            state.loading -> Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(
                    modifier = Modifier.padding(24.dp).height(22.dp),
                    strokeWidth = 2.dp,
                    color = MaterialTheme.colorScheme.primary
                )
            }
            state.error != null -> Column(
                modifier = Modifier.fillMaxSize().padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text(state.error ?: "加载失败", color = MaterialTheme.colorScheme.error)
                Spacer(modifier = Modifier.height(12.dp))
                QuantumButton(
                    text = "重试",
                    onClick = { viewModel.start(proposalId) },
                    variant = QuantumButtonVariant.Tonal,
                    size = QuantumButtonSize.Sm
                )
            }
            else -> {
                val hScroll = rememberScrollState()
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(
                        start = QuantumPage.Padding,
                        end = QuantumPage.Padding,
                        bottom = 12.dp
                    )
                ) {
                    items(capped) { row ->
                        val bg = when (row.kind) {
                            AiLineDiff.RowKind.Add -> Emerald.copy(alpha = 0.10f)
                            AiLineDiff.RowKind.Del -> Rose.copy(alpha = 0.10f)
                            AiLineDiff.RowKind.Same -> androidx.compose.ui.graphics.Color.Transparent
                        }
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .background(bg)
                                .horizontalScroll(hScroll)
                                .padding(vertical = 1.dp)
                        ) {
                            Text(
                                row.oldLine?.toString() ?: "",
                                style = MaterialTheme.typography.labelSmall.copy(fontFamily = FontFamily.Monospace),
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.width(44.dp)
                            )
                            Text(
                                row.newLine?.toString() ?: "",
                                style = MaterialTheme.typography.labelSmall.copy(fontFamily = FontFamily.Monospace),
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.width(44.dp)
                            )
                            Text(
                                when (row.kind) {
                                    AiLineDiff.RowKind.Add -> "+"
                                    AiLineDiff.RowKind.Del -> "-"
                                    AiLineDiff.RowKind.Same -> " "
                                },
                                style = MaterialTheme.typography.labelSmall.copy(fontFamily = FontFamily.Monospace),
                                color = when (row.kind) {
                                    AiLineDiff.RowKind.Add -> Emerald
                                    AiLineDiff.RowKind.Del -> Rose
                                    AiLineDiff.RowKind.Same -> MaterialTheme.colorScheme.onSurfaceVariant
                                },
                                modifier = Modifier.width(14.dp)
                            )
                            Text(
                                row.text,
                                style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                                color = MaterialTheme.colorScheme.onSurface
                            )
                        }
                    }
                }
            }
        }
    }
}
