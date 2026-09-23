<template>
    <div class="options" :class="{ expired }">
        <div class="options-divider"></div>
        <!-- 可重复点选（2026-09-19）：不折叠，多值已选集合——每个点过的 key 均标「已选」徽标 -->
        <div v-for="option in options" :key="option.key" class="option" :style="optionStyle(option)"
            :class="{ disabled, chosen: isChosen(option) }" @click="pick(option)">
            <span class="option-dot" :style="{ background: colorOf(option) }"></span>
            <div class="option-body">
                <div class="option-label">{{ option.label }}</div>
                <div v-if="option.desc" class="option-desc">{{ option.desc }}</div>
            </div>
            <span v-if="isChosen(option)" class="option-chosen" :style="{ color: colorOf(option) }">已选</span>
        </div>
        <div v-if="expired" class="expired-note">该列表已过期（脚本已推送新列表）</div>
    </div>
</template>

<script>
/**
 * 可点选项块（对齐 App OptionsBlock）：
 * - 逐行可点，可重复点选：点选以 reply 文本发送（行渲染为居中系统提示），chosenKeys 记录
 *   该块所有点过的 key（多值），同一块可继续点其它/同一选项（如逐首保存多首歌）
 * - 过期（本会话已有更新的选项消息）整块降透明度并提示；过期块保留已选标记（历史事实）
 */
import { optionColor } from '@/utils/messagePayload'

export default {
    name: 'OptionsBlock',
    props: {
        options: {
            type: Array,
            default: () => []
        },
        chosenKeys: {
            type: Array,
            default: () => []
        },
        expired: {
            type: Boolean,
            default: false
        },
        disabled: {
            type: Boolean,
            default: false
        }
    },
    emits: ['pick'],
    methods: {
        isChosen(option) {
            return this.chosenKeys.includes(option.key)
        },
        colorOf(option) {
            return optionColor(option.color)
        },
        optionStyle(option) {
            const color = this.colorOf(option)
            const alpha = this.isChosen(option) ? '33' : '1a'
            return { background: `${color}${alpha}` }
        },
        pick(option) {
            if (this.disabled || this.expired) {
                return
            }
            this.$emit('pick', option)
        }
    }
}
</script>

<style scoped>
.options {
    margin-top: 8px;
    padding-top: 8px;
}

.options.expired {
    opacity: .45;
}

.options-divider {
    height: 1px;
    background: rgba(148, 163, 184, .2);
    margin-bottom: 8px;
}

.option {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 10px;
    border-radius: 10px;
    cursor: pointer;
    margin-bottom: 6px;
    transition: filter .15s;
}

.option:last-child {
    margin-bottom: 0;
}

.option:hover {
    filter: brightness(1.28);
}

.option.disabled {
    cursor: not-allowed;
}

.option-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    flex: none;
}

.option-body {
    flex: 1;
    min-width: 0;
}

.option-label {
    font-size: 14px;
    font-weight: 500;
}

.option-desc {
    font-size: 11px;
    color: var(--text-2);
    margin-top: 1px;
}

.option-chosen {
    font-size: 11px;
    font-weight: 500;
    flex: none;
}

.expired-note {
    font-size: 11px;
    color: var(--text-2);
    margin-top: 4px;
}
</style>
