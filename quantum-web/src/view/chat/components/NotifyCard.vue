<template>
    <div class="notify-card" :class="{ clickable: !!jump }" @click="openJump">
        <div class="notify-head">
            <span class="notify-dot" :style="{ background: categoryColor }"></span>
            <span class="notify-category" :style="{ color: categoryColor }">{{ categoryLabel }}</span>
            <span class="notify-time">{{ timeText }}</span>
        </div>
        <div v-if="title" class="notify-title">{{ title }}</div>
        <div class="notify-body">{{ body }}</div>
        <div v-if="jump" class="notify-jump">点按查看 ›</div>
    </div>
</template>

<script>
/**
 * 站内通知卡（对齐 App 通知卡）：分类色点 + 分类标签 + 标题 + 正文 3 行截断 + jump。
 * 分类/标题/jump 的事实源是 t_app_notification（由 store 按 msgId 关联）；
 * 关联不到时退化为从正文首行「【标题】」还原标题，避免卡片丢标题（与 App 降级路径一致）。
 */
import { formatTimeLabel } from '@/utils/chatFormat'

export default {
    name: 'NotifyCard',
    props: {
        meta: {
            type: Object,
            default: () => ({})
        }
    },
    emits: ['jump'],
    computed: {
        category() {
            return this.meta.category || 'system'
        },
        categoryLabel() {
            return { task: '任务', security: '安全' }[this.category] || '系统'
        },
        categoryColor() {
            return { task: 'var(--accent)', security: 'var(--danger)' }[this.category] || 'var(--ok)'
        },
        /** 正文首行「【标题】内容」拆解 */
        parsed() {
            const raw = this.meta.body || ''
            const lines = raw.split('\n')
            const match = /^【([^】]*)】\s*(.*)$/.exec(lines[0] || '')
            if (!match) {
                return { title: this.meta.title || '', body: raw }
            }
            // 首行除标题外还有内容时才保留（避免正文出现空行）
            const rest = match[2] ? [match[2], ...lines.slice(1)] : lines.slice(1)
            return { title: this.meta.title || match[1], body: rest.join('\n') }
        },
        title() {
            return this.parsed.title
        },
        body() {
            return this.parsed.body
        },
        timeText() {
            return this.meta.time || ''
        },
        jump() {
            return this.meta.jump || ''
        }
    },
    methods: {
        formatTimeLabel,
        openJump() {
            if (this.jump) {
                this.$emit('jump', this.jump)
            }
        }
    }
}
</script>

<style scoped>
.notify-card {
    max-width: min(86%, 720px);
    border-radius: 16px;
    background: var(--bubble-bot, #1E293B);
    border: 1px solid var(--line-2);
    padding: 12px;
    cursor: default;
}

.notify-card.clickable {
    cursor: pointer;
}

.notify-card.clickable:hover {
    border-color: rgba(14, 165, 233, .45);
}

.notify-head {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    font-weight: 500;
}

.notify-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
}

.notify-time {
    margin-left: auto;
    color: var(--text-2);
    font-weight: 400;
    font-size: 11px;
}

.notify-title {
    margin-top: 6px;
    font-size: 14px;
    font-weight: 700;
}

.notify-body {
    margin-top: 4px;
    font-size: 13.5px;
    color: var(--text);
    display: -webkit-box;
    -webkit-line-clamp: 3;
    -webkit-box-orient: vertical;
    overflow: hidden;
    white-space: pre-wrap;
    word-break: break-word;
}

.notify-jump {
    margin-top: 6px;
    font-size: 11.5px;
    color: var(--accent);
}
</style>
