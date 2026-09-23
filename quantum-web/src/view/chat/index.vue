<template>
    <div class="chat-page" :class="{ narrow, 'show-room': narrowShowRoom }">
        <ConversationList :sessions="sessions" :task-map="taskMap" :current-key="currentKey"
            @select="switchSession" @delete="onDeleteSession" />

        <section class="chat-room">
            <ChatHeader :session-key="currentKey" :title="title" :total="currentSession?.Total || 0"
                :ws-state="wsState" :ws-detail="wsDetail" :task-map="taskMap" :show-back="narrow"
                @refresh="refresh" @delete="onDeleteCurrentSession" @open-session="switchSession" @back="backToList" />

            <MessageList :messages="currentMessages" :latest-options-seq="latestOptionsSeq"
                :has-more="hasMoreEarlier" :loading-earlier="loadingEarlier" :loading="loadingSession"
                :session-key="currentKey" @pick-option="onPickOption" @open-media="onOpenMedia"
                @resend="onResend" @jump="onJump" @save-media="onSaveMedia" @load-earlier="loadEarlier" />

            <ChatComposer :disabled="wsState === 'connecting' && !currentMessages.length" @send="onSend" />
        </section>

        <MediaViewer v-model:show="viewer.show" :mode="viewer.mode" :url="viewer.url" :title="viewer.title"
            :poster="viewer.poster" />
    </div>
</template>

<script>
/**
 * 会话页（Web 端会话视图，交互/视觉对齐 App 端）：
 * 左会话列表 + 右会话详情双栏；窄屏（<960px）折叠为单栏，用顶栏返回按钮切换。
 * 数据全部来自 stores/chat（WS 实时 + REST 首屏/补拉），本页只做编排与交互。
 */
import { mapState, mapActions } from 'pinia'
import { useChatStore } from '@/stores/chat'
import { sessionTitle } from '@/utils/chatFormat'
import ConversationList from './components/ConversationList.vue'
import ChatHeader from './components/ChatHeader.vue'
import MessageList from './components/MessageList.vue'
import ChatComposer from './components/ChatComposer.vue'
import MediaViewer from './components/MediaViewer.vue'

export default {
    name: 'ChatIndex',
    components: {
        ConversationList,
        ChatHeader,
        MessageList,
        ChatComposer,
        MediaViewer
    },
    data() {
        return {
            viewer: { show: false, mode: 'image', url: '', title: '', poster: '' },
            narrow: false,
            narrowShowRoom: false
        }
    },
    computed: {
        ...mapState(useChatStore, [
            'sessions', 'taskMap', 'currentKey', 'wsState', 'wsDetail',
            'loadingSession', 'loadingEarlier'
        ]),
        ...mapState(useChatStore, [
            'currentMessages', 'currentSession', 'latestOptionsSeq', 'hasMoreEarlier'
        ]),
        title() {
            return sessionTitle(this.currentKey, this.taskMap)
        }
    },
    created() {
        this.chatStore = useChatStore()
    },
    watch: {
        // 跨会话触发迁移自动切换（store.applyMessageMove 只切数据态 currentKey）：
        // 地址栏 query 与窄屏详情视图跟随对齐，与手点列表项同效；手点路径会重复触发一次，
        // syncQuery 幂等（query 相同 no-op）、narrowShowRoom 重复置 true 无害
        currentKey(next, prev) {
            if (next === prev) {
                return
            }
            this.syncQuery(next)
            this.narrowShowRoom = true
        }
    },
    async mounted() {
        this.handleResize()
        window.addEventListener('resize', this.handleResize)
        const store = this.chatStore
        store.initRealtime()
        const target = this.$route.query.session || ''
        await store.openSession(target)
        this.syncQuery(target)
    },
    activated() {
        // keep-alive 返回本页：刷新会话列表与未读（WS 期间可能已变化），
        // 并把页签/地址栏的 query 与当前会话对齐（从页签重新进入时名字相同、query 会丢）
        this.chatStore.refreshSessions()
        this.syncQuery(this.currentKey)
    },
    beforeUnmount() {
        window.removeEventListener('resize', this.handleResize)
    },
    methods: {
        ...mapActions(useChatStore, ['openSession', 'loadEarlier', 'refreshSessions']),
        handleResize() {
            this.narrow = window.innerWidth < 960
        },
        syncQuery(sessionKey) {
            // 只在会话页路由上改写 query：动态路由尚未就绪时本页可能挂在 404 路由上，
            // 此时 replace 会落到根路径（守卫随后会重新导航，别在此处抢先改地址）
            if (this.$route.name !== 'chat-index') {
                return
            }
            const current = this.$route.query.session || ''
            if (current !== sessionKey) {
                this.$router.replace({
                    name: this.$route.name,
                    query: sessionKey ? { session: sessionKey } : {}
                })
            }
        },
        async switchSession(sessionKey) {
            if (sessionKey === this.currentKey) {
                this.narrowShowRoom = true
                return
            }
            await this.chatStore.openSession(sessionKey)
            this.syncQuery(sessionKey)
            this.narrowShowRoom = true
        },
        backToList() {
            this.narrowShowRoom = false
        },
        async refresh() {
            await this.chatStore.refreshSessions()
            await this.chatStore.catchUp()
        },
        /**
         * 会话详情顶栏「删除会话」（2026-09-21 日志删除逻辑调整）：与列表项删除同语义——
         * 只删会话数据与会话窗口（消息记录服务端保留，删除入口收敛到日志中心）。
         * 窄屏单栏视图删除后返回列表；宽屏右侧停留空视图，任务再推送时自动重建。
         */
        async onDeleteCurrentSession() {
            await this.chatStore.deleteSession(this.chatStore.currentKey);
            if (this.narrow) {
                this.backToList();
            }
        },
        /** 列表项悬浮「删除会话」：删会话实体（消息记录保留，2026-09-21 起）；
         *  删除当前会话时右侧停留空视图，任务再推送时自动重建 */
        async onDeleteSession(sessionKey) {
            await this.chatStore.deleteSession(sessionKey)
        },
        async onSend({ content, contentType, contentText }) {
            await this.chatStore.send(content, { contentType, contentText })
        },
        async onPickOption({ message, option }) {
            await this.chatStore.pickOption(message, option)
        },
        onResend(message) {
            this.chatStore.resend(message)
        },
        onOpenMedia({ mode, url, title, poster }) {
            this.viewer = { show: true, mode, url, title, poster: poster || '' }
        },
        /** 「保存到服务器」：发普通文本指令，由 media_saver 任务落盘后回推可播气泡（与 App 一致） */
        onSaveMedia(url) {
            this.chatStore.send(`保存媒体 ${url}`)
        },
        /** 通知卡 jump：会话键 → 直接切会话；外链/任务 → 打开对应位置 */
        onJump(jump) {
            if (!jump) {
                return
            }
            const sessionMatch = /^quantum:\/\/session\/(.+)$/.exec(jump)
            if (sessionMatch) {
                this.switchSession(sessionMatch[1])
                return
            }
            if (/^https?:\/\//i.test(jump)) {
                window.open(jump, '_blank', 'noreferrer')
                return
            }
            window.$message?.info(`跳转目标：${jump}`)
        }
    }
}
</script>

<style scoped>
.chat-page {
    display: flex;
    height: 100%;
    min-height: 0;
    background: var(--bg);
}

.chat-room {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-width: 0;
    min-height: 0;
}

/* 窄屏（<960px）折叠为单栏：列表 ⇄ 详情，等价 App 的两级路由体验 */
.chat-page.narrow.show-room :deep(.conversation-list) {
    display: none;
}

.chat-page.narrow:not(.show-room) .chat-room {
    display: none;
}
</style>
