import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
    // 视图层回归测试需要直接导入 .vue（只测 options 对象上的方法，不挂载渲染）
    plugins: [vue()],
    resolve: {
        alias: {
            '@': fileURLToPath(new URL('./src', import.meta.url)),
            // 与 vite.config.js 对齐：login.vue 等视图经 `_c/` 引组件，缺这条别名测试无法导入该类 .vue
            _c: fileURLToPath(new URL('./src/components', import.meta.url))
        }
    },
    test: {
        include: ['tests/**/*.spec.js'],
        environment: 'node'
    }
})
