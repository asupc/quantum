<template>
    <div class="composer">
        <div class="composer-inner" :class="{ focused }">
            <textarea ref="input" v-model="draft" rows="1" :placeholder="placeholder" @focus="focused = true"
                @blur="focused = false" @keydown="onKeydown" @input="autoGrow"></textarea>
            <button class="plus" :class="{ open: panelOpen }" type="button" title="附件与指令" @click="togglePanel">
                <i class="fa-solid fa-plus"></i>
            </button>
        </div>

        <div v-show="panelOpen" class="attach-panel">
            <div class="attach-item" @click="pickFile('image')">
                <span><i class="fa-regular fa-image"></i></span>相册
            </div>
            <div class="attach-item" @click="pickFile('file')">
                <span><i class="fa-regular fa-file-lines"></i></span>文件
            </div>
            <div class="attach-item" :class="{ active: showCommands }" @click="toggleCommands">
                <span><i class="fa-solid fa-terminal"></i></span>指令
            </div>
        </div>

        <div v-show="panelOpen && showCommands" class="command-panel">
            <div class="command-head">快捷指令（api/Command）</div>
            <div v-if="!commands.length" class="command-empty">暂无快捷指令</div>
            <div v-for="item in commands" :key="item.Id || item.Key" class="command-item" @click="sendCommand(item)">
                <div class="command-key">{{ item.Key }}</div>
                <div class="command-message">{{ item.Message }}</div>
            </div>
            <div class="command-tip">多步任务进行中可发送「Q」退出</div>
        </div>

        <!-- 附件发送预览（图片/文件 + 可选配文），与 App 的待发送预览弹窗同构 -->
        <n-modal v-model:show="previewVisible" preset="card" style="width: 460px" :title="previewTitle">
            <div class="preview-body">
                <img v-if="previewKind === 'image' && previewUrl" :src="previewUrl" alt="待发送图片" />
                <div v-else class="preview-file">
                    <i class="fa-regular fa-file-lines"></i>
                    <span>{{ previewFile?.name }}</span>
                </div>
                <n-input v-model:value="previewCaption" type="textarea" :rows="3" maxlength="200" show-count
                    placeholder="配文（可选）…" />
            </div>
            <template #footer>
                <div class="preview-footer">
                    <n-button size="small" @click="cancelPreview">取消</n-button>
                    <n-button size="small" type="primary" :loading="uploading" @click="confirmSend">发送</n-button>
                </div>
            </template>
        </n-modal>

        <input ref="imageInput" type="file" accept="image/jpeg,image/png,image/gif,image/webp" hidden
            @change="onFileChange($event, 'image')" />
        <input ref="fileInput" type="file" hidden @change="onFileChange($event, 'file')" />
    </div>
</template>

<script>
/**
 * 输入条（对齐 App 底部输入条 + 附件面板 + 快捷指令）：
 * - Enter 发送 / Shift+Enter 换行（App 为 Enter 发送、最多 4 行；桌面端补 Shift+Enter）
 * - 「+」展开附件面板（相册 / 文件 / 指令），选附件后弹预览填配文再发送
 * - 附件上传：AppUpload/image|file 拿 FileId → 以 ContentType=image/file 提交
 * - 快捷指令来自 api/Command，点按即发送 key
 * 另支持粘贴板图片与拖拽文件（桌面端增强）。
 */
import { GetCommand } from '@/api/command'
import { UploadImage, UploadFile } from '@/api/app'

export default {
    name: 'ChatComposer',
    props: {
        disabled: {
            type: Boolean,
            default: false
        }
    },
    emits: ['send'],
    data() {
        return {
            draft: '',
            focused: false,
            panelOpen: false,
            showCommands: false,
            commands: [],
            previewVisible: false,
            previewKind: 'image',
            previewUrl: '',
            previewCaption: '',
            previewFile: null,
            uploading: false
        }
    },
    computed: {
        placeholder() {
            return this.disabled ? '正在连接…' : '输入指令或消息…（Enter 发送 / Shift+Enter 换行）'
        },
        previewTitle() {
            return this.previewKind === 'image' ? '发送图片' : '发送文件'
        }
    },
    mounted() {
        this.loadCommands()
    },
    methods: {
        autoGrow() {
            const el = this.$refs.input
            if (!el) {
                return
            }
            el.style.height = 'auto'
            el.style.height = `${Math.min(el.scrollHeight, 96)}px`
        },
        onKeydown(event) {
            if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault()
                this.submit()
                return
            }
            if (event.key === 'Escape') {
                this.panelOpen = false
            }
        },
        async submit() {
            const text = this.draft.trim()
            if (!text || this.disabled) {
                return
            }
            this.$emit('send', { content: text })
            // 发送即清空（失败由气泡标红提供重发，与 App 体验一致）
            this.draft = ''
            this.$nextTick(this.autoGrow)
        },
        togglePanel() {
            this.panelOpen = !this.panelOpen
            if (!this.panelOpen) {
                this.showCommands = false
            }
        },
        toggleCommands() {
            this.showCommands = !this.showCommands
        },
        async loadCommands() {
            try {
                const res = await GetCommand({ PageIndex: 1, PageSize: 200 })
                this.commands = res?.Data || []
            } catch (e) {
                // 快捷指令拉取失败不影响输入
            }
        },
        sendCommand(item) {
            this.$emit('send', { content: item.Key })
            this.panelOpen = false
            this.showCommands = false
        },
        pickFile(kind) {
            const ref = kind === 'image' ? this.$refs.imageInput : this.$refs.fileInput
            if (ref) {
                ref.value = ''
                ref.click()
            }
        },
        onFileChange(event, kind) {
            const file = event.target.files?.[0]
            if (!file) {
                return
            }
            this.previewKind = kind
            this.previewFile = file
            this.previewUrl = kind === 'image' ? URL.createObjectURL(file) : ''
            this.previewCaption = ''
            this.previewVisible = true
            this.panelOpen = false
        },
        cancelPreview() {
            this.previewVisible = false
            this.revokePreview()
        },
        revokePreview() {
            if (this.previewUrl) {
                URL.revokeObjectURL(this.previewUrl)
                this.previewUrl = ''
            }
        },
        async confirmSend() {
            if (!this.previewFile || this.uploading) {
                return
            }
            this.uploading = true
            try {
                const upload = this.previewKind === 'image' ? UploadImage : UploadFile
                const res = await upload(this.previewFile)
                const caption = this.previewCaption.trim()
                this.$emit('send', {
                    content: res?.FileId,
                    contentType: this.previewKind,
                    contentText: caption || undefined
                })
                this.previewVisible = false
                this.revokePreview()
                this.previewFile = null
            } catch (e) {
                // 上传失败文案由 axios 拦截器提示
            } finally {
                this.uploading = false
            }
        }
    }
}
</script>

<style scoped>
.composer {
    flex: none;
    border-top: 1px solid var(--line);
    padding: 6px 8px 4px;
    background: var(--bg);
}

.composer-inner {
    display: flex;
    align-items: flex-end;
    gap: 6px;
    padding: 0 6px 0 0;
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
    min-height: 38px;
    max-height: 96px;
    padding: 10px 0 10px 16px;
    border: 0;
    outline: none;
    resize: none;
    background: none;
    color: var(--text);
    font-family: inherit;
    font-size: 14px;
    line-height: 1.55;
}

.composer-inner textarea::placeholder {
    color: rgba(148, 163, 184, .65);
}

.plus {
    width: 36px;
    height: 36px;
    flex: none;
    margin-bottom: 4px;
    border: 0;
    border-radius: 50%;
    background: none;
    color: var(--text-2);
    cursor: pointer;
    transition: transform .18s, background .18s;
}

.plus:hover {
    background: rgba(148, 163, 184, .12);
    color: var(--text);
}

.plus.open {
    background: var(--accent-weak);
    color: var(--accent);
    transform: rotate(45deg);
}

.attach-panel {
    display: flex;
    align-items: center;
    justify-content: space-evenly;
    padding: 10px 12px 4px;
}

.attach-item {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 5px;
    font-size: 11.5px;
    color: var(--text-2);
    cursor: pointer;
}

.attach-item:hover,
.attach-item.active {
    color: var(--accent);
}

.attach-item span {
    width: 40px;
    height: 40px;
    border-radius: 12px;
    background: rgba(148, 163, 184, .10);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 15px;
}

.command-panel {
    max-height: 260px;
    overflow-y: auto;
    margin-top: 8px;
    border: 1px solid var(--line-2);
    border-radius: 12px;
    background: var(--card);
}

.command-head {
    padding: 10px 14px 6px;
    font-size: 12px;
    color: var(--text-2);
}

.command-empty {
    padding: 12px 14px;
    color: var(--text-3);
    font-size: 12.5px;
}

.command-item {
    padding: 8px 14px;
    border-top: 1px solid var(--line);
    cursor: pointer;
}

.command-item:hover {
    background: rgba(148, 163, 184, .07);
}

.command-key {
    font-size: 14px;
    font-weight: 600;
    color: var(--text);
}

.command-message {
    font-size: 12px;
    color: var(--text-2);
    margin-top: 2px;
}

.command-tip {
    padding: 8px 14px 10px;
    border-top: 1px solid var(--line);
    font-size: 11.5px;
    color: var(--text-3);
}

.preview-body {
    display: flex;
    flex-direction: column;
    gap: 12px;
}

.preview-body img {
    max-width: 100%;
    max-height: 260px;
    align-self: flex-start;
    border-radius: 10px;
}

.preview-file {
    display: flex;
    align-items: center;
    gap: 8px;
    color: var(--text-2);
    font-size: 13px;
}

.preview-footer {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
}
</style>
