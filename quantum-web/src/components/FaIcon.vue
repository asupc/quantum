<template>
    <i :class="iconClass" :style="style" aria-hidden="true"></i>
</template>

<script>
// FontAwesome 图标统一封装：接受完整 class 字符串（如 'fa-solid fa-gear'），
// 后端菜单 DB 存的 icon 即此格式，不可改
export default {
    name: 'FaIcon',
    props: {
        icon: { type: String, required: true },
        size: { type: [Number, String], default: undefined }
    },
    computed: {
        iconClass() {
            return this.icon || ''
        },
        style() {
            if (this.size === undefined) return undefined
            const s = typeof this.size === 'number' ? `${this.size}px` : this.size
            return { fontSize: s, width: s, height: s }
        }
    }
}
</script>
