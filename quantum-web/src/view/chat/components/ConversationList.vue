<template>
    <aside class="conversation-list">
        <div class="list-head">
            <div class="list-title">
                <h3>会话</h3>
                <span class="count">{{ sessions.length }} 个</span>
            </div>
            <n-input v-model:value="keyword" size="small" clearable placeholder="筛选会话 / 消息内容">
                <template #prefix>
                    <i class="fa-solid fa-magnifying-glass"></i>
                </template>
            </n-input>
        </div>
        <div class="list-body">
            <div v-if="!filtered.length" class="list-empty">暂无会话消息</div>
            <div v-for="item in filtered" :key="item.SessionKey || 'default'" class="conversation"
                :class="{ active: item.SessionKey === currentKey }" @click="$emit('select', item.SessionKey || '')">
                <div class="conv-icon" :class="{ task: !!item.SessionKey }">
                    <i :class="item.SessionKey ? 'fa-solid fa-list-check' : 'fa-regular fa-comments'"></i>
                </div>
                <div class="conv-body">
                    <div class="conv-title">{{ titleOf(item) }}</div>
                    <div class="conv-preview">{{ previewOf(item) }}</div>
                </div>
                <div class="conv-side">
                    <span class="conv-time">{{ timeOf(item) }}</span>
                    <span v-if="item.Unread > 0" class="conv-unread">{{ item.Unread > 99 ? '99+' : item.Unread }}</span>
                </div>
                <n-popconfirm placement="left-end" positive-text="删除" negative-text="取消"
                    @positive-click="$emit('delete', item.SessionKey || '')">
                    <template #trigger>
                        <button class="conv-delete" type="button" title="删除会话" @click.stop>
                            <i class="fa-solid fa-trash-can"></i>
                        </button>
                    </template>
                    删除后该会话从列表移除（消息记录保留），任务再次推送消息时会自动重建
                </n-popconfirm>
            </div>
        </div>
    </aside>
</template>

<script>
/**
 * 会话列表（对齐 App ConversationListScreen）：
 * 44px 圆图标（默认会话/任务会话两色）、标题、预览、时间、未读角标（>99 显示 99+）。
 * 标题取任务名映射（会话键 = 任务 Id 或任务配置的会话名），映射不到显示会话键原文；预览规则见 chatFormat.sessionPreview。
 * Web 增强：顶部筛选框（按标题/预览本地过滤）；悬浮显示「删除会话」（时间/未读侧淡出、垃圾桶淡入，确认后 emit delete）。
 */
import { sessionTitle } from '@/utils/chatFormat'

export default {
    name: 'ConversationList',
    props: {
        sessions: {
            type: Array,
            default: () => []
        },
        taskMap: {
            type: Object,
            default: () => ({})
        },
        currentKey: {
            type: String,
            default: ''
        }
    },
    emits: ['select', 'delete'],
    data() {
        return {
            keyword: ''
        }
    },
    computed: {
        filtered() {
            const keyword = this.keyword.trim().toLowerCase()
            if (!keyword) {
                return this.sessions
            }
            return this.sessions.filter((item) => {
                const text = `${this.titleOf(item)} ${this.previewOf(item)}`.toLowerCase()
                return text.includes(keyword)
            })
        }
    },
    methods: {
        titleOf(item) {
            return sessionTitle(item.SessionKey, this.taskMap)
        },
        previewOf(item) {
            // §3-10：预览纯文本已在 store 建行/改行时算好存 previewText，模板直读、不再逐行 parseRich
            return item.previewText || '（暂无内容）'
        },
        timeOf(item) {
            return item.Last?.CreateTime || ''
        }
    }
}
</script>

<style scoped>
.conversation-list {
    width: 292px;
    flex: none;
    display: flex;
    flex-direction: column;
    border-right: 1px solid var(--line);
    background: rgba(20, 28, 46, .45);
    min-height: 0;
}

.list-head {
    flex: none;
    padding: 14px 14px 10px;
}

.list-title {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 10px;
}

.list-title h3 {
    font-size: 16px;
    font-weight: 700;
    margin: 0;
    color: var(--text);
}

.count {
    margin-left: auto;
    font-size: 11.5px;
    color: var(--text-2);
}

.list-body {
    flex: 1;
    overflow-y: auto;
    padding: 2px 10px 10px;
}

.list-empty {
    text-align: center;
    color: var(--text-3);
    font-size: 12.5px;
    padding: 24px 0;
}

.conversation {
    position: relative;
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 10px;
    border-radius: 12px;
    cursor: pointer;
    border: 1px solid transparent;
}

.conversation:hover {
    background: rgba(148, 163, 184, .07);
}

.conversation.active {
    background: var(--accent-weak);
    border-color: rgba(14, 165, 233, .28);
}

.conv-icon {
    width: 44px;
    height: 44px;
    flex: none;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 17px;
    background: rgba(14, 165, 233, .15);
    color: var(--accent);
}

.conv-icon.task {
    background: rgba(99, 102, 241, .16);
    color: #A5B4FC;
}

.conv-body {
    flex: 1;
    min-width: 0;
}

.conv-title {
    font-size: 14px;
    font-weight: 600;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--text);
}

.conv-preview {
    margin-top: 2px;
    font-size: 12px;
    color: var(--text-2);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.conv-side {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    gap: 4px;
    flex: none;
    transition: opacity .15s ease;
}

/* 悬浮时时间/未读侧淡出，给「删除会话」让位 */
.conversation:hover .conv-side {
    opacity: 0;
}

/* 删除会话（悬浮显示）：定位在会话项右侧，默认不可见也不拦截点击 */
.conv-delete {
    position: absolute;
    top: 50%;
    right: 10px;
    transform: translateY(-50%);
    width: 26px;
    height: 26px;
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

.conversation:hover .conv-delete {
    opacity: 1;
    pointer-events: auto;
}

.conv-delete:hover {
    background: rgba(244, 63, 94, .14);
    color: var(--danger);
}

.conv-time {
    font-size: 11px;
    color: var(--text-2);
    white-space: nowrap;
}

.conv-unread {
    min-width: 18px;
    height: 18px;
    padding: 0 6px;
    border-radius: 9px;
    background: var(--danger);
    color: #fff;
    font-size: 11px;
    font-weight: 700;
    line-height: 18px;
    text-align: center;
}
</style>
