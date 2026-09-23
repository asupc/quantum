<template>
    <header class="chat-header">
        <button v-if="showBack" class="back" type="button" title="返回会话列表" @click="$emit('back')">
            <i class="fa-solid fa-arrow-left"></i>
        </button>
        <div class="head-main">
            <div class="head-title">{{ title }}</div>
            <div class="head-meta">
                会话键：<span class="mono">{{ sessionKey || '（空 = 默认）' }}</span>
                <span v-if="total"> · {{ total }} 条消息</span>
            </div>
        </div>
        <div class="head-actions">
            <div class="ws-state" :class="wsState" :title="wsDetail || '实时连接状态'">
                <span class="dot"></span>{{ wsLabel }}
            </div>
            <n-popover v-model:show="searching" trigger="click" placement="bottom-end" style="width: 420px">
                <template #trigger>
                    <button class="icon-btn" type="button" title="搜索消息">
                        <i class="fa-solid fa-magnifying-glass"></i>
                    </button>
                </template>
                <div class="search-panel">
                    <n-input v-model:value="keyword" size="small" placeholder="按关键字检索会话消息（回车搜索）"
                        :loading="searchLoading" @keyup.enter="doSearch" clearable />
                    <div class="search-results">
                        <div v-if="searchDone && !results.length" class="search-empty">没有匹配的消息</div>
                        <div v-for="item in results" :key="item.Id" class="search-item"
                            @click="openResult(item)">
                            <div class="search-item-head">
                                <span class="search-session">{{ titleOf(item.SessionKey) }}</span>
                                <span class="search-time">{{ item.CreateTime }}</span>
                            </div>
                            <div class="search-text">{{ preview(item) }}</div>
                        </div>
                    </div>
                </div>
            </n-popover>
            <button class="icon-btn" type="button" title="刷新会话列表" @click="$emit('refresh')">
                <i class="fa-solid fa-rotate"></i>
            </button>
            <button class="icon-btn danger" type="button" title="删除会话（消息记录保留）" @click="confirmDelete">
                <i class="fa-solid fa-trash-can"></i>
            </button>
        </div>
    </header>
</template>

<script>
/**
 * 会话顶栏：标题（任务名/会话名映射）、实时连接状态、消息搜索、刷新、删除会话。
 * 2026-09-21 日志删除逻辑调整：删除会话只删会话数据与会话窗口（与列表项删除同语义），
 * 消息记录（业务真实产生的数据）服务端保留——消息删除入口只有日志中心；
 * 搜索走 api/App/history（App 侧该接口未接线，Web 补齐）。
 */
import { History } from '@/api/app'
import { sessionTitle, sessionPreview, stripMarkup } from '@/utils/chatFormat'

export default {
    name: 'ChatHeader',
    props: {
        sessionKey: {
            type: String,
            default: ''
        },
        title: {
            type: String,
            default: '默认会话'
        },
        total: {
            type: Number,
            default: 0
        },
        wsState: {
            type: String,
            default: 'closed'
        },
        wsDetail: {
            type: String,
            default: ''
        },
        showBack: {
            type: Boolean,
            default: false
        },
        taskMap: {
            type: Object,
            default: () => ({})
        }
    },
    emits: ['refresh', 'delete', 'back', 'open-session'],
    data() {
        return {
            searching: false,
            keyword: '',
            results: [],
            searchLoading: false,
            searchDone: false
        }
    },
    computed: {
        wsLabel() {
            return { open: '实时已连接', connecting: '连接中…', closed: '离线（仅补拉）' }[this.wsState] || '离线'
        }
    },
    methods: {
        confirmDelete() {
            window.$dialog.warning({
                title: '删除会话',
                content: `将删除「${this.title}」会话与窗口显示，消息记录保留在服务器（删除消息记录请前往日志中心），任务再次推送时自动重建。`,
                positiveText: '删除',
                negativeText: '取消',
                onPositiveClick: () => this.$emit('delete')
            })
        },
        async doSearch() {
            const keyword = this.keyword.trim()
            if (!keyword) {
                this.results = []
                this.searchDone = false
                return
            }
            this.searchLoading = true
            try {
                const res = await History(keyword, 1, 20)
                this.results = Array.isArray(res) ? res : []
                this.searchDone = true
            } finally {
                this.searchLoading = false
            }
        },
        titleOf(sessionKey) {
            return sessionTitle(sessionKey || '', this.taskMap)
        },
        preview(item) {
            const text = stripMarkup(item.ContentText || item.Content || '')
            return text.length > 80 ? `${text.slice(0, 80)}…` : text
        },
        openResult(item) {
            this.searching = false
            this.$emit('open-session', item.SessionKey || '')
        }
    }
}
</script>

<style scoped>
.chat-header {
    height: 60px;
    flex: none;
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 0 16px;
    border-bottom: 1px solid var(--line);
}

.back {
    width: 30px;
    height: 30px;
    flex: none;
    border: 0;
    border-radius: 8px;
    background: none;
    color: var(--text-2);
    cursor: pointer;
    font-size: 14px;
}

.back:hover {
    background: rgba(148, 163, 184, .1);
    color: var(--text);
}

.head-main {
    min-width: 0;
}

.head-title {
    font-size: 15px;
    font-weight: 700;
    color: var(--text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.head-meta {
    font-size: 11.5px;
    color: var(--text-3);
    margin-top: 1px;
}

.head-actions {
    margin-left: auto;
    display: flex;
    align-items: center;
    gap: 6px;
}

.ws-state {
    display: flex;
    align-items: center;
    gap: 5px;
    padding: 0 6px;
    font-size: 11.5px;
    color: var(--text-3);
}

.ws-state .dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--text-3);
}

.ws-state.open .dot {
    background: var(--ok);
    box-shadow: 0 0 8px var(--ok);
}

.ws-state.connecting .dot {
    background: var(--warn);
}

.ws-state.closed .dot {
    background: var(--danger);
}

.icon-btn {
    width: 32px;
    height: 32px;
    border: 0;
    border-radius: 8px;
    background: none;
    color: var(--text-2);
    cursor: pointer;
    font-size: 13px;
}

.icon-btn:hover {
    background: rgba(148, 163, 184, .10);
    color: var(--text);
}

.icon-btn.danger:hover {
    background: rgba(244, 63, 94, .14);
    color: var(--danger);
}

.search-panel {
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.search-results {
    max-height: 340px;
    overflow-y: auto;
}

.search-empty {
    padding: 14px 0;
    text-align: center;
    color: var(--text-3);
    font-size: 12.5px;
}

.search-item {
    padding: 8px 6px;
    border-radius: 8px;
    cursor: pointer;
}

.search-item:hover {
    background: rgba(148, 163, 184, .10);
}

.search-item-head {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 11.5px;
    color: var(--text-3);
}

.search-time {
    margin-left: auto;
}

.search-text {
    margin-top: 2px;
    font-size: 13px;
    color: var(--text);
    word-break: break-word;
}
</style>
