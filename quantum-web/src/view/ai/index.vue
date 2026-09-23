<template>
    <div class="ai-page">
        <!-- 左：会话列表 -->
        <aside class="ai-side">
            <div class="side-head">
                <div class="side-title">
                    <h3>AI 会话</h3>
                    <span class="side-count">{{ Conversations.length }} 个</span>
                </div>
                <n-button type="primary" size="small" block :loading="Creating" @click="createConversation">
                    <template #icon>
                        <i class="fa-solid fa-plus"></i>
                    </template>
                    新建会话
                </n-button>
            </div>
            <div class="side-body">
                <div v-if="!Conversations.length" class="side-empty">暂无会话，点上方「新建会话」开始</div>
                <div v-for="item in Conversations" :key="item.Id" class="side-item"
                    :class="{ active: item.Id === CurrentId }" :title="item.Title || '未命名会话'"
                    @click="openConversation(item)">
                    <div class="side-info">
                        <div class="side-name">{{ item.Title || '未命名会话' }}</div>
                        <div class="side-time">{{ shortTime(item.LastMessageTime || item.UpdateTime || item.CreateTime) }}</div>
                    </div>
                    <button class="side-del" type="button" title="删除会话" @click.stop="removeConversation(item)">
                        <i class="fa-solid fa-trash-can"></i>
                    </button>
                </div>
            </div>
        </aside>

        <!-- 右：对话区 -->
        <section class="ai-room">
            <header class="room-head">
                <div class="room-title">
                    <i class="fa-solid fa-robot"></i>
                    <span class="room-name">{{ CurrentTitle }}</span>
                </div>
                <span class="room-model mono">{{ ModelText }}</span>
                <div class="room-tools">
                    <n-switch v-model:value="AllowEnvValues" size="small" :disabled="!CurrentId" />
                    <span class="tool-label">本会话允许读取环境变量值</span>
                    <n-button size="tiny" quaternary @click="openContract">
                        <template #icon>
                            <i class="fa-solid fa-list-check"></i>
                        </template>
                        平台能力
                    </n-button>
                </div>
            </header>

            <!-- 运行状态条：run 未结束期间常驻，可随时停止 -->
            <div v-if="Running" class="run-bar">
                <span class="run-spin"></span>
                <span>运行中：{{ (Run && Run.Progress) || '思考中…' }}</span>
                <span v-if="Run && Run.Rounds" class="run-meta mono">第 {{ Run.Rounds }} 轮</span>
                <div class="run-action">
                    <n-button size="tiny" type="error" ghost @click="cancelRun">
                        <template #icon>
                            <i class="fa-solid fa-hand"></i>
                        </template>
                        停止
                    </n-button>
                </div>
            </div>

            <div ref="stream" class="room-stream">
                <div v-if="!CurrentId" class="stream-empty">
                    <i class="fa-solid fa-comments"></i>
                    <div class="empty-title">选择或新建一个会话</div>
                    <div class="empty-tip">AI 会读取脚本与日志，给出可试运行的修复提案（应用前需你确认）</div>
                </div>
                <div v-else-if="!RenderedMessages.length" class="stream-empty">
                    <i class="fa-solid fa-wand-magic-sparkles"></i>
                    <div class="empty-title">开始一次脚本分析</div>
                    <div class="empty-tip">如「帮我看看 demo/Demo.cs 为什么执行失败」，也可在下方先选好目标脚本</div>
                </div>

                <template v-for="m in RenderedMessages" :key="m.Id || m.Seq">
                    <!-- 执行过程块：挂在带 RunId 的 assistant 消息（结论文案）上方，默认折叠、首展懒加载 -->
                    <div v-if="m.stepsEntry" class="steps-card" :class="{ open: m.stepsEntry.expanded }">
                        <div class="steps-card-head" @click="toggleRunSteps(m.RunId)">
                            <i class="fa-solid fa-list-check"></i>
                            <span class="steps-card-title">执行过程（{{ m.stepsEntry.steps.length }} 步）</span>
                            <span v-if="!m.stepsEntry.loaded" class="steps-hint">展开加载轨迹</span>
                            <i :class="m.stepsEntry.expanded ? 'fa-solid fa-chevron-down' : 'fa-solid fa-chevron-right'"></i>
                        </div>
                        <div v-show="m.stepsEntry.expanded" class="steps-card-body">
                            <div v-if="!m.stepsEntry.steps.length" class="steps-empty">暂无过程记录</div>
                            <div v-for="step in stepRows(m.stepsEntry.steps)" :key="step.Id" class="step-row">
                                <i :class="step.Icon"></i>
                                <span class="step-name">{{ step.Label }}</span>
                                <span v-if="step.Duration" class="step-time mono">{{ step.Duration }}</span>
                                <span class="step-preview mono" :title="step.Detail">{{ step.Preview }}</span>
                            </div>
                            <div v-if="m.stepsEntry.steps.length >= 500" class="steps-empty">已达单次运行步骤上限（500），超出部分已截断</div>
                        </div>
                    </div>

                    <div class="msg-row" :class="{ mine: m.mine, status: m.Role === 'status', block: m.proposal || m.test }">
                    <!-- 状态行：居中灰字，不占气泡位 -->
                    <div v-if="m.Role === 'status'" class="status-line">{{ m.Content }}</div>

                    <!-- 提案卡 -->
                    <div v-else-if="m.proposal" class="card proposal-card">
                        <div class="card-head">
                            <i class="fa-solid fa-file-code"></i>
                            <span class="card-name">脚本修复提案 · {{ m.proposal.FileName }}</span>
                            <span class="pill" :class="m.proposal.statusCls">
                                <span class="dot"></span>{{ m.proposal.statusText }}
                            </span>
                            <span class="pill" :class="m.proposal.testCls">
                                <span class="dot"></span>{{ m.proposal.testText }}
                            </span>
                        </div>
                        <div v-if="m.proposal.Summary" class="card-summary">{{ m.proposal.Summary }}</div>
                        <div v-if="m.proposal.blockCount || m.proposal.errorCount" class="card-warn">
                            <i class="fa-solid fa-triangle-exclamation"></i>
                            生成时诊断：门禁拦截 {{ m.proposal.blockCount }} 项、编译错误 {{ m.proposal.errorCount }} 项
                            （应用时会重新走门禁与编译）
                        </div>
                        <div v-if="m.proposal.Status === 'Stale'" class="card-warn">
                            <i class="fa-solid fa-clock-rotate-left"></i>
                            脚本已被改动，请让 AI 重新生成提案
                        </div>
                        <div class="card-actions">
                            <span v-if="m.proposal.Status === 'Applied'" class="applied">
                                <i class="fa-solid fa-check"></i>
                                已应用（版本 {{ m.proposal.appliedShort }}）
                            </span>
                            <template v-else>
                                <n-button size="tiny" type="primary" ghost :loading="m.proposal.Testing"
                                    :disabled="m.proposal.testingDisabled" @click="testRunProposal(m.proposal)">
                                    <template #icon>
                                        <i class="fa-solid fa-flask"></i>
                                    </template>
                                    试运行
                                </n-button>
                                <n-button size="tiny" type="primary" :disabled="m.proposal.applyDisabled"
                                    @click="applyProposal(m.proposal)">
                                    <template #icon>
                                        <i class="fa-solid fa-check"></i>
                                    </template>
                                    应用
                                </n-button>
                                <n-button size="tiny" @click="discardProposal(m.proposal)">忽略</n-button>
                                <n-button size="tiny" tertiary @click="openDiff(m.proposal)">
                                    <template #icon>
                                        <i class="fa-solid fa-code-compare"></i>
                                    </template>
                                    查看差异
                                </n-button>
                            </template>
                        </div>
                    </div>

                    <!-- 试运行结果卡 -->
                    <div v-else-if="m.test" class="card result-card">
                        <div class="card-head">
                            <i class="fa-solid fa-vial-circle-check"></i>
                            <span class="card-name">试运行结果</span>
                            <span class="pill" :class="m.test.cls">
                                <span class="dot"></span>{{ m.test.text }}
                            </span>
                            <span v-if="m.test.Duration" class="mono">耗时 {{ m.test.Duration }}</span>
                            <div v-if="m.test.LogId" class="card-head-action">
                                <n-button size="tiny" tertiary @click="openLog(m.test.LogId)">
                                    <template #icon>
                                        <i class="fa-solid fa-up-right-from-square"></i>
                                    </template>
                                    查看完整日志
                                </n-button>
                            </div>
                        </div>
                        <pre v-if="m.test.Tail" class="result-log">{{ m.test.Tail }}</pre>
                    </div>

                    <!-- 普通气泡：assistant 文本 / 错误（红色边框） -->
                    <template v-else>
                        <div v-if="!m.mine" class="avatar">AI</div>
                        <div class="bubble" :class="{ error: m.Kind === 'error' }">
                            <template v-for="(block, index) in m.blocks" :key="index">
                                <pre v-if="block.code" class="code-block">{{ block.text }}</pre>
                                <!-- 已先做 HTML 转义再拼标签，仅允许 <p>/<br>/<code> 三种标签 -->
                                <div v-else class="md-text" v-html="block.html"></div>
                            </template>
                            <div class="bubble-time mono">{{ shortTime(m.CreateTime) }}</div>
                        </div>
                    </template>
                </div>
                </template>

                <!-- 运行中的实时执行过程块：会话流末尾常驻（结论消息落库后由上方同 RunId 的消息级块接管） -->
                <div v-if="liveStepEntry && !LiveBlockTaken" class="steps-card live" :class="{ open: liveStepEntry.expanded }">
                    <div class="steps-card-head" @click="toggleRunSteps(RunId)">
                        <span class="run-spin steps-spin"></span>
                        <span class="steps-card-title">执行过程（{{ liveStepEntry.steps.length }} 步）· {{ (Run && Run.Progress) || '思考中…' }}</span>
                        <i :class="liveStepEntry.expanded ? 'fa-solid fa-chevron-down' : 'fa-solid fa-chevron-right'"></i>
                    </div>
                    <div v-show="liveStepEntry.expanded" class="steps-card-body">
                        <div v-if="!liveStepEntry.steps.length" class="steps-empty">正在等待第一个动作…</div>
                        <div v-for="step in stepRows(liveStepEntry.steps)" :key="step.Id" class="step-row">
                            <i :class="step.Icon"></i>
                            <span class="step-name">{{ step.Label }}</span>
                            <span v-if="step.Duration" class="step-time mono">{{ step.Duration }}</span>
                            <span class="step-preview mono" :title="step.Detail">{{ step.Preview }}</span>
                        </div>
                        <div v-if="liveStepEntry.steps.length >= 500" class="steps-empty">已达单次运行步骤上限（500），超出部分已截断</div>
                    </div>
                </div>
            </div>

            <div class="room-composer">
                <div class="composer-inner" :class="{ focused: Focused }">
                    <textarea ref="input" v-model="Input" rows="1" :placeholder="ComposerPlaceholder"
                        @focus="Focused = true" @blur="Focused = false" @keydown="onKeydown" @input="autoGrow"></textarea>
                    <n-button type="primary" size="small" :loading="Sending" :disabled="!CanSend" @click="send">
                        <template #icon>
                            <i class="fa-solid fa-paper-plane"></i>
                        </template>
                        发送
                    </n-button>
                </div>
                <div class="composer-meta">
                    <span class="hint">Enter 发送 / Shift+Enter 换行</span>
                    <n-select v-model:value="TargetFile" size="small" clearable filterable :options="ScriptOptions"
                        placeholder="目标脚本（可留空，由 AI 从消息里识别）" style="width: 320px" />
                </div>
            </div>
        </section>

        <!-- 平台能力 -->
        <n-modal preset="card" v-model:show="ContractModal.Show" title="AI 已知的平台能力" style="width: 900px"
            :mask-closable="false">
            <div class="contract-tip">
                下面是 Agent 内置的能力摘要（脚本契约、可用工具与安全约束），只读；改脚本前先看它是否理解你的写法。
            </div>
            <div class="contract-box">
                <pre>{{ ContractModal.Text }}</pre>
            </div>
            <template #footer>
                <div style="text-align: right">
                    <n-button style="margin-right: 8px" @click="copyContract">
                        <template #icon>
                            <i class="fa-regular fa-copy"></i>
                        </template>
                        复制
                    </n-button>
                    <n-button @click="ContractModal.Show = false">关闭</n-button>
                </div>
            </template>
        </n-modal>

        <!-- 提案差异 -->
        <n-modal preset="card" v-model:show="DiffModal.Show" :title="DiffModal.Title" style="width: 1200px"
            :mask-closable="false">
            <div style="margin-bottom: 8px">
                <span class="pill ok"><span class="dot"></span>新增 {{ DiffModal.Added }}</span>
                <span class="pill danger" style="margin-left: 8px"><span class="dot"></span>删除 {{ DiffModal.Removed }}</span>
                <span v-if="DiffModal.Truncated" style="margin-left: 12px; color: var(--warn)">
                    差异过大，已按整块替换展示
                </span>
            </div>
            <div class="diff-box">
                <div v-for="(row, index) in DiffRows" :key="index" :class="['diff-row', 'diff-' + row.kind]">
                    <span class="diff-no">{{ row.oldLine === null ? '' : row.oldLine }}</span>
                    <span class="diff-no">{{ row.newLine === null ? '' : row.newLine }}</span>
                    <span class="diff-sign">{{ row.kind === 'add' ? '+' : row.kind === 'del' ? '-' : ' ' }}</span>
                    <span class="diff-text mono">{{ row.text }}</span>
                </div>
                <div v-if="DiffModal.Rows.length > DiffRows.length" class="diff-more">
                    仅展示前 {{ DiffRows.length }} 行差异（共 {{ DiffModal.Rows.length }} 行）
                </div>
            </div>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="DiffModal.Show = false">关闭</n-button>
                </div>
            </template>
        </n-modal>

        <!-- 应用被拒时的诊断清单 -->
        <n-modal preset="card" v-model:show="Diagnostics.Show" :title="Diagnostics.Title" style="width: 760px">
            <n-alert type="error" :show-icon="true" style="margin-bottom: 12px">
                提案未通过安全门禁/编译检查，脚本未落盘（与在线保存同口径）。
            </n-alert>
            <div v-for="group in Diagnostics.Groups" :key="group.Title" style="margin-bottom: 12px">
                <div style="margin-bottom: 6px">{{ group.Title }}（{{ group.Items.length }}）</div>
                <div class="diag-list">
                    <div v-for="(item, index) in group.Items" :key="index" class="diag-item mono">
                        {{ diagText(item) }}
                    </div>
                </div>
            </div>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="Diagnostics.Show = false">关闭</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>
<script>
  import { diffLines } from '@/utils/lineDiff'
  import { ScriptList as GetScriptList } from '@/api/task'
  import {
    GetConversations,
    SaveConversation,
    DeleteConversations,
    GetAiMessages,
    SendAgentChat,
    GetAgentRun,
    GetLatestRun,
    CancelAgentRun,
    GetAgentSteps,
    GetAgentContract,
    GetDefaultModel,
    TestRunProposal,
    ApplyProposal,
    DiscardProposal,
    GetProposalContent
  } from '@/api/aiAgent'

  // 试运行状态 → 胶囊文案与语义色（与后端 AiTestStatus 取值一一对应）
  const TEST_STATUS = {
    None: { text: '未试运行', cls: 'off' },
    Running: { text: '试运行中', cls: 'warn' },
    Passed: { text: '试运行通过', cls: 'ok' },
    Failed: { text: '试运行失败', cls: 'danger' },
    Timeout: { text: '试运行超时', cls: 'warn' }
  }

  // 提案状态 → 胶囊文案与语义色（与后端 AiProposalStatus 一致）
  const PROPOSAL_STATUS = {
    Pending: { text: '待确认', cls: 'warn' },
    Applied: { text: '已应用', cls: 'ok' },
    Discarded: { text: '已忽略', cls: 'off' },
    Stale: { text: '已过期', cls: 'danger' }
  }

  const DIFF_MAX_ROWS = 3000
  const STEP_PREVIEW = 80
  const RUN_POLL_INTERVAL = 1200
  const TEST_WATCH_INTERVAL = 2000
  const TEST_WATCH_LIMIT = 30
  const POLL_FAIL_LIMIT = 5

  const HTML_ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }

  // 先转义再拼标签：模型原文可能带 <style>/<img onerror> 之类内容，不转义直接 v-html 就是注入口
  function escapeHtml(text) {
    return String(text == null ? '' : text).replace(/[&<>"']/g, (char) => HTML_ESCAPES[char])
  }

  /**
   * 极简 Markdown：只认 ``` 围栏代码块与 `行内代码`，其余按段落保留换行（不引第三方 markdown 库）。
   * 代码块只交给 Vue 文本插值渲染，不经 v-html；正文段是先转义、再把 `x` 换成 <code> 后拼出来的。
   */
  function renderMarkdownLite(raw) {
    const blocks = []
    String(raw == null ? '' : raw).split('```').forEach((chunk, index) => {
      if (index % 2 === 1) {
        // 奇数是围栏内的代码：首行是语言标记（```cs）时剥掉，代码本体原样保留
        blocks.push({ code: true, text: chunk.replace(/^[A-Za-z0-9_+#.-]*\r?\n/, '').replace(/\s+$/, '') })
        return
      }
      const body = chunk.replace(/^\s*\n+/, '').replace(/\n+\s*$/, '')
      if (!body.trim()) {
        return
      }
      const html = body
        .split(/\n{2,}/)
        .map((para) => {
          const inner = escapeHtml(para.trim()).replace(/`([^`\n]+)`/g, '<code>$1</code>').replace(/\n/g, '<br>')
          return `<p>${inner}</p>`
        })
        .join('')
      blocks.push({ code: false, html })
    })
    return blocks
  }

  // Payload 约定是 JSON 字符串，但历史/异常数据可能是对象或坏 JSON：统一在此收口，坏数据退回纯文本渲染
  function parsePayload(raw) {
    if (!raw) {
      return null
    }
    if (typeof raw === 'object') {
      return raw
    }
    try {
      const value = JSON.parse(raw)
      return value && typeof value === 'object' ? value : null
    } catch (error) {
      return null
    }
  }

  function readDiagnostics(value) {
    const parsed = typeof value === 'string' ? parsePayload(value) : value
    if (!parsed || typeof parsed !== 'object') {
      return { Blocked: [], Errors: [], Warnings: [] }
    }
    return {
      Blocked: Array.isArray(parsed.Blocked) ? parsed.Blocked : [],
      Errors: Array.isArray(parsed.Errors) ? parsed.Errors : [],
      Warnings: Array.isArray(parsed.Warnings) ? parsed.Warnings : []
    }
  }

  function firstText(...values) {
    for (const value of values) {
      if (value !== undefined && value !== null && String(value) !== '') {
        return String(value)
      }
    }
    return ''
  }

  // 单行摘要：换行/多空格压平，超长截断（过程面板与日志尾都用它，避免撑爆行高）
  function flatten(text, max) {
    const flat = String(text == null ? '' : text).replace(/\s+/g, ' ').trim()
    return flat.length > max ? flat.slice(0, max) + '…' : flat
  }

  function formatDiagnostic(item) {
    if (item === null || item === undefined) {
      return ''
    }
    if (typeof item === 'string') {
      return item
    }
    return (item.Line ? `[第${item.Line}行] ` : '') + (item.Message || '')
  }

  // 脚本树 → 目标脚本下拉项：只取 .cs 文件叶节点，path 就是后端要的相对路径
  function flattenScripts(nodes) {
    const options = []
    for (const node of nodes || []) {
      if (Array.isArray(node.children) && node.children.length) {
        options.push(...flattenScripts(node.children))
        continue
      }
      if (node.path && /\.cs$/i.test(node.path)) {
        options.push({ label: node.path, value: node.path })
      }
    }
    return options
  }

  /**
   * AI 助手对话页（AI 脚本修复 Agent 阶段三/四）：
   * 左会话列表 + 右对话区；消息按 Role/Kind 渲染（气泡 / 提案卡 / 试运行结果卡）。
   * 运行进度不占长连接：发送后 1.2s 轮询 runs（状态条）与 messages（增量追加），run 结束即停。
   */
  export default {
    name: 'AiIndex',
    data() {
      return {
        Conversations: [],
        CurrentId: '',
        Messages: [],
        MaxSeq: 0,
        DefaultModel: null,
        Run: null,
        RunId: '',
        // 执行过程缓存（按 runId）：steps=已拉取轨迹、maxSeq=增量游标、loaded=全量已拉（懒加载完成/实时轨道）、expanded=展开态
        StepCache: {},
        Input: '',
        Sending: false,
        Creating: false,
        TestingProposalId: '',
        TargetFile: null,
        ScriptOptions: [],
        Focused: false,
        ContractModal: { Show: false, Text: '' },
        DiffModal: { Show: false, Title: '提案差异', Rows: [], Added: 0, Removed: 0, Truncated: false },
        Diagnostics: { Show: false, Title: '提案应用被拒绝', Groups: [] }
      }
    },
    computed: {
      CurrentConversation() {
        return this.Conversations.find((n) => n.Id === this.CurrentId) || null
      },
      CurrentTitle() {
        return this.CurrentConversation ? this.CurrentConversation.Title || '未命名会话' : 'AI 助手'
      },
      // 状态条只认 Running：Succeeded/Failed/WaitingConfirm/Cancelled 都算结束
      Running() {
        return !!this.Run && this.Run.Status === 'Running'
      },
      // 运行中禁用发送：页面只有一条运行轮询轨道，并发发起会互相覆盖状态条
      CanSend() {
        return !!String(this.Input || '').trim() && !this.Sending && !this.Running
      },
      ComposerPlaceholder() {
        if (!this.CurrentId) {
          return '新建或选择左侧会话后开始（Enter 发送 / Shift+Enter 换行）'
        }
        if (this.Running) {
          return 'AI 运行中：可先点「停止」再发送新内容'
        }
        return '描述要处理的脚本问题…（Enter 发送 / Shift+Enter 换行）'
      },
      ModelText() {
        const model = this.DefaultModel
        if (!model || !model.ModelId) {
          return '未配置可用模型（请到系统管理「AI供应商」页配置）'
        }
        const context = model.ContextWindow ? `上下文 ${this.formatTokens(model.ContextWindow)}K` : '上下文未知'
        const mode = model.SupportsTools === 1 ? '工具模式' : model.SupportsTools === 2 ? 'JSON 模式' : '工具能力未探测'
        return `${model.ProviderName || '未知供应商'} / ${model.DisplayName || model.ModelId} · ${context} · ${mode}`
      },
      // 环境变量值属凭据：开关逐会话显式开启，改动即落库（后端据此决定是否把变量值写进提示词）
      AllowEnvValues: {
        get() {
          return !!(this.CurrentConversation && this.CurrentConversation.AllowEnvValues)
        },
        set(value) {
          const current = this.CurrentConversation
          if (!current) {
            return
          }
          const before = !!current.AllowEnvValues
          if (before === value) {
            return
          }
          current.AllowEnvValues = value
          SaveConversation({ Id: current.Id, Title: current.Title, AllowEnvValues: value }).then((res) => {
            if (res) {
              current.AllowEnvValues = !!res.AllowEnvValues
            }
            this.$message.success(value ? '本会话允许把环境变量值发给模型' : '本会话只提供变量名，不外发值')
          }).catch((error) => {
            current.AllowEnvValues = before
            this.$message.error(String((error && error.message) || error || '会话设置保存失败'))
          })
        }
      },
      // 消息视图模型：解析 Payload、预渲染 Markdown，模板只做展示
      RenderedMessages() {
        return this.Messages.map((message) => {
          const row = {
            ...message,
            mine: message.Role === 'user',
            blocks: [],
            proposal: null,
            test: null,
            // 执行过程块只挂「带 RunId 的 assistant 消息」（结论/提案后的说明文本）；提案卡本身不带 RunId
            stepsEntry: message.RunId && message.Role === 'assistant' ? (this.StepCache[message.RunId] || null) : null
          }
          if (message.Kind === 'proposal') {
            row.proposal = this.buildProposal(message)
          } else if (message.Kind === 'test_result') {
            row.test = this.buildTestResult(message)
          }
          // 提案卡解析失败（Payload 坏）时退回纯文本，不能让它整条消失
          if (!row.proposal && !row.test) {
            row.blocks = renderMarkdownLite(message.Content)
          }
          return row
        })
      },
      // 运行中的实时过程块数据（结论消息落库后由消息级块接管，末尾不再重复挂）
      liveStepEntry() {
        return this.Running && this.RunId ? (this.StepCache[this.RunId] || null) : null
      },
      LiveBlockTaken() {
        return this.RenderedMessages.some((m) => m.stepsEntry && m.RunId === this.RunId)
      },
      DiffRows() {
        return this.DiffModal.Rows.slice(0, DIFF_MAX_ROWS)
      }
    },
    created() {
      // 并发标记与定时器句柄：非响应式数据，挂实例上避免无谓的组件重渲染
      this._fetchBusy = false
      this._pollBusy = false
      this._pollFails = 0
      this._testTries = 0
      this.pollTimer = null
      this.testTimer = null
    },
    mounted() {
      this.loadConversations()
      this.loadDefaultModel()
      this.loadScripts()
    },
    activated() {
      // keep-alive 返回：会话标题/最后消息时间可能已变，且离开期间轮询已停，这里补一次增量与运行态校准
      this.loadConversations()
      this.loadDefaultModel()
      this.fetchNewMessages().then(() => this.resumeRunIfNeeded())
    },
    deactivated() {
      // 离开页面必须停表：keep-alive 下组件仍活着，后台轮询会一直占用请求
      this.stopPolling()
      this.stopTestWatch()
    },
    beforeUnmount() {
      this.stopPolling()
      this.stopTestWatch()
    },
    methods: {
      shortTime(value) {
        // 窄栏列表只留「月-日 时:分」
        return value ? String(value).replace('T', ' ').slice(5, 16) : ''
      },
      formatTokens(value) {
        const size = Number(value || 0)
        return size >= 1000 ? (size / 1000).toFixed(size % 1000 === 0 ? 0 : 1) : String(size)
      },
      formatDuration(ms) {
        const value = Number(ms || 0)
        return value >= 1000 ? (value / 1000).toFixed(1) + 's' : value + 'ms'
      },
      // 步骤行视图模型（实时块与历史块共用展示口径）：图标、名称、耗时、单行摘要
      stepRows(steps) {
        return (steps || []).map((step) => {
          const isResult = step.Kind === 'tool_result'
          const isCall = step.Kind === 'tool_call'
          const detail = isResult || step.Kind === 'error'
            ? firstText(step.Result, step.Arguments)
            : firstText(step.Arguments, step.Result)
          return {
            Id: step.Id,
            Icon: step.Kind === 'error'
              ? 'fa-solid fa-triangle-exclamation'
              : isCall ? 'fa-solid fa-screwdriver-wrench' : isResult ? 'fa-solid fa-reply' : 'fa-solid fa-comment-dots',
            Label: firstText(step.Name, isCall ? '工具调用' : isResult ? '工具结果' : step.Kind === 'error' ? '错误' : '模型输出'),
            Duration: step.DurationMs ? this.formatDuration(step.DurationMs) : '',
            Preview: flatten(detail, STEP_PREVIEW),
            Detail: flatten(detail, 400)
          }
        })
      },
      diagText(item) {
        return formatDiagnostic(item)
      },
      onKeydown(event) {
        if (event.key === 'Enter' && !event.shiftKey) {
          event.preventDefault()
          this.send()
        }
      },
      autoGrow() {
        const el = this.$refs.input
        if (!el) {
          return
        }
        el.style.height = 'auto'
        el.style.height = `${Math.min(el.scrollHeight, 140)}px`
      },

      // ---------------------------------------------------------------- 会话
      loadConversations() {
        GetConversations().then((res) => {
          this.Conversations = res || []
          if (!this.CurrentId && this.Conversations.length) {
            this.openConversation(this.Conversations[0])
          }
        }).catch((error) => {
          this.$message.error(String((error && error.message) || error || 'AI 会话列表加载失败'))
        })
      },
      loadDefaultModel() {
        GetDefaultModel().then((res) => {
          this.DefaultModel = res || null
        }).catch(() => {
          // 未配供应商时后端直接抛业务异常：顶栏给引导文案即可，不弹错打断
          this.DefaultModel = null
        })
      },
      loadScripts() {
        GetScriptList().then((res) => {
          this.ScriptOptions = flattenScripts(res)
        }).catch(() => {
          // 脚本目录缺失/接口失败不影响对话：留空即由 AI 从消息里识别目标脚本
          this.ScriptOptions = []
        })
      },
      createConversation() {
        this.Creating = true
        SaveConversation({ Title: '', AllowEnvValues: false }).then((res) => {
          this.Creating = false
          if (!res || !res.Id) {
            return
          }
          this.switchTo(res.Id)
          this.loadConversations()
          this.$nextTick(() => {
            const el = this.$refs.input
            if (el) {
              el.focus()
            }
          })
        }).catch((error) => {
          this.Creating = false
          this.$message.error(String((error && error.message) || error || '新建会话失败'))
        })
      },
      openConversation(item) {
        if (!item || item.Id === this.CurrentId) {
          return
        }
        this.switchTo(item.Id)
        this.fetchNewMessages().then(() => this.resumeRunIfNeeded())
      },
      // 切会话：清空消息/过程/运行态并停表，避免上一个会话的轮询把消息写到新会话里
      switchTo(conversationId) {
        this.stopPolling()
        this.stopTestWatch()
        this.CurrentId = conversationId
        this.Messages = []
        this.MaxSeq = 0
        this.StepCache = {}
        this.Run = null
        this.RunId = ''
      },
      removeConversation(item) {
        const c = this
        this.$dialog.warning({
          title: '删除 AI 会话',
          content: `确定删除会话「${item.Title || '未命名会话'}」？其消息、运行记录与提案一并删除。`,
          positiveText: '确认删除',
          negativeText: '取消',
          onPositiveClick: function () {
            return DeleteConversations([item.Id]).then(() => {
              c.$message.success('会话已删除')
              if (c.CurrentId === item.Id) {
                c.switchTo('')
              }
              c.loadConversations()
            }).catch((error) => {
              c.$message.error(String((error && error.message) || error || '删除会话失败'))
            })
          }
        })
      },

      // ---------------------------------------------------------------- 消息
      // 增量拉消息：afterSeq=0 取最近一屏，运行期间由轮询持续追加。
      // 失败不向调用方抛（拦截器已提示过一次），返回 0 让轮询/首屏逻辑继续。
      async fetchNewMessages() {
        const conversationId = this.CurrentId
        if (!conversationId || this._fetchBusy) {
          return 0
        }
        this._fetchBusy = true
        const stick = this.nearBottom()
        try {
          const list = await GetAiMessages({ conversationId, afterSeq: this.MaxSeq, limit: 200 })
          // 切会话后才回来的旧响应直接丢弃
          if (conversationId !== this.CurrentId) {
            return 0
          }
          const rows = (list || []).filter((n) => !this.Messages.some((m) => m.Id === n.Id))
          if (!rows.length) {
            return 0
          }
          this.Messages = this.Messages.concat(rows)
          this.MaxSeq = Math.max(this.MaxSeq, ...rows.map((n) => Number(n.Seq) || 0))
          this.ensureStepEntries()
          if (stick) {
            this.scrollToBottom()
          }
          return rows.length
        } catch (error) {
          return 0
        } finally {
          this._fetchBusy = false
        }
      },
      // 全量重拉最近一屏：提案的试运行状态与结果常写在原消息的 Payload 里，增量拉取看不见这种就地更新
      reloadMessages() {
        const conversationId = this.CurrentId
        if (!conversationId) {
          return Promise.resolve()
        }
        return GetAiMessages({ conversationId, afterSeq: 0, limit: 200 }).then((res) => {
          if (conversationId !== this.CurrentId) {
            return
          }
          const rows = res || []
          this.Messages = rows
          this.MaxSeq = rows.length ? Math.max(...rows.map((n) => Number(n.Seq) || 0)) : 0
          this.ensureStepEntries()
          this.scrollToBottom()
        }).catch(() => {
          // 重拉失败保持现状，下一次增量/轮询会继续
        })
      },

      // ---------------------------------------------------------------- 运行轮询
      startPolling(runId) {
        this.stopPolling()
        if (!runId) {
          return
        }
        this.RunId = runId
        this._pollFails = 0
        this.pollTimer = setInterval(() => this.pollRun(), RUN_POLL_INTERVAL)
        this.pollRun()
      },
      stopPolling() {
        if (this.pollTimer) {
          clearInterval(this.pollTimer)
          this.pollTimer = null
        }
      },
      async pollRun() {
        if (this._pollBusy || !this.RunId) {
          return
        }
        const runId = this.RunId
        this._pollBusy = true
        try {
          const run = await GetAgentRun(runId)
          if (runId !== this.RunId) {
            return
          }
          this._pollFails = 0
          this.Run = run
          await this.fetchNewMessages()
          // 运行期间始终增量拉过程（内联过程块常驻会话流，不再「面板展开才拉」）
          await this.fetchSteps(runId)
          if (!run || run.Status !== 'Running') {
            this.stopPolling()
            // 收尾：run 结束的瞬间往往还有最后一条结论消息正在写入
            await this.fetchNewMessages()
            // 过程块转为折叠态，钉在结论消息上方（同 runId 的消息级块接管渲染）
            const entry = this.StepCache[runId]
            if (entry) {
              entry.expanded = false
            }
            this.loadConversations()
          }
        } catch (error) {
          // 单次失败多为瞬时（进程重启/网络抖动）：连错到阈值才放弃并收起状态条，避免无限重试刷提示
          this._pollFails += 1
          if (this._pollFails >= POLL_FAIL_LIMIT) {
            this.stopPolling()
            this.Run = null
            this.$message.error(String((error && error.message) || error || 'AI 运行状态查询失败'))
          }
        } finally {
          this._pollBusy = false
        }
      },
      // 刷新/重进/切回会话：运行态恢复的唯一权威来源是后端 latest-run 端点
      // （用户消息不带 RunId、运行中又没有 assistant 消息落库，扫消息只能扫到上一次已结束的 run）
      resumeRunIfNeeded() {
        const conversationId = this.CurrentId
        if (!conversationId) {
          return
        }
        GetLatestRun(conversationId).then((run) => {
          if (conversationId !== this.CurrentId) {
            return
          }
          if (!run) {
            // 会话没有 run：清残留运行态
            this.Run = null
            this.RunId = ''
            return
          }
          this.Run = run
          this.RunId = run.Id
          if (run.Status === 'Running') {
            this.ensureStepEntry(run.Id, { loaded: true, expanded: true })
            this.startPolling(run.Id)
          }
        }).catch(() => {
          // latest-run 拉取失败静默，不打断阅读（下次切会话/activated 再试）
        })
      },
      // ================================================================= 执行过程（内联块）
      // 取或建过程缓存条目（Vue3 对新增 key 的响应式赋值无碍）
      ensureStepEntry(runId, defaults) {
        if (!runId) {
          return null
        }
        if (!this.StepCache[runId]) {
          this.StepCache[runId] = Object.assign({ steps: [], maxSeq: 0, loaded: false, expanded: false }, defaults || {})
        }
        return this.StepCache[runId]
      },
      // 消息加载后为「带 RunId 的 assistant 消息」补建缓存条目（历史回看块默认折叠、首展懒加载）
      ensureStepEntries() {
        for (const message of this.Messages) {
          if (message.RunId && message.Role === 'assistant') {
            this.ensureStepEntry(message.RunId)
          }
        }
      },
      // 运行中实时增量拉过程（afterSeq=该 run 已拉到的最大步序）
      fetchSteps(runId) {
        const id = runId || this.RunId
        if (!id) {
          return Promise.resolve()
        }
        const entry = this.ensureStepEntry(id)
        return GetAgentSteps(id, entry.maxSeq).then((res) => {
          const rows = res || []
          if (!rows.length) {
            return
          }
          entry.steps = entry.steps.concat(rows)
          entry.maxSeq = Math.max(entry.maxSeq, ...rows.map((n) => Number(n.Seq) || 0))
        }).catch(() => {
          // 过程块是辅助信息：拉取失败静默，不影响消息流
        })
      },
      // 展开/折叠过程块；历史块首次展开时懒加载全量轨迹（同一 run 不重复拉）
      toggleRunSteps(runId) {
        const entry = this.ensureStepEntry(runId)
        entry.expanded = !entry.expanded
        if (entry.expanded && !entry.loaded) {
          GetAgentSteps(runId, 0).then((res) => {
            const rows = res || []
            entry.steps = rows
            entry.maxSeq = rows.length ? Math.max(...rows.map((n) => Number(n.Seq) || 0)) : 0
            entry.loaded = true
          }).catch(() => {
            // 拉取失败收起，等下次展开重试
            entry.expanded = false
          })
        }
      },
      cancelRun() {
        if (!this.RunId) {
          return
        }
        CancelAgentRun(this.RunId).then(() => {
          this.$message.info('已请求停止，等待当前一步结束…')
        }).catch((error) => {
          this.$message.error(String((error && error.message) || error || '停止运行失败'))
        })
      },

      // ---------------------------------------------------------------- 发送
      async send() {
        const content = String(this.Input || '').trim()
        if (!content) {
          this.$message.warning('请输入内容后再发送')
          return
        }
        if (this.Sending || this.Running) {
          return
        }
        this.Sending = true
        try {
          // 后端 chat 要求会话已存在：未选中任何会话时先建一个空会话，消息内容由后端据首条消息生成标题
          const conversationId = this.CurrentId || await this.ensureConversation()
          const res = await SendAgentChat({
            ConversationId: conversationId,
            Content: content,
            TargetFile: this.TargetFile || null
          })
          this.Input = ''
          this.$nextTick(this.autoGrow)
          this.Run = { Id: res && res.RunId, Status: 'Running', Progress: '已提交，等待模型响应…' }
          this.ensureStepEntry(res && res.RunId, { loaded: true, expanded: true })
          await this.fetchNewMessages()
          this.startPolling(res && res.RunId)
          this.loadConversations()
        } catch (error) {
          // 未配供应商/开关关闭等业务失败由后端文案说明（信封 Code!=200 时拦截器已提示一次）
          this.$message.error(String((error && error.message) || error || 'AI 请求失败'))
        } finally {
          this.Sending = false
        }
      },
      ensureConversation() {
        return SaveConversation({ Title: '', AllowEnvValues: false }).then((res) => {
          if (!res || !res.Id) {
            throw new Error('会话创建失败，请重试')
          }
          this.switchTo(res.Id)
          this.loadConversations()
          return res.Id
        })
      },

      // ---------------------------------------------------------------- 提案
      buildProposal(message) {
        const payload = parsePayload(message.Payload)
        if (!payload || !payload.Id) {
          return null
        }
        const diagnostics = readDiagnostics(payload.Diagnostics)
        const test = TEST_STATUS[payload.TestStatus] || TEST_STATUS.None
        const status = PROPOSAL_STATUS[payload.Status] || PROPOSAL_STATUS.Pending
        return {
          Id: payload.Id,
          FileName: payload.FileName || '(未知脚本)',
          Summary: payload.Summary || '',
          Status: payload.Status || 'Pending',
          TestStatus: payload.TestStatus || 'None',
          TestRunLogId: payload.TestRunLogId || '',
          appliedShort: String(payload.AppliedVersionId || '').slice(0, 8),
          blockCount: diagnostics.Blocked.length,
          errorCount: diagnostics.Errors.length,
          testText: test.text,
          testCls: test.cls,
          statusText: status.text,
          statusCls: status.cls,
          Testing: this.TestingProposalId === payload.Id,
          testingDisabled: this.TestingProposalId === payload.Id || payload.TestStatus === 'Running',
          applyDisabled: payload.Status === 'Stale' || payload.TestStatus === 'Running'
        }
      },
      buildTestResult(message) {
        const payload = parsePayload(message.Payload) || {}
        const test = TEST_STATUS[payload.Status] || TEST_STATUS.None
        return {
          Id: payload.Id || message.Id,
          text: test.text,
          cls: test.cls,
          Duration: payload.DurationMs ? this.formatDuration(payload.DurationMs) : '',
          LogId: firstText(payload.TestRunLogId, payload.LogId),
          // 日志尾字段按后端可能的几种命名兜底，兜不到就退回消息正文
          Tail: firstText(payload.Tail, payload.LogTail, payload.Output, payload.Summary, message.Content)
        }
      },
      testRunProposal(proposal) {
        if (!proposal || this.TestingProposalId) {
          return
        }
        this.TestingProposalId = proposal.Id
        this.$message.loading('已提交试运行，正在执行脚本…', { duration: 1200 })
        TestRunProposal(proposal.Id).then(() => {
          return this.reloadMessages()
        }).then(() => {
          this.watchTestRun(proposal.Id)
        }).catch((error) => {
          this.stopTestWatch()
          this.$message.error(String((error && error.message) || error || '试运行提交失败'))
        })
      },
      /**
       * 试运行在后端异步跑，提交后原地没有结果可看：每 2s 重拉一屏消息，
       * 直到提案的 TestStatus 不再是运行中、有新消息追加（试运行结果卡）或次数用尽。
       */
      watchTestRun(proposalId) {
        this.stopTestWatch()
        this._testTries = 0
        const beforeSeq = this.MaxSeq
        this.testTimer = setInterval(async () => {
          this._testTries += 1
          await this.reloadMessages()
          const status = this.proposalTestStatus(proposalId)
          const arrived = this.MaxSeq > beforeSeq
          if (arrived || (status && status !== 'Running' && status !== 'None') || this._testTries >= TEST_WATCH_LIMIT) {
            this.stopTestWatch()
          }
        }, TEST_WATCH_INTERVAL)
      },
      stopTestWatch() {
        if (this.testTimer) {
          clearInterval(this.testTimer)
          this.testTimer = null
        }
        this.TestingProposalId = ''
      },
      proposalTestStatus(proposalId) {
        for (let i = this.Messages.length - 1; i >= 0; i--) {
          const message = this.Messages[i]
          if (message.Kind !== 'proposal') {
            continue
          }
          const payload = parsePayload(message.Payload)
          if (payload && payload.Id === proposalId) {
            return payload.TestStatus || 'None'
          }
        }
        return ''
      },
      applyProposal(proposal) {
        const c = this
        this.$dialog.warning({
          title: '应用修复提案',
          content: `将把提案内容写入「${proposal.FileName}」并记入版本历史（可回滚）。应用同样要过安全门禁与编译检查。`,
          positiveText: '确认应用',
          negativeText: '取消',
          onPositiveClick: function () {
            return ApplyProposal(proposal.Id).then((res) => {
              if (res && res.Success) {
                c.$message.success('提案已应用，脚本已写入并记入版本历史')
                c.reloadMessages()
                c.loadConversations()
                return
              }
              c.showDiagnostics(res || {})
            }).catch((error) => {
              c.$message.error(String((error && error.message) || error || '应用提案失败'))
            })
          }
        })
      },
      discardProposal(proposal) {
        const c = this
        this.$dialog.warning({
          title: '忽略修复提案',
          content: '忽略后该提案不再可用（脚本不受影响）；需要时请让 AI 重新生成。',
          positiveText: '确认忽略',
          negativeText: '取消',
          onPositiveClick: function () {
            return DiscardProposal(proposal.Id).then(() => {
              c.$message.success('提案已忽略')
              return c.reloadMessages()
            }).catch((error) => {
              c.$message.error(String((error && error.message) || error || '忽略提案失败'))
            })
          }
        })
      },
      openDiff(proposal) {
        GetProposalContent(proposal.Id).then((res) => {
          const result = diffLines(res ? res.BaseContent : '', res ? res.NewContent : '')
          this.DiffModal.Title = `提案差异：${(res && res.FileName) || proposal.FileName || ''}`
          this.DiffModal.Rows = result.rows
          this.DiffModal.Added = result.added
          this.DiffModal.Removed = result.removed
          this.DiffModal.Truncated = result.truncated
          this.DiffModal.Show = true
        }).catch((error) => {
          this.$message.error(String((error && error.message) || error || '提案内容读取失败'))
        })
      },
      openLog(logId) {
        // Payload 只给日志 Id，正文走既有日志中心页
        this.$router.push({ path: '/logs/index', query: { id: logId } }).catch(() => {
          this.$message.warning(`无法跳转到日志页，请手动打开日志中心并搜索：${logId}`)
        })
      },
      showDiagnostics(result) {
        const groups = []
        if ((result.Blocked || []).length) groups.push({ Title: '安全门禁拦截', Items: result.Blocked })
        if ((result.Errors || []).length) groups.push({ Title: '编译错误', Items: result.Errors })
        if ((result.Warnings || []).length) groups.push({ Title: '警告', Items: result.Warnings })
        if (!groups.length) {
          // 失败但没带回诊断（如磁盘写入失败）：给条兜底文案，别让弹层空着
          groups.push({ Title: '未返回诊断明细', Items: ['应用未成功，请查看服务端日志了解原因'] })
        }
        this.Diagnostics.Groups = groups
        this.Diagnostics.Show = true
      },

      // ---------------------------------------------------------------- 平台能力
      openContract() {
        GetAgentContract().then((res) => {
          this.ContractModal.Text = typeof res === 'string' ? res : JSON.stringify(res || {}, null, 2)
          this.ContractModal.Show = true
        }).catch((error) => {
          this.$message.error(String((error && error.message) || error || '平台能力读取失败'))
        })
      },
      async copyContract() {
        try {
          await navigator.clipboard.writeText(this.ContractModal.Text || '')
          this.$message.success('已复制平台能力文本')
        } catch (error) {
          this.$message.error('复制失败，请手动选择文本')
        }
      },

      // ---------------------------------------------------------------- 滚动
      nearBottom() {
        const stream = this.$refs.stream
        if (!stream) {
          return true
        }
        return stream.scrollHeight - stream.scrollTop - stream.clientHeight < 120
      },
      scrollToBottom() {
        this.$nextTick(() => {
          const stream = this.$refs.stream
          if (stream) {
            stream.scrollTop = stream.scrollHeight
          }
        })
      }
    }
  }
</script>
<style scoped>
.ai-page {
    display: flex;
    height: 100%;
    min-height: 0;
    background: var(--bg);
}

/* ---------------------------------------------------------------- 左：会话列表 */
.ai-side {
    width: 268px;
    flex: none;
    display: flex;
    flex-direction: column;
    min-height: 0;
    border-right: 1px solid var(--line);
    background: rgba(20, 28, 46, .45);
}

.side-head {
    flex: none;
    padding: 14px 12px 10px;
}

.side-title {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 10px;
}

.side-title h3 {
    margin: 0;
    font-size: 16px;
    font-weight: 700;
    color: var(--text);
}

.side-count {
    margin-left: auto;
    font-size: 11.5px;
    color: var(--text-2);
}

.side-body {
    flex: 1;
    overflow-y: auto;
    padding: 2px 10px 10px;
}

.side-empty {
    padding: 24px 0;
    text-align: center;
    font-size: 12.5px;
    color: var(--text-3);
}

.side-item {
    position: relative;
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 9px 10px;
    border: 1px solid transparent;
    border-radius: 12px;
    cursor: pointer;
}

.side-item:hover {
    background: rgba(148, 163, 184, .07);
}

.side-item.active {
    background: var(--accent-weak);
    border-color: rgba(14, 165, 233, .28);
}

.side-info {
    flex: 1;
    min-width: 0;
}

.side-name {
    font-size: 13.5px;
    font-weight: 600;
    color: var(--text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.side-time {
    margin-top: 2px;
    font-size: 11px;
    color: var(--text-2);
    transition: opacity .15s ease;
}

/* 悬浮时时间淡出，给「删除会话」让位 */
.side-item:hover .side-time {
    opacity: 0;
}

.side-del {
    position: absolute;
    top: 50%;
    right: 8px;
    transform: translateY(-50%);
    width: 24px;
    height: 24px;
    border: 0;
    border-radius: 7px;
    background: none;
    color: var(--text-3);
    cursor: pointer;
    font-size: 12px;
    opacity: 0;
    pointer-events: none;
    transition: opacity .15s ease;
}

.side-item:hover .side-del {
    opacity: 1;
    pointer-events: auto;
}

.side-del:hover {
    background: rgba(244, 63, 94, .14);
    color: var(--danger);
}

/* ---------------------------------------------------------------- 右：对话区 */
.ai-room {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-width: 0;
    min-height: 0;
}

.room-head {
    flex: none;
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 12px 18px;
    border-bottom: 1px solid var(--line);
    background: rgba(20, 28, 46, .35);
}

.room-title {
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
}

.room-title i {
    color: var(--accent);
    font-size: 15px;
}

.room-name {
    max-width: 320px;
    font-size: 15px;
    font-weight: 600;
    color: var(--text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.room-model {
    min-width: 0;
    font-size: 11.5px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.room-tools {
    margin-left: auto;
    flex: none;
    display: flex;
    align-items: center;
    gap: 8px;
}

.tool-label {
    font-size: 11.5px;
    color: var(--text-2);
}

.run-bar {
    flex: none;
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 7px 18px;
    background: rgba(14, 165, 233, .08);
    border-bottom: 1px solid var(--line);
    font-size: 12.5px;
    color: var(--accent-2);
}

.run-spin {
    width: 12px;
    height: 12px;
    flex: none;
    border-radius: 50%;
    border: 2px solid rgba(148, 163, 184, .35);
    border-top-color: var(--accent);
    animation: ai-spin .8s linear infinite;
}

@keyframes ai-spin {
    to {
        transform: rotate(360deg);
    }
}

.run-meta {
    color: var(--text-3);
}

.run-action {
    margin-left: auto;
}

.room-stream {
    position: relative;
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 14px 18px 16px;
}

.stream-empty {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 8px;
    padding: 16vh 0;
    text-align: center;
}

.stream-empty i {
    font-size: 30px;
    color: var(--text-3);
}

.empty-title {
    font-size: 14px;
    color: var(--text-2);
}

.empty-tip {
    font-size: 12px;
    color: var(--text-3);
}

.msg-row {
    display: flex;
    align-items: flex-start;
    gap: 8px;
    margin-bottom: 10px;
}

.msg-row.mine {
    flex-direction: row-reverse;
}

.msg-row.status {
    justify-content: center;
}

.msg-row.block {
    display: block;
}

.status-line {
    padding: 2px 0;
    font-size: 11.5px;
    color: var(--text-3);
}

.avatar {
    width: 34px;
    height: 34px;
    flex: none;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 11.5px;
    font-weight: 700;
    background: linear-gradient(135deg, var(--accent), #6366F1);
    color: #04121B;
}

.bubble {
    position: relative;
    max-width: min(76%, 860px);
    padding: 9px 12px;
    border-radius: 16px;
    border-bottom-left-radius: 4px;
    background: var(--bubble-bot, #1E293B);
    font-size: 14px;
    line-height: 1.65;
    word-break: break-word;
}

.msg-row.mine .bubble {
    background: var(--bubble-me, var(--accent-weak));
    border-bottom-left-radius: 16px;
    border-bottom-right-radius: 4px;
}

.bubble.error {
    border: 1px solid var(--danger);
    background: rgba(244, 63, 94, .08);
}

.bubble-time {
    margin-top: 4px;
    font-size: 10.5px;
    text-align: right;
    color: rgba(148, 163, 184, .75);
}

/* v-html 出来的节点没有 scoped 属性，必须用 :deep() 穿透 */
.md-text :deep(p) {
    margin: 0 0 6px;
    white-space: pre-wrap;
}

.md-text :deep(p:last-child) {
    margin-bottom: 0;
}

.md-text :deep(code) {
    padding: 1px 5px;
    border-radius: 5px;
    background: rgba(148, 163, 184, .16);
    font-family: Consolas, "Segoe UI", monospace;
    font-size: 12.5px;
    color: var(--accent-2);
}

.code-block {
    margin: 6px 0;
    padding: 10px 12px;
    border: 1px solid var(--line);
    border-radius: 10px;
    background: #0F172A;
    overflow-x: auto;
    white-space: pre;
    font-family: Consolas, "Segoe UI", monospace;
    font-variant-numeric: tabular-nums;
    font-size: 12px;
    line-height: 1.6;
    color: var(--text);
}

/* ---------------------------------------------------------------- 卡片（提案 / 试运行结果） */
.card {
    padding: 12px 14px;
    border: 1px solid var(--line);
    border-left: 3px solid var(--accent);
    border-radius: var(--radius);
    background: linear-gradient(180deg, var(--card) 0%, var(--card-2) 100%);
}

.card-head {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
}

.card-head>i {
    color: var(--accent);
}

.card-name {
    font-size: 13.5px;
    font-weight: 600;
    color: var(--text);
}

.card-head-action {
    margin-left: auto;
}

.card-summary {
    margin-top: 8px;
    font-size: 13px;
    line-height: 1.65;
    color: var(--text-2);
    white-space: pre-wrap;
    word-break: break-word;
}

.card-warn {
    margin-top: 8px;
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    color: var(--danger);
}

.card-actions {
    margin-top: 10px;
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
}

.applied {
    font-size: 12.5px;
    color: var(--ok);
}

.result-log {
    margin: 8px 0 0;
    padding: 10px 12px;
    max-height: 220px;
    overflow: auto;
    border: 1px solid var(--line);
    border-radius: 10px;
    background: #0F172A;
    white-space: pre-wrap;
    word-break: break-all;
    font-family: Consolas, "Segoe UI", monospace;
    font-size: 12px;
    line-height: 1.6;
    color: var(--text-2);
}

/* ---------------------------------------------------------------- 执行过程（会话流内联块） */
.steps-card {
    margin-bottom: 10px;
    border: 1px solid var(--line);
    border-left: 3px solid var(--accent);
    border-radius: var(--radius);
    background: linear-gradient(180deg, var(--card) 0%, var(--card-2) 100%);
}

.steps-card.live {
    border-left-color: var(--ok);
}

.steps-card-head {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 7px 12px;
    cursor: pointer;
    font-size: 12.5px;
    color: var(--text-2);
    user-select: none;
}

.steps-card-head:hover {
    color: var(--text);
}

.steps-card-head>i:first-child {
    color: var(--accent);
}

.steps-spin {
    width: 11px;
    height: 11px;
    flex: none;
}

.steps-card-title {
    flex: 1;
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.steps-card-head>i:last-child {
    flex: none;
    font-size: 11px;
}

.steps-hint {
    flex: none;
    font-size: 11.5px;
    color: var(--text-3);
}

.steps-card-body {
    max-height: 260px;
    overflow-y: auto;
    padding: 2px 12px 10px;
    border-top: 1px solid var(--line);
}

.steps-empty {
    padding: 6px 0;
    font-size: 12px;
    color: var(--text-3);
}

.step-row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 3px 0;
    font-size: 12px;
}

.step-row i {
    width: 14px;
    color: var(--text-3);
}

.step-name {
    color: var(--text-2);
    white-space: nowrap;
}

.step-time {
    white-space: nowrap;
}

.step-preview {
    flex: 1;
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

/* ---------------------------------------------------------------- 输入条 */
.room-composer {
    flex: none;
    padding: 8px 18px 10px;
    border-top: 1px solid var(--line);
    background: var(--bg);
}

.composer-inner {
    display: flex;
    align-items: flex-end;
    gap: 8px;
    padding: 4px 6px 4px 0;
    border: 1px solid var(--line-2);
    border-radius: 14px;
    background: var(--chat-input, #1C2841);
}

.composer-inner.focused {
    border-color: var(--accent);
    box-shadow: 0 0 0 3px rgba(14, 165, 233, .14);
}

.composer-inner textarea {
    flex: 1;
    min-height: 40px;
    max-height: 140px;
    padding: 10px 0 10px 14px;
    border: 0;
    outline: none;
    resize: none;
    background: none;
    color: var(--text);
    font-family: inherit;
    font-size: 14px;
    line-height: 1.6;
}

.composer-inner textarea::placeholder {
    color: rgba(148, 163, 184, .65);
}

.composer-meta {
    margin-top: 6px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
}

.hint {
    font-size: 11.5px;
    color: var(--text-3);
}

/* ---------------------------------------------------------------- 弹层 */
.contract-tip {
    margin-bottom: 10px;
    font-size: 12px;
    color: var(--text-3);
}

.contract-box {
    max-height: 60vh;
    overflow: auto;
    padding: 12px;
    background: var(--card-2);
    border: 1px solid var(--line);
    border-radius: var(--radius);
}

.contract-box pre {
    margin: 0;
    white-space: pre-wrap;
    word-break: break-word;
    font-family: Consolas, "Segoe UI", monospace;
    font-size: 12px;
    line-height: 1.6;
    color: var(--text-2);
}

/* 提案 diff：样式与脚本版本页 .diff-* 同套 */
.diff-box {
    max-height: 56vh;
    overflow: auto;
    padding: 4px 0;
    background: var(--card-2);
    border: 1px solid var(--line);
    border-radius: var(--radius);
}

.diff-row {
    display: flex;
    align-items: flex-start;
    font-size: 12px;
    line-height: 1.6;
    white-space: pre-wrap;
    word-break: break-all;
}

.diff-no {
    flex: 0 0 52px;
    text-align: right;
    padding-right: 8px;
    color: var(--text-3);
    user-select: none;
    font-family: Consolas, "Segoe UI", monospace;
}

.diff-sign {
    flex: 0 0 14px;
    text-align: center;
    user-select: none;
}

.diff-text {
    flex: 1;
    padding-right: 12px;
}

.diff-add {
    background: rgba(14, 165, 233, 0.12);
}

.diff-add .diff-sign {
    color: var(--ok);
}

.diff-del {
    background: rgba(239, 68, 68, 0.12);
}

.diff-del .diff-sign {
    color: var(--danger);
}

.diff-more,
.diag-item {
    padding: 4px 12px;
    color: var(--text-3);
    font-size: 12px;
}

.diag-list {
    max-height: 40vh;
    overflow: auto;
    padding: 6px 0;
    background: var(--card-2);
    border: 1px solid var(--line);
    border-radius: var(--radius);
}

:deep(.pill) {
    white-space: nowrap;
}
</style>
