<template>
    <div class="full-screen-btn-con">
        <n-tooltip trigger="hover" placement="bottom">
            <template #trigger>
                <button class="icon-btn" type="button" @click="toggle">
                    <i :class="isFullscreen ? 'fa-solid fa-compress' : 'fa-solid fa-expand'"></i>
                </button>
            </template>
            {{ isFullscreen ? '退出全屏' : '全屏' }}
        </n-tooltip>
    </div>
</template>

<script>
import { useFullscreen } from '@vueuse/core'

export default {
    name: 'Fullscreen',
    setup() {
        const { isFullscreen, toggle } = useFullscreen(document.body)
        return { isFullscreen, toggle }
    }
}
</script>

<style scoped>
.full-screen-btn-con {
    display: flex;
    align-items: center;
}

.icon-btn {
    background: transparent;
    border: none;
    color: var(--text-2);
    cursor: pointer;
    font-size: 15px;
    padding: 6px 8px;
    border-radius: 8px;
    display: flex;
    align-items: center;
}

.icon-btn:hover {
    background: rgba(148, 163, 184, .08);
    color: var(--text);
}
</style>
