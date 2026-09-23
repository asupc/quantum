<template>
    <div class="rich-text" :class="{ 'is-clamped': clamped }" v-html="html"></div>
    <button v-if="foldable" class="fold-btn" type="button" @click="toggleFold">
        {{ clamped ? '展开全文' : '收起' }}
    </button>
</template>

<script>
/**
 * 富文本渲染（对齐 App RichMessageText）：
 * 彩色文字 / 胶囊标签 / 命名链接（只显示文字）/ URL 自动链接 / 超 10 行折叠。
 * 折叠按换行数判定（与 App 一致），折叠态用 CSS 行钳制，展开/收起点同一按钮切换。
 */
import { parseRich, needFold, SPAN_PLAIN, SPAN_LINK, RICH_COLORS } from '@/utils/richText'

const URL_REGEX = /(https?:\/\/[A-Za-z0-9\-._~:/?#[\]@!$&'()*+,;=%]+)/g

function escapeHtml(text) {
    return String(text).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]))
}


export default {
    name: 'RichText',
    props: {
        text: {
            type: String,
            default: ''
        },
        // 配文等紧凑场景可关掉折叠
        supportFold: {
            type: Boolean,
            default: true
        }
    },
    data() {
        return {
            folded: true
        }
    },
    computed: {
        foldable() {
            return this.supportFold && needFold(this.text)
        },
        clamped() {
            return this.foldable && this.folded
        },
        html() {
            return parseRich(this.text)
                .map((span) => {
                    if (span.type === SPAN_PLAIN) {
                        return this.linkify(span.text)
                    }
                    if (span.type === SPAN_LINK) {
                        // 命名链接：只显示文字（如「详情」），链接地址不占版面
                        return `<a href="${escapeHtml(span.url)}" target="_blank" rel="noreferrer">${escapeHtml(span.text)}</a>`
                    }
                    const color = RICH_COLORS[span.color]
                    if (span.pill) {
                        return `<span class="rt-pill" style="color:${color};background:${color}1f;border-color:${color}59">` +
                            `<i style="background:${color}"></i>${escapeHtml(span.text)}</span>`
                    }
                    return `<span class="rt-mark" style="color:${color}">${escapeHtml(span.text)}</span>`
                })
                .join('')
        }
    },
    methods: {
        linkify(text) {
            return escapeHtml(text)
                .split(URL_REGEX)
                .map((part, index) =>
                    index % 2
                        ? `<a href="${part}" target="_blank" rel="noreferrer">${part}</a>`
                        : part
                )
                .join('')
        },
        toggleFold() {
            this.folded = !this.folded
        }
    }
}
</script>

<style scoped>
.rich-text {
    font-size: 14px;
    line-height: 1.6;
    white-space: pre-wrap;
    word-break: break-word;
}

.rich-text.is-clamped {
    display: -webkit-box;
    -webkit-line-clamp: 10;
    -webkit-box-orient: vertical;
    overflow: hidden;
}

.rich-text :deep(.rt-mark) {
    font-weight: 600;
}

.rich-text :deep(.rt-pill) {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 2px 6px;
    margin: 0 1px;
    border-radius: 999px;
    border: 1px solid;
    font-size: 11px;
    font-weight: 500;
    line-height: 1.5;
}

.rich-text :deep(.rt-pill i) {
    width: 5px;
    height: 5px;
    border-radius: 50%;
    display: block;
}

.rich-text :deep(a) {
    color: var(--accent);
    text-decoration: underline;
    text-underline-offset: 2px;
    word-break: break-all;
}

.fold-btn {
    margin-top: 4px;
    padding: 0;
    border: 0;
    background: none;
    color: var(--accent);
    font-size: 12px;
    font-family: inherit;
    cursor: pointer;
}

.fold-btn:hover {
    color: var(--accent-2);
}
</style>
