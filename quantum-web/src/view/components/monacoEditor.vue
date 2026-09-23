<template>
    <div class="code-editor-shell">
        <textarea ref="ta" class="code-area" spellcheck="false" :value="content" :readonly="readOnly"
            @input="onInput" @keydown="onKeydown"></textarea>
    </div>
</template>

<script>
// 代码编辑器（textarea 方案）
// 说明：monaco-editor（ESM/内联 worker/AMD loader 多种形态、0.52/0.56 双版本）在本环境
// 生产构建下存在无法稳定复现定位的随机主线程自旋（最小用例均不复现），为保证脚本
// 编辑/保存链路稳定交付，降级为等宽 textarea；对外 API 保持不变（content/setValue/getValue/
// save-file 事件），后续如需语法高亮可在不变更接口的前提下替换实现。
export default {
    name: "monacoEditor",
    props: {
        content: {
            type: String,
            default: ""
        },
        language: {
            type: String,
            default: "javascript"
        },
        readOnly: {
            type: Boolean,
            default: false
        },
        editorMounted: {
            type: Function,
            default: null
        }
    },
    emits: ["update:content", "save-file", "on-cursor-change", "on-jsonpath-change", "mounted"],
    computed: {
        api() {
            const me = this;
            return {
                setValue: (v) => me.setContent(v),
                getValue: () => me.getValue()
            };
        }
    },
    mounted: function () {
        if (this.editorMounted) {
            this.editorMounted(this.api, null);
        }
        this.$emit("mounted", this.api, null);
    },
    methods: {
        onInput(e) {
            this.$emit("update:content", e.target.value);
        },
        onKeydown(e) {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
                e.preventDefault();
                this.$emit("save-file");
                return;
            }
            // Tab 键插入两个空格，保持代码编辑体验
            if (e.key === "Tab") {
                e.preventDefault();
                const ta = this.$refs.ta;
                const start = ta.selectionStart;
                const end = ta.selectionEnd;
                const value = ta.value;
                ta.value = value.slice(0, start) + "  " + value.slice(end);
                ta.selectionStart = ta.selectionEnd = start + 2;
                this.$emit("update:content", ta.value);
            }
        },
        // 程序化载入内容（文件打开等场景）
        setContent(newValue) {
            const v = newValue == null ? "" : String(newValue);
            const ta = this.$refs.ta;
            if (ta) {
                ta.value = v;
            }
            this.$emit("update:content", v);
        },
        getValue() {
            const ta = this.$refs.ta;
            return ta ? ta.value : this.content;
        }
    }
};
</script>

<style scoped>
.code-editor-shell {
    width: 100%;
    height: 100%;
    background: #0f1830;
    border: 1px solid var(--line-2);
    border-radius: 6px;
    overflow: hidden;
}

.code-area {
    width: 100%;
    height: 100%;
    box-sizing: border-box;
    background: #0f1830;
    color: #d6deeb;
    caret-color: var(--accent);
    border: none;
    outline: none;
    resize: none;
    padding: 12px 14px;
    font-family: Consolas, "Courier New", monospace;
    font-size: 13px;
    line-height: 1.6;
    tab-size: 4;
    white-space: pre;
    overflow: auto;
}

.code-area::selection {
    background: rgba(34, 211, 238, .25);
}
</style>
