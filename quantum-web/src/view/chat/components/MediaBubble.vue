<template>
    <div class="media-bubble">
        <!-- 图片：自适应宽度、限高 420，点击全屏查看；配文（ContentText）在图片下方 -->
        <template v-if="kind === 'image'">
            <div v-if="loadError" class="media-fallback">图片加载失败（链接可能已失效）</div>
            <img v-else-if="src" :src="src" alt="图片消息" @error="loadError = true" @click="open('image')" />
            <div v-else class="media-placeholder">图片加载中…</div>
            <div v-if="contentText" class="media-caption"><RichText :text="contentText" /></div>
        </template>

        <!-- 视频：点封面/紧凑行 → 气泡内联播放（不弹窗）；解码失败回退 重试/下载 -->
        <template v-else-if="kind === 'video'">
            <div v-if="videoActive" class="inline-video">
                <div v-if="videoError" class="media-fallback">
                    <div>{{ videoError }}</div>
                    <div class="inline-actions">
                        <button class="inline-btn" type="button" @click="playVideo">重试</button>
                        <button class="inline-btn" type="button" @click="download">下载查看</button>
                    </div>
                </div>
                <video v-else-if="videoSrc" :src="videoSrc" :poster="posterSrc" controls autoplay class="video-el"
                    @error="onVideoError" />
                <div v-else class="media-placeholder">视频加载中…</div>
                <button class="inline-collapse" type="button" title="收起播放器" @click="closeVideo">
                    <i class="fa-solid fa-chevron-up"></i>
                </button>
            </div>
            <template v-else>
                <div v-if="posterSrc" class="video-cover" @click="playVideo">
                    <img :src="posterSrc" alt="视频封面" />
                    <span class="video-play"><i class="fa-solid fa-play"></i></span>
                </div>
                <div v-else class="video-compact" @click="playVideo">
                    <i class="fa-solid fa-play"></i>
                    <div class="video-body">
                        <div class="video-title">{{ contentText || '视频消息' }}</div>
                        <div class="video-hint">点按播放</div>
                    </div>
                </div>
            </template>
            <div v-if="posterSrc && contentText" class="media-caption"><RichText :text="contentText" /></div>
        </template>

        <!-- 音频：胶囊气泡原地播放（不弹窗）——播放/暂停、进度条可点定位、时间显示 -->
        <div v-else-if="kind === 'audio'" class="media-audio" :class="{ 'is-playing': audioPlaying }">
            <button class="audio-play" type="button" :title="audioPlaying ? '暂停' : '播放'" @click="toggleAudio">
                <i :class="audioLoading ? 'fa-solid fa-spinner fa-spin' : audioPlaying ? 'fa-solid fa-pause' : 'fa-solid fa-play'"></i>
            </button>
            <div class="audio-body">
                <div class="audio-title">
                    <i class="fa-solid fa-music"></i>{{ audioTitle }}
                </div>
                <div class="audio-row">
                    <div class="audio-bar" @click="seekAudio"><i :style="{ width: audioPercent }"></i></div>
                    <span class="audio-time">{{ audioTimeLabel }}</span>
                </div>
                <div v-if="audioError" class="audio-error">
                    <span>{{ audioError }}</span>
                    <button class="inline-btn" type="button" @click="toggleAudio">重试</button>
                    <button class="inline-btn" type="button" @click="download">下载</button>
                </div>
            </div>
            <audio v-if="audioSrc" ref="audioEl" :src="audioSrc" preload="metadata"
                @play="audioPlaying = true" @pause="audioPlaying = false" @ended="onAudioEnded"
                @timeupdate="onAudioTime" @loadedmetadata="onAudioTime" @error="onAudioError" />
        </div>

        <!-- 文件：文件名 + 提示，点按下载（可播扩展名已路由到音视频分支），配文在下方 -->
        <template v-else>
            <div class="media-file" @click="download">
                <span class="file-icon"><i class="fa-solid fa-file-lines"></i></span>
                <div class="file-body">
                    <div class="file-name">{{ fileName }}</div>
                    <div class="file-hint">点按下载查看</div>
                </div>
            </div>
            <div v-if="contentText" class="media-caption"><RichText :text="contentText" /></div>
        </template>
    </div>
</template>

<script>
/**
 * 媒体气泡（图片/视频/音频/文件），视觉对齐 App MessageBubble 的各分支：
 * - image：图片 + 配文（ContentText 在下方），点按进全屏查看器
 * - video：有 Payload.poster → 封面卡；无封面 → 紧凑行；点按在气泡内联播放（不弹窗）
 * - audio：胶囊气泡原地播放（播放/暂停、进度条定位、时间），不弹窗
 * - file：文件名（地址末段）+「点按下载查看」+ 配文（可播扩展名已被 kind 路由到音视频分支）
 * 站内地址（/api/AppUpload、/api/AppMedia）需 JWT，统一经 chatMedia 拉成 blob 再渲染。
 */
import RichText from './RichText.vue'
import { stripMarkup } from '@/utils/chatFormat'
import { loadMediaUrl, resolveMediaUrl, isPlayableAudio, isPlayableVideo, retainMedia, releaseMedia } from '@/utils/chatMedia'

/** 模块级音频单例：同一时刻只允许一个气泡在播，开播前暂停上一个 */
let activeAudioEl = null

function pauseOtherAudio(el) {
    if (activeAudioEl && activeAudioEl !== el && !activeAudioEl.paused) {
        activeAudioEl.pause()
    }
    activeAudioEl = el
}

function formatTime(seconds) {
    const total = Math.max(0, Math.floor(seconds || 0))
    return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`
}

export default {
    name: 'MediaBubble',
    components: { RichText },
    props: {
        message: {
            type: Object,
            required: true
        }
    },
    emits: ['open'],
    data() {
        return {
            src: '',
            posterSrc: '',
            loadError: false,
            visible: false,
            videoActive: false,
            videoSrc: '',
            videoError: '',
            audioSrc: '',
            audioPlaying: false,
            audioLoading: false,
            audioError: '',
            audioCurrent: 0,
            audioDuration: 0
        }
    },
    computed: {
        contentType() {
            return this.message.ContentType || 'file'
        },
        rawUrl() {
            return resolveMediaUrl(this.message.Content, this.contentType)
        },
        contentText() {
            return (this.message.ContentText || '').trim()
        },
        playableAs() {
            if (isPlayableAudio(this.rawUrl)) {
                return 'audio'
            }
            if (isPlayableVideo(this.rawUrl)) {
                return 'video'
            }
            return ''
        },
        kind() {
            if (this.contentType === 'image') {
                return 'image'
            }
            if (this.contentType === 'video') {
                return 'video'
            }
            if (this.contentType === 'audio') {
                return 'audio'
            }
            // 历史 file 类型的音视频按扩展名路由（与 App MediaTypes 判定一致）
            return this.playableAs || 'file'
        },
        audioTitle() {
            return this.contentText || this.baseName || '音频'
        },
        fileName() {
            return this.baseName || this.contentText || '文件'
        },
        /** 地址末段（去 query/hash），与 App 音频标题回退规则一致 */
        baseName() {
            const clean = (this.rawUrl || '').split('#')[0].split('?')[0]
            const name = clean.substring(clean.lastIndexOf('/') + 1)
            try {
                return decodeURIComponent(name)
            } catch (e) {
                return name
            }
        },
        audioPercent() {
            if (!this.audioDuration) {
                return '0%'
            }
            return `${Math.min(100, (this.audioCurrent / this.audioDuration) * 100)}%`
        },
        audioTimeLabel() {
            if (!this.audioCurrent && !this.audioDuration) {
                return ''
            }
            if (!this.audioDuration) {
                return formatTime(this.audioCurrent)
            }
            return `${formatTime(this.audioCurrent)} / ${formatTime(this.audioDuration)}`
        }
    },
    watch: {
        rawUrl() {
            // 仅当已可见才即时换源重拉；未可见时交给 mounted 的 IntersectionObserver 首次进入视口再 load
            if (this.visible) {
                this.load()
            }
        }
    },
    mounted() {
        if (typeof IntersectionObserver === 'undefined') {
            // 环境不支持懒加载 → 直接可见加载（等价旧行为）
            this.visible = true
            this.load()
            return
        }
        this._mediaObserver = new IntersectionObserver(
            (entries) => {
                if (entries.some((e) => e.isIntersecting)) {
                    this.visible = true
                    this.load()
                    this._mediaObserver.disconnect()
                    this._mediaObserver = null
                }
            },
            { rootMargin: '200px 0px' }
        )
        this._mediaObserver.observe(this.$el)
    },
    beforeUnmount() {
        this._mediaObserver?.disconnect()
        this._mediaObserver = null
        this.untrackRetainedAll()
        const el = this.$refs.audioEl
        if (el) {
            el.pause()
            if (activeAudioEl === el) {
                activeAudioEl = null
            }
        }
        this.closeVideo()
    },
    methods: {
        /** 记录本气泡挂载的 object URL（换源/卸载时统一 release），供延迟 revoke 跳过在用对象 */
        trackRetained(objectUrl) {
            if (!objectUrl) {
                return
            }
            if (!this._retained) {
                this._retained = []
            }
            this._retained.push(objectUrl)
            retainMedia(objectUrl)
        },
        untrackRetainedAll() {
            (this._retained || []).forEach((u) => releaseMedia(u))
            this._retained = []
        },
        async load() {
            this.loadError = false
            this.src = ''
            this.posterSrc = ''
            this.closeVideo()
            this.resetAudio()
            if (!this.rawUrl) {
                return
            }
            if (this.kind === 'image') {
                try {
                    this.src = await loadMediaUrl(this.rawUrl)
                    this.trackRetained(this.src)
                } catch (e) {
                    this.loadError = true
                }
            }
            if (this.kind === 'video' && this.message.poster) {
                try {
                    this.posterSrc = await loadMediaUrl(resolveMediaUrl(this.message.poster, 'image'))
                    this.trackRetained(this.posterSrc)
                } catch (e) {
                    this.posterSrc = ''
                }
            }
        },
        /** 图片：全屏查看器（视频/音频不再弹窗，一律内联播放） */
        open() {
            this.$emit('open', {
                mode: 'image',
                url: this.rawUrl,
                title: this.contentText ? stripMarkup(this.contentText) : '图片消息',
                poster: ''
            })
        },
        async playVideo() {
            if (this.videoActive) {
                // 重试：清掉 src 强制重建 <video>，并重新走鉴权加载
                this.videoError = ''
                this.videoSrc = ''
            }
            this.videoActive = true
            try {
                this.videoSrc = await loadMediaUrl(this.rawUrl)
                this.trackRetained(this.videoSrc)
            } catch (e) {
                this.videoError = e?.message || '视频加载失败'
            }
        },
        onVideoError() {
            if (this.videoSrc) {
                this.videoError = '播放失败，链接可能已过期或格式不支持'
            }
        },
        closeVideo() {
            this.videoActive = false
            this.videoSrc = ''
            this.videoError = ''
        },
        async toggleAudio() {
            if (this.audioPlaying) {
                this.$refs.audioEl?.pause()
                return
            }
            this.audioError = ''
            if (!this.audioSrc) {
                this.audioLoading = true
                try {
                    this.audioSrc = await loadMediaUrl(this.rawUrl)
                    this.trackRetained(this.audioSrc)
                } catch (e) {
                    this.audioError = e?.message || '音频加载失败'
                    return
                } finally {
                    this.audioLoading = false
                }
            }
            await this.$nextTick()
            const el = this.$refs.audioEl
            if (!el) {
                return
            }
            pauseOtherAudio(el)
            try {
                await el.play()
            } catch (e) {
                this.audioError = '播放失败，格式可能不受支持'
            }
        },
        onAudioTime() {
            const el = this.$refs.audioEl
            if (!el) {
                return
            }
            this.audioCurrent = el.currentTime || 0
            if (el.duration && Number.isFinite(el.duration)) {
                this.audioDuration = el.duration
            }
        },
        onAudioEnded() {
            this.audioPlaying = false
            this.audioCurrent = 0
        },
        onAudioError() {
            if (this.audioSrc) {
                this.audioPlaying = false
                this.audioError = '播放失败，链接可能已过期或格式不支持'
            }
        },
        seekAudio(event) {
            const el = this.$refs.audioEl
            if (!el || !this.audioDuration) {
                return
            }
            const rect = event.currentTarget.getBoundingClientRect()
            const ratio = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width))
            el.currentTime = ratio * this.audioDuration
            this.audioCurrent = el.currentTime
        },
        resetAudio() {
            this.$refs.audioEl?.pause()
            this.audioSrc = ''
            this.audioPlaying = false
            this.audioLoading = false
            this.audioError = ''
            this.audioCurrent = 0
            this.audioDuration = 0
        },
        async download() {
            try {
                const url = await loadMediaUrl(this.rawUrl)
                const link = document.createElement('a')
                link.href = url
                link.download = this.fileName
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
.media-bubble {
    min-width: 170px;
}

.media-image img,
.media-bubble img {
    display: block;
    max-width: 100%;
    max-height: 360px;
    border-radius: 10px;
    cursor: zoom-in;
}

.media-placeholder,
.media-fallback {
    padding: 18px;
    border-radius: 10px;
    background: rgba(148, 163, 184, .10);
    color: var(--text-2);
    font-size: 12.5px;
}

.video-cover {
    position: relative;
    width: 340px;
    max-width: 100%;
    height: 190px;
    border-radius: 10px;
    overflow: hidden;
    cursor: pointer;
    background: #0d1626;
}

.video-cover img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    cursor: pointer;
}

.video-play {
    position: absolute;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
}

.video-play i {
    width: 52px;
    height: 52px;
    border-radius: 50%;
    background: rgba(0, 0, 0, .5);
    color: #fff;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 18px;
    padding-left: 3px;
}

.video-compact {
    display: flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;
    color: var(--accent);
}

.video-body {
    min-width: 0;
}

.video-title {
    font-size: 14px;
    color: var(--text);
}

.video-hint {
    font-size: 12px;
    color: var(--text-2);
}

/* 内联视频播放器（点封面/紧凑行原位展开，不弹窗） */
.inline-video {
    position: relative;
}

.video-el {
    display: block;
    width: 340px;
    max-width: 100%;
    max-height: 420px;
    border-radius: 10px;
    background: #000;
}

.inline-collapse {
    position: absolute;
    top: 8px;
    right: 8px;
    width: 26px;
    height: 26px;
    border: 0;
    border-radius: 50%;
    background: rgba(0, 0, 0, .45);
    color: #fff;
    cursor: pointer;
    font-size: 12px;
    display: flex;
    align-items: center;
    justify-content: center;
}

.inline-collapse:hover {
    background: rgba(0, 0, 0, .7);
}

.inline-actions {
    margin-top: 10px;
    display: flex;
    gap: 10px;
    justify-content: center;
}

.inline-btn {
    padding: 4px 12px;
    border-radius: 7px;
    border: 1px solid var(--line-2);
    background: var(--card);
    color: var(--text);
    cursor: pointer;
    font-family: inherit;
    font-size: 12px;
}

.inline-btn:hover {
    border-color: var(--accent);
    color: var(--accent);
}

.media-audio {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 250px;
    padding: 2px 0;
}

.audio-play {
    width: 36px;
    height: 36px;
    flex: none;
    border: 0;
    padding: 0;
    border-radius: 50%;
    background: rgba(14, 165, 233, .14);
    color: var(--accent);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 14px;
    font-family: inherit;
    cursor: pointer;
}

.media-audio.is-playing .audio-play {
    background: rgba(14, 165, 233, .24);
}

.audio-body {
    flex: 1;
    min-width: 0;
}

.audio-title {
    display: flex;
    align-items: center;
    gap: 4px;
    font-size: 14px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--text);
}

.audio-title i {
    font-size: 11px;
    color: var(--text-2);
}

.audio-row {
    margin-top: 4px;
    display: flex;
    align-items: center;
    gap: 8px;
}

.audio-bar {
    flex: 1;
    height: 3px;
    border-radius: 2px;
    background: rgba(148, 163, 184, .22);
    overflow: hidden;
    cursor: pointer;
}

.audio-bar i {
    display: block;
    height: 100%;
    width: 0;
    background: var(--accent);
    transition: width .2s linear;
}

.audio-time {
    flex: none;
    font-size: 11px;
    color: var(--text-2);
    font-variant-numeric: tabular-nums;
}

.audio-error {
    margin-top: 5px;
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 11.5px;
    color: #f87171;
}

.media-file {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 230px;
    cursor: pointer;
}

.file-icon {
    width: 34px;
    height: 34px;
    flex: none;
    border-radius: 8px;
    background: rgba(148, 163, 184, .12);
    color: var(--text-2);
    display: flex;
    align-items: center;
    justify-content: center;
}

.file-body {
    min-width: 0;
}

.file-name {
    font-size: 14px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 320px;
}

.file-hint {
    font-size: 11.5px;
    color: var(--text-2);
    margin-top: 1px;
}

.media-caption {
    margin-top: 6px;
}
</style>
