<template>
    <div ref="stream" class="message-stream" @scroll="onScroll">
        <div ref="content" class="stream-content">
            <div v-if="loading" class="stream-hint">正在加载会话…</div>
            <template v-else>
                <div v-if="hasMore" class="stream-hint">
                    {{ loadingEarlier ? '正在加载更早的消息…' : '向上滚动加载更早的消息' }}
                </div>
                <div v-if="!messages.length" class="stream-empty">
                    <div class="empty-title">暂无消息</div>
                    <div class="empty-tip">在下方输入指令，或等待脚本推送</div>
                </div>
                <template v-for="(item, index) in rows" :key="item.key">
                    <div v-if="item.separator" class="time-separator">{{ item.separator }}</div>
                    <MessageBubble :message="item.message" :latest-options-seq="latestOptionsSeq"
                        @pick-option="$emit('pick-option', $event)" @open-media="$emit('open-media', $event)"
                        @resend="$emit('resend', $event)" @jump="$emit('jump', $event)"
                        @save-media="$emit('save-media', $event)" />
                </template>
            </template>
        </div>

        <button v-if="showJump" class="jump-bottom" type="button" @click="scrollToBottom(true)">
            <i class="fa-solid fa-arrow-down"></i>回到底部
        </button>
    </div>
</template>

<script>
/**
 * 消息流（对齐 App ChatScreen 的 LazyColumn 行为 + 桌面端增强）：
 * - 时间分隔：首条必插，间隔 > 5 分钟插（规则见 chatFormat）
 * - 进入会话（首屏/切会话）**瞬时**定位到最后一条，且不播放滚动动画；贴底时新消息才平滑跟随
 * - 上翻读历史时不打断；向下滚回底部后恢复贴底
 * - 媒体（图片/封面）异步加载会撑高内容：只要仍处于贴底态就继续贴底，避免"最新的看不见"
 * - 向上滚动到顶自动加载更早一页（App 侧 loadMore 未接线，Web 补齐）
 */
import MessageBubble from './MessageBubble.vue'
import { formatTimeLabel, shouldInsertTimeSeparator } from '@/utils/chatFormat'

export default {
    name: 'MessageList',
    components: { MessageBubble },
    props: {
        messages: {
            type: Array,
            default: () => []
        },
        latestOptionsSeq: {
            type: Number,
            default: 0
        },
        hasMore: {
            type: Boolean,
            default: false
        },
        loadingEarlier: {
            type: Boolean,
            default: false
        },
        loading: {
            type: Boolean,
            default: false
        },
        /** 当前会话键：变化即视为「重新进入会话」，定位状态与滚动一律重置 */
        sessionKey: {
            type: String,
            default: ''
        }
    },
    emits: ['pick-option', 'open-media', 'resend', 'jump', 'save-media', 'load-earlier'],
    data() {
        return {
            showJump: false,
            positioned: false,
            prepending: false,
            /** 贴底态：为真时新内容（含媒体异步加载撑高）继续贴底；用户上翻后置否 */
            stickBottom: true,
            prevScrollHeight: 0,
            prevScrollTop: 0,
            resizeObserver: null
        }
    },
    computed: {
        rows() {
            const result = []
            let prevTime = null
            this.messages.forEach((message, index) => {
                const separator = index === 0 || shouldInsertTimeSeparator(prevTime, message.CreateTime)
                    ? formatTimeLabel(message.CreateTime)
                    : ''
                result.push({
                    key: message.MsgId || `idx-${index}`,
                    separator,
                    message
                })
                if (message.CreateTime) {
                    prevTime = message.CreateTime
                }
            })
            return result
        }
    },
    watch: {
        sessionKey() {
            this.resetPosition()
        },
        messages: {
            handler(next, prev) {
                const stream = this.$refs.stream
                if (!stream) {
                    return
                }
                if (!this.positioned && next.length) {
                    // 进入会话（首屏或切会话）：瞬时定位到最后一条，**不播放滚动动画**
                    this.pinToBottom()
                    return
                }
                if (this.prepending) {
                    // 上翻加载：保持视口内容不动（新增高度补进 scrollTop）
                    const added = stream.scrollHeight - this.prevScrollHeight
                    this.$nextTick(() => {
                        stream.scrollTop = this.prevScrollTop + added
                        this.prepending = false
                    })
                    return
                }
                const grew = (next?.length || 0) > (prev?.length || 0)
                if (grew && this.stickBottom) {
                    // 贴底时新消息平滑跟随（首屏定位不走这里，不会出现"从上往下滚"）
                    this.$nextTick(() => this.scrollToBottom(true))
                } else if (grew) {
                    this.showJump = true
                }
            }
        }
    },
    mounted() {
        // 媒体（图片/视频封面）异步加载会撑高内容：只要仍是贴底态就继续贴底，
        // 否则进会话后看到的是"加载完图片前"的旧位置，最新的消息被顶出视口。
        // 观察内容包裹层（容器自身高度固定，观察容器不会触发）。
        if (typeof ResizeObserver !== 'undefined' && this.$refs.content) {
            this.resizeObserver = new ResizeObserver(() => {
                if (this.stickBottom && this.positioned) {
                    this.scrollToBottom(false)
                }
            })
            this.resizeObserver.observe(this.$refs.content)
        }
    },
    beforeUnmount() {
        this.resizeObserver?.disconnect()
        this.resizeObserver = null
        clearTimeout(this._scrollTimer)
    },
    methods: {
        nearBottom(tolerance = 60) {
            const stream = this.$refs.stream
            if (!stream) {
                return true
            }
            return stream.scrollHeight - stream.scrollTop - stream.clientHeight < tolerance
        },
        /** 进入会话的定位：等 DOM 更新后瞬时落底（赋值 scrollTop 不经过动画），并标记贴底 */
        pinToBottom() {
            const stream = this.$refs.stream
            if (!stream) {
                return
            }
            this.stickBottom = true
            this.showJump = false
            this.$nextTick(() => {
                stream.scrollTop = stream.scrollHeight
                this.positioned = true
                // 首帧渲染后媒体可能刚解码完，再补一次（仍为瞬时）
                requestAnimationFrame(() => {
                    if (this.stickBottom) {
                        stream.scrollTop = stream.scrollHeight
                    }
                })
            })
        },
        onScroll() {
            const stream = this.$refs.stream
            if (!stream) {
                return
            }
            const atBottom = this.nearBottom()
            this.showJump = !atBottom
            // 仅由用户滚动改变贴底态（程序化滚动落底后 atBottom 仍为真，不受影响）
            this.stickBottom = atBottom
            if (stream.scrollTop < 40 && this.hasMore && !this.loadingEarlier) {
                this.prevScrollHeight = stream.scrollHeight
                this.prevScrollTop = stream.scrollTop
                this.prepending = true
                this.stickBottom = false
                this.$emit('load-earlier')
            }
        },
        scrollToBottom(smooth = false) {
            const stream = this.$refs.stream
            if (!stream) {
                return
            }
            if (smooth) {
                // 平滑滚动期间按住贴底标记：滚动事件里 nearBottom 为假会把标记清掉，
                // 用一次性标记在结束后恢复
                stream.scrollTo({ top: stream.scrollHeight, behavior: 'smooth' })
                clearTimeout(this._scrollTimer)
                this._scrollTimer = setTimeout(() => {
                    this.stickBottom = true
                    this.showJump = false
                }, 320)
            } else {
                stream.scrollTop = stream.scrollHeight
                this.stickBottom = true
                this.showJump = false
            }
        },
        /** 切会话：重置定位状态（下一次数据到达走瞬时落底分支） */
        resetPosition() {
            this.positioned = false
            this.prepending = false
            this.stickBottom = true
            this.showJump = false
            const stream = this.$refs.stream
            if (stream) {
                stream.scrollTop = 0
            }
        }
    }
}
</script>

<style scoped>
.message-stream {
    position: relative;
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 12px 18px 16px;
}

.stream-hint {
    text-align: center;
    font-size: 12px;
    color: var(--text-3);
    padding: 6px 0 10px;
}

.stream-content {
    display: flow-root;
}

.stream-empty {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
    padding: 18vh 0;
}

.empty-title {
    font-size: 14px;
    color: var(--text-2);
}

.empty-tip {
    font-size: 12px;
    color: var(--text-3);
}

.time-separator {
    display: flex;
    justify-content: center;
    margin: 8px 0;
    font-size: 11px;
    color: var(--text-2);
}

.jump-bottom {
    position: sticky;
    bottom: 8px;
    left: 100%;
    transform: translateX(-100%);
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 7px 12px;
    border-radius: 999px;
    background: var(--card);
    border: 1px solid var(--line-2);
    color: var(--text-2);
    font-size: 12px;
    font-family: inherit;
    cursor: pointer;
    box-shadow: 0 10px 30px rgba(0, 0, 0, .5);
    margin-top: 6px;
}

.jump-bottom:hover {
    color: var(--text);
    border-color: rgba(14, 165, 233, .45);
}
</style>
