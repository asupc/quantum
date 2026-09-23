import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import Components from 'unplugin-vue-components/vite'
import { NaiveUiResolver } from 'unplugin-vue-components/resolvers'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
    plugins: [
        vue(),
        Components({
            resolvers: [NaiveUiResolver()],
            dts: false
        })
    ],
    resolve: {
        alias: {
            '@': fileURLToPath(new URL('./src', import.meta.url)),
            _c: fileURLToPath(new URL('./src/components', import.meta.url))
        }
    },
    server: {
        host: '127.0.0.1',
        port: 8080,
        proxy: {
            '/api': {
                target: 'http://127.0.0.1:5088',
                changeOrigin: true,
                ws: true
            },
            // 会话长连接：/ws/app 不在 /api 前缀下，需单列一条才能转发升级头
            // （生产由同一后端托管静态产物，同源直连无需代理）
            '/ws/app': {
                target: 'ws://127.0.0.1:5088',
                changeOrigin: true,
                ws: true
            }
        }
    },
    build: {
        outDir: '../quantum-release/wwwroot',
        emptyOutDir: true,
        chunkSizeWarningLimit: 4096
    },
    esbuild: {
        drop: ['debugger']
    }
})
