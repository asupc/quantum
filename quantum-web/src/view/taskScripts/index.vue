<style scoped>
.container {
    height: 100%;
}

.split-container {
    /* 布局纵向预算：顶栏 56 + 标签栏 38 + 内容区上下 padding 36 */
    height: calc(100vh - 130px);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    overflow: hidden;
    background: var(--card);
}

.file-tree {
    height: 100%;
    overflow-y: auto;
    padding: 12px 6px;
}

.editor-container {
    height: 100%;
    position: relative;
    display: flex;
    flex-direction: column;
    border-left: 1px solid var(--line);
}

.editor-header {
    padding: 8px 16px;
    background: var(--card-2);
    border-bottom: 1px solid var(--line);
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex: none;
}

.file-info {
    font-size: 14px;
    color: var(--text-2);
}

.file-name {
    font-weight: 500;
    color: var(--text);
}

.language-tag {
    background: var(--accent-weak);
    color: var(--accent);
    padding: 2px 8px;
    border-radius: 3px;
    font-size: 12px;
    margin-left: 8px;
}

.editor {
    width: 100%;
    flex: 1;
    min-height: 0;
}
</style>

<template>
    <div class="container">
        <div class="split-container">
            <n-split v-model:size="split" direction="horizontal" :min="0.12" :max="0.3">
                <template #1>
                    <div class="file-tree">
                        <n-tree block-line :data="treeOptions" key-field="key" label-field="label"
                            children-field="children" :selected-keys="selectedKeys"
                            @update:selected-keys="treeNodeSelectChange" :render-prefix="renderPrefix" />
                    </div>
                </template>
                <template #2>
                    <div class="editor-container">
                        <div class="editor-header">
                            <div class="file-info">
                                <span class="file-name">{{ fileName || '未选择文件' }}</span>
                                <span v-if="fileName" class="language-tag">{{ getLanguageDisplay }}</span>
                            </div>
                            <n-space>
                                <n-button size="small" @click="openVersions" :disabled="!fileName">
                                    <template #icon>
                                        <i class="fa-solid fa-clock-rotate-left"></i>
                                    </template>
                                    历史版本
                                </n-button>
                                <n-button size="small" @click="deleteFile" :disabled="!content || !fileName"
                                    type="error">
                                    <template #icon>
                                        <i class="fa-solid fa-trash-can"></i>
                                    </template>
                                    删除
                                </n-button>
                                <n-button size="small" @click="saveFile" :disabled="!content || !fileName"
                                    type="primary">
                                    <template #icon>
                                        <i class="fa-solid fa-check"></i>
                                    </template>
                                    保存
                                </n-button>
                            </n-space>
                        </div>
                        <monaco-editor ref="editorComp" :content="content" :language="getLanguage" :readOnly="false"
                            class="editor" @save-file="saveFile" @update:content="content = $event"
                            @mounted="onEditorMounted" />
                        <n-modal v-model:show="showDiagnostics" preset="card" title="保存诊断清单" class="diag-modal"
                            :style="{ width: '640px' }">
                            <n-alert v-for="(item, index) in diagnostics.blocked" :key="'b' + index" type="error"
                                :show-icon="true" style="margin-bottom: 8px">
                                安全拦截 · 第 {{ item.Line }} 行：{{ item.Message }}
                            </n-alert>
                            <n-alert v-for="(item, index) in diagnostics.errors" :key="'e' + index" type="error"
                                :show-icon="true" style="margin-bottom: 8px">
                                编译错误 · 第 {{ item.Line }} 行：{{ item.Message }}
                            </n-alert>
                            <n-alert v-for="(item, index) in diagnostics.warnings" :key="'w' + index" type="warning"
                                :show-icon="true" style="margin-bottom: 8px">
                                提示 · 第 {{ item.Line }} 行：{{ item.Message }}
                            </n-alert>
                            <n-empty v-if="!diagnostics.blocked.length && !diagnostics.errors.length && !diagnostics.warnings.length"
                                description="无诊断信息" />
                            <template #footer>
                                <n-space justify="end">
                                    <n-button size="small" @click="showDiagnostics = false">关闭</n-button>
                                </n-space>
                            </template>
                        </n-modal>
                    </div>
                </template>
            </n-split>
        </div>
    </div>
</template>

<script>
import { h } from 'vue'
import {
    GetScripts,
    UpdateScripts,
    DeleteScripts,
    ScriptList
} from '@/api/task.js'
import MonacoEditor from '@/view/components/monacoEditor.vue'

export default {
    name: 'task-scripts',
    data() {
        return {
            split: 0.18,
            treeDatas: [],
            content: '',
            Languages: {
                '.cs': 'csharp',
                '.py': 'python',
                '.js': 'javascript',
                '.json': 'json',
                '.sh': 'shell',
                '.ts': 'typescript'
            },
            LanguageDisplays: {
                csharp: 'C#',
                python: 'Python',
                javascript: 'JavaScript',
                json: 'JSON',
                shell: 'Shell',
                typescript: 'TypeScript'
            },
            fileName: '',
            selectedKeys: [],
            // 保存诊断（三类：blocked 安全拦截 / errors 编译错误 / warnings 提示）
            showDiagnostics: false,
            diagnostics: { blocked: [], errors: [], warnings: [] },
            // 文件切换请求序号：丢弃乱序返回的旧文件响应，防止旧内容覆盖新选中文件状态
            fileSeq: 0
        }
    },
    computed: {
        getLanguage() {
            if (this.fileName) {
                var ss = this.fileName.split('.')
                return this.Languages['.' + ss[ss.length - 1]]
            }
            return 'csharp'
        },
        getLanguageDisplay() {
            const lang = this.getLanguage
            return this.LanguageDisplays[lang] || lang
        },
        treeOptions() {
            // 后端树节点 {title, path, children} → n-tree {key, label, children, isLeaf}
            // 目录节点恒带 children 数组（可能为空），文件节点无 children 且带 path；
            // 空目录若按 length 判断会被误渲染成文件（本页最初的文件夹识别 bug）
            const map = (nodes) =>
                (nodes || []).map((n) => {
                    const isDir = Array.isArray(n.children)
                    return {
                        key: n.path || n.title,
                        label: n.title,
                        isFile: !isDir,
                        isLeaf: !isDir,
                        children: isDir ? map(n.children) : undefined
                    }
                })
            return map(this.treeDatas)
        }
    },
    components: {
        MonacoEditor
    },
    methods: {
        renderPrefix({ option }) {
            return h('i', {
                class: option.isLeaf ? 'fa-regular fa-file-lines' : 'fa-solid fa-folder',
                style: {
                    marginRight: '4px',
                    color: option.isLeaf ? 'var(--text-2)' : 'var(--warn)'
                }
            })
        },
        getScriptList() {
            ScriptList().then((res) => {
                this.treeDatas = res
                // 支持从外触内执页跳转直达（sessionStorage 传参，用后即清）
                const target = sessionStorage.getItem('openScriptFile')
                if (target) {
                    sessionStorage.removeItem('openScriptFile')
                    this.openFile(String(target))
                }
            })
        },
        openFile(path) {
            const seq = ++this.fileSeq
            this.fileName = path
            this.selectedKeys = [path]
            this.getFile(seq)
        },
        getFile(seq) {
            if (!this.fileName) {
                return
            }
            GetScripts({
                fileName: this.fileName
            }).then((res) => {
                // 乱序防护：响应返回时若已切换到其它文件，丢弃本次结果
                if (seq !== this.fileSeq) return
                this.content = res
                this.$nextTick(() => {
                    this.$refs.editorComp && this.$refs.editorComp.setContent(res)
                })
            }).catch((error) => {
                if (seq !== this.fileSeq) return
                this.$notification.error({
                    title: '加载文件失败',
                    content: String((error && error.message) || error || '无法加载所选文件'),
                    duration: 4000
                })
            })
        },
        deleteFile() {
            var that = this
            this.$dialog.warning({
                title: '脚本删除确认',
                content: `确认删除该脚本文件 "${this.fileName}" 吗？此操作不可恢复！`,
                positiveText: '确定删除',
                negativeText: '取消',
                onPositiveClick: function () {
                    DeleteScripts(that.fileName)
                        .then((res) => {
                            that.getScriptList()
                            that.content = ''
                            that.fileName = ''
                            that.selectedKeys = []
                            that.$notification.success({
                                title: '成功',
                                content: '删除脚本文件成功',
                                duration: 3000
                            })
                        })
                        .catch(function (error) {
                            that.$notification.error({
                                title: '删除脚本文件失败',
                                content: String((error && error.message) || error || '未知错误'),
                                duration: 4000
                            })
                        })
                }
            })
        },
        // 跳转脚本版本页并定位当前文件（版本页读 route.query.file 自动筛选）
        openVersions() {
            if (!this.fileName) {
                return
            }
            this.$router.push({ path: '/task/script-versions', query: { file: this.fileName } })
        },
        saveFile() {
            if (!this.fileName) {
                return
            }

            var that = this
            let trimmedStr = that.content.replace(/[\r\n]+$/, '')
            UpdateScripts({
                path: that.fileName,
                content: trimmedStr
            })
                .then((res) => {
                    // 保存流水线返回三类诊断（保存失败时内容不落盘，编辑区内容保留）
                    if (res && res.Success) {
                        that.$notification.success({
                            title: '成功',
                            content: '保存脚本文件成功（已通过安全门禁并编译缓存）',
                            duration: 3000
                        })
                        if ((res.Warnings || []).length) {
                            that.diagnostics = { blocked: [], errors: [], warnings: res.Warnings }
                            that.showDiagnostics = true
                        }
                    } else {
                        that.diagnostics = {
                            blocked: res && res.Blocked ? res.Blocked : [],
                            errors: res && res.Errors ? res.Errors : [],
                            warnings: res && res.Warnings ? res.Warnings : []
                        }
                        that.showDiagnostics = true
                        that.$notification.error({
                            title: '保存被拒绝',
                            content: '脚本未通过保存检查（内容未落盘，请按诊断清单修改后重试）',
                            duration: 4000
                        })
                    }
                })
                .catch(function (error) {
                    that.$notification.error({
                        title: '保存脚本文件失败',
                        content: String((error && error.message) || error || '未知错误'),
                        duration: 4000
                    })
                })
        },
        onEditorMounted(editor, monaco) {
            window.editor = editor
            window.monaco = monaco
        },
        treeNodeSelectChange(keys) {
            const key = keys && keys[0]
            if (!key) {
                return
            }
            // 按key回查节点：仅文件节点（带 path）触发打开，目录点击只做展开
            const findNode = (nodes) => {
                for (const n of nodes || []) {
                    if (n.key === key) return n
                    const hit = findNode(n.children)
                    if (hit) return hit
                }
                return null
            }
            const node = findNode(this.treeOptions)
            if (node && node.isFile) {
                this.openFile(key)
            }
        }
    },
    mounted() {
        this.getScriptList()
    },
    beforeUnmount() {
        // §3-14：编辑器 api 闭包持有本组件实例，卸载时清全局引用防泄漏（monaco 降级为 textarea 后这两个全局仅供调试）
        window.editor = null
        window.monaco = null
    }
}
</script>
