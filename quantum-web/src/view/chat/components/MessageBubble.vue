<template>
    <!-- 通知卡：独立左对齐卡片，不带头像 -->
    <div v-if="isNotify" class="message-row">
        <NotifyCard :meta="notifyMeta" @jump="$emit('jump', $event)" />
    </div>

    <!-- 点选代发行：QQ 系统提示形态（居中小字灰），不渲染头像/气泡 -->
    <div v-else-if="isPickHint" class="pick-hint" :title="fullTime">
        <span>已选择「{{ message.pickLabel }}」</span>
        <template v-if="message.failed">
            <span class="pick-failed">{{ message.failReason || '发送失败' }}</span>
            <button type="button" @click="$emit('resend', message)">重发</button>
        </template>
    </div>

    <!-- 普通气泡 -->
    <div v-else class="message-row" :class="{ mine: isMine }">
        <div v-if="!isMine" class="avatar bot">Q</div>
        <div v-if="isMine" class="avatar mine">我</div>
        <span v-if="message.pending" class="send-state" title="发送中"></span>
        <div class="bubble" :title="fullTime">
            <div class="bubble-tools">
                <span class="tool" title="复制" @click="copy"><i class="fa-regular fa-copy"></i></span>
                <span v-if="savable" class="tool" title="保存到服务器" @click="saveToServer">
                    <i class="fa-solid fa-cloud-arrow-down"></i>
                </span>
            </div>
            <div class="bubble-content">
                <MediaBubble v-if="isMedia" :message="message" @open="$emit('open-media', $event)" />
                <RichText v-else :text="message.Content" />
            </div>
            <OptionsBlock v-if="hasOptions" :options="message.options" :chosen-keys="message.chosenKeys"
                :expired="expired" :disabled="isMine" @pick="$emit('pick-option', { message, option: $event })" />
            <div v-if="message.failed" class="bubble-failed">
                <span>{{ message.failReason || '发送失败' }}</span>
                <button type="button" @click="$emit('resend', message)">重发</button>
            </div>
        </div>
    </div>
</template>

<script>
/**
 * 消息气泡外壳（对齐 App MessageBubble / MessageRow）：
 * 头像 38px、机器人左/用户右、靠头像侧下角 4px 收尾、圆角 16、内边距 9/12、最大宽 min(68%,720px)。
 * 气泡内容按 ContentType 分派；机器人消息下方追加选项块；悬停显示复制/保存工具条（桌面端增强）。
 */
import RichText from './RichText.vue'
import MediaBubble from './MediaBubble.vue'
import OptionsBlock from './OptionsBlock.vue'
import NotifyCard from './NotifyCard.vue'
import { formatFullTime, formatTimeLabel } from '@/utils/chatFormat'
import { resolveMediaUrl, isOwnServerUrl } from '@/utils/chatMedia'

const MEDIA_TYPES = ['image', 'video', 'audio', 'file']

export default {
    name: 'MessageBubble',
    components: { RichText, MediaBubble, OptionsBlock, NotifyCard },
    props: {
        message: {
            type: Object,
            required: true
        },
        latestOptionsSeq: {
            type: Number,
            default: 0
        }
    },
    emits: ['pick-option', 'open-media', 'resend', 'jump', 'save-media'],
    computed: {
        isNotify() {
            return this.message.ContentType === 'notify'
        },
        /** 点选代发行（本端单端标记，见 stores/chat.js pickRows）：渲染为居中系统提示 */
        isPickHint() {
            return this.message.Direction === 2 && !!this.message.pickLabel
        },
        isMine() {
            return this.message.Direction === 2
        },
        isMedia() {
            return MEDIA_TYPES.includes(this.message.ContentType)
        },
        fullTime() {
            return formatFullTime(this.message.CreateTime)
        },
        hasOptions() {
            return this.message.Direction === 1 && Array.isArray(this.message.options) && this.message.options.length > 0
        },
        /** 本会话已有更新的选项消息时，本块过期（与 App 判定一致） */
        expired() {
            return !!this.message.Seq && this.message.Seq < this.latestOptionsSeq
        },
        mediaUrl() {
            return resolveMediaUrl(this.message.Content, this.message.ContentType)
        },
        /** 外链媒体才提供「保存到服务器」（自家文件不显示，与 App 一致） */
        savable() {
            return this.isMedia && /^https?:\/\//i.test(this.mediaUrl) && !isOwnServerUrl(this.mediaUrl)
        },
        notifyMeta() {
            return {
                ...(this.message.notifyMeta || {}),
                time: formatTimeLabel(this.message.CreateTime),
                body: this.message.Content
            }
        }
    },
    methods: {
        async copy() {
            const text = this.message.Content || ''
            try {
                await navigator.clipboard.writeText(text)
                window.$message?.success('已复制消息内容')
            } catch (e) {
                window.$message?.error('复制失败，请手动选择文本')
            }
        },
        saveToServer() {
            // 与 App 一致：发普通文本指令「保存媒体 <url>」，由 media_saver 任务落盘后回推
            this.$emit('save-media', this.mediaUrl)
        }
    }
}
</script>

<style scoped>
.message-row {
    display: flex;
    gap: 8px;
    margin-bottom: 8px;
    align-items: flex-start;
}

/* 点选代发系统提示（QQ IM 系统消息样式）：居中、小字、无头像无气泡 */
.pick-hint {
    display: flex;
    justify-content: center;
    align-items: center;
    gap: 6px;
    margin-bottom: 8px;
    font-size: 11px;
    color: var(--text-2);
}

.pick-hint .pick-failed {
    color: var(--danger);
}

.pick-hint button {
    border: 0;
    background: none;
    color: var(--accent);
    cursor: pointer;
    font-family: inherit;
    font-size: 11px;
    padding: 0;
}

.message-row.mine {
    flex-direction: row-reverse;
}

.avatar {
    width: 38px;
    height: 38px;
    flex: none;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 13px;
    font-weight: 700;
}

.avatar.bot {
    background: linear-gradient(135deg, var(--accent), #6366F1);
    color: #04121B;
    font-size: 16px;
}

.avatar.mine {
    background: rgba(99, 102, 241, .20);
    color: #C7D2FE;
    font-weight: 600;
}

.send-state {
    width: 14px;
    height: 14px;
    flex: none;
    margin-top: 12px;
    border-radius: 50%;
    border: 2px solid rgba(148, 163, 184, .35);
    border-top-color: var(--text-2);
    animation: chat-spin .8s linear infinite;
}

@keyframes chat-spin {
    to {
        transform: rotate(360deg);
    }
}

.bubble {
    position: relative;
    max-width: min(68%, 720px);
    padding: 9px 12px;
    border-radius: 16px;
    border-bottom-left-radius: 4px;
    background: var(--bubble-bot, #1E293B);
}

.message-row.mine .bubble {
    background: var(--bubble-me, #0C4A6E);
    border-bottom-left-radius: 16px;
    border-bottom-right-radius: 4px;
}

.bubble-tools {
    position: absolute;
    top: -12px;
    right: 8px;
    display: none;
    gap: 2px;
    padding: 2px;
    border-radius: 8px;
    background: var(--card);
    border: 1px solid var(--line-2);
    box-shadow: 0 6px 20px rgba(0, 0, 0, .45);
    z-index: 2;
}

.message-row.mine .bubble-tools {
    right: auto;
    left: 8px;
}

.bubble:hover .bubble-tools {
    display: flex;
}

.tool {
    width: 24px;
    height: 24px;
    border-radius: 6px;
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--text-2);
    cursor: pointer;
    font-size: 12px;
}

.tool:hover {
    background: rgba(148, 163, 184, .14);
    color: var(--text);
}

.bubble-content {
    font-size: 14px;
}

.bubble-failed {
    margin-top: 6px;
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 12px;
    color: var(--danger);
}

.bubble-failed button {
    border: 0;
    background: none;
    color: var(--accent);
    cursor: pointer;
    font-family: inherit;
    font-size: 12px;
    padding: 0;
}
</style>
