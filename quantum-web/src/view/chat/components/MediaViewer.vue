<template>
    <n-modal v-model:show="visible" :mask-closable="true" @update:show="onToggle">
        <div class="media-viewer" @click.self="close">
            <div class="viewer-bar">
                <button class="bar-btn" type="button" title="关闭" @click="close">
                    <i class="fa-solid fa-arrow-left"></i>
                </button>
                <span class="viewer-title">{{ title }}</span>
                <span v-if="mode === 'image'" class="bar-actions">
                    <button class="bar-btn" type="button" title="缩小" @click="zoomBy(-1)">
                        <i class="fa-solid fa-magnifying-glass-minus"></i>
                    </button>
                    <button class="bar-btn" type="button" title="放大" @click="zoomBy(1)">
                        <i class="fa-solid fa-magnifying-glass-plus"></i>
                    </button>
                    <button class="bar-btn" type="button" title="重置" @click="resetZoom">
                        <i class="fa-solid fa-xmark"></i>
                    </button>
                </span>
            </div>

            <div class="viewer-body" @click.self="mode === 'image' && close()">
                <div v-if="error" class="viewer-error">
                    <div>{{ error }}</div>
                    <div class="viewer-actions">
                        <button type="button" @click="load">重试</button>
                        <button type="button" @click="download">下载查看</button>
                    </div>
                </div>
                <template v-else>
                    <img v-if="mode === 'image' && src" :src="src" :style="imageStyle" @dblclick="toggleZoom" />
                    <video v-else-if="mode === 'video' && src" :src="src" :poster="posterSrc" controls autoplay
                        class="media-el" @error="onMediaError" />
                    <div v-else-if="mode === 'audio'" class="audio-viewer">
                        <div class="audio-icon"><i class="fa-solid fa-music"></i></div>
                        <div class="audio-title">{{ title }}</div>
                        <audio v-if="src" :src="src" controls autoplay class="audio-el" @error="onMediaError" />
                    </div>
                    <div v-else class="viewer-loading">正在加载…</div>
                </template>
            </div>
        </div>
    </n-modal>
</template>

<script>
/**
 * 全屏媒体查看器（对齐 App 的全屏 Dialog）：
 * - 图片：滚轮/按钮缩放（1–6x）、双击在 1x 与 2.5x 间切换、单击背景关闭
 * - 视频：封面 poster 盖层 → 原生控件播放；播放失败给「重试 / 下载查看」（mkv 等浏览器不支持的格式走下载）
 * - 音频：音符图标 + 标题 + 原生控件播放
 * 站内媒体带 JWT 拉 blob 后渲染（App 侧对应 OkHttp 带 JWT 播放）。
 */
import { loadMediaUrl, resolveMediaUrl } from '@/utils/chatMedia'

export default {
    name: 'MediaViewer',
    props: {
        show: {
            type: Boolean,
            default: false
        },
        mode: {
            type: String,
            default: 'image'
        },
        url: {
            type: String,
            default: ''
        },
        title: {
            type: String,
            default: ''
        },
        poster: {
            type: String,
            default: ''
        }
    },
    emits: ['update:show'],
    data() {
        return {
            src: '',
            posterSrc: '',
            error: '',
            scale: 1
        }
    },
    computed: {
        visible: {
            get() {
                return this.show
            },
            set(value) {
                this.$emit('update:show', value)
            }
        },
        imageStyle() {
            return { transform: `scale(${this.scale})` }
        }
    },
    watch: {
        show(value) {
            if (value) {
                this.scale = 1
                this.load()
            } else {
                this.src = ''
                this.posterSrc = ''
                this.error = ''
            }
        }
    },
    methods: {
        onToggle(value) {
            if (!value) {
                this.src = ''
            }
        },
        async load() {
            this.error = ''
            this.src = ''
            try {
                this.src = await loadMediaUrl(this.url)
                if (this.mode === 'video' && this.poster) {
                    this.posterSrc = await loadMediaUrl(resolveMediaUrl(this.poster, 'image'))
                }
            } catch (e) {
                this.error = e?.message || '媒体加载失败'
            }
        },
        close() {
            this.visible = false
        },
        /** 浏览器解码失败（mkv 等不支持的容器/编码、链接失效）：文案与 App 一致，按钮改为下载查看 */
        onMediaError() {
            this.error = '播放失败，链接可能已过期或格式不支持'
        },
        zoomBy(direction) {
            const next = direction > 0 ? this.scale * 1.4 : this.scale / 1.4
            this.scale = Math.min(6, Math.max(1, next))
        },
        resetZoom() {
            this.scale = 1
        },
        toggleZoom() {
            this.scale = this.scale > 1 ? 1 : 2.5
        },
        async download() {
            try {
                const url = this.src || (await loadMediaUrl(this.url))
                const link = document.createElement('a')
                link.href = url
                link.download = this.title || 'download'
                link.target = '_blank'
                document.body.appendChild(link)
                link.click()
                link.remove()
            } catch (e) {
                window.$message?.error(e?.message || '下载失败')
            }
        }
    }
}
</script>

<style scoped>
.media-viewer {
    position: fixed;
    inset: 0;
    display: flex;
    flex-direction: column;
    background: rgba(0, 0, 0, .96);
}

.viewer-bar {
    height: 48px;
    flex: none;
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 0 16px;
    background: rgba(0, 0, 0, .5);
    color: #E2E8F0;
}

.viewer-title {
    font-size: 13px;
    font-weight: 600;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.bar-actions {
    margin-left: auto;
    display: flex;
    gap: 6px;
}

.bar-btn {
    width: 32px;
    height: 32px;
    border: 0;
    border-radius: 8px;
    background: none;
    color: #E2E8F0;
    cursor: pointer;
}

.bar-btn:hover {
    background: rgba(255, 255, 255, .12);
}

.viewer-body {
    flex: 1;
    display: flex;
    align-items: center;
    justify-content: center;
    overflow: auto;
    padding: 20px;
}

.viewer-body img {
    max-width: min(88%, 980px);
    max-height: 78vh;
    border-radius: 10px;
    transition: transform .2s;
    cursor: zoom-in;
}

.media-el {
    max-width: min(90%, 1000px);
    max-height: 78vh;
    border-radius: 10px;
}

.audio-viewer {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 14px;
    color: rgba(255, 255, 255, .85);
}

.audio-icon {
    width: 72px;
    height: 72px;
    border-radius: 50%;
    background: rgba(255, 255, 255, .10);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 26px;
}

.audio-title {
    font-size: 16px;
    font-weight: 500;
    text-align: center;
    max-width: 460px;
}

.audio-el {
    width: 420px;
    max-width: 86vw;
}

.viewer-loading,
.viewer-error {
    color: var(--text-2);
    font-size: 13.5px;
    text-align: center;
}

.viewer-actions {
    margin-top: 12px;
    display: flex;
    gap: 10px;
    justify-content: center;
}

.viewer-actions button {
    padding: 6px 14px;
    border-radius: 8px;
    border: 1px solid var(--line-2);
    background: var(--card);
    color: var(--text);
    cursor: pointer;
    font-family: inherit;
    font-size: 12.5px;
}
</style>
