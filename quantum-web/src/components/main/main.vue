<template>
    <div class="main-console">
        <aside class="sider" :class="{ collapsed }">
            <div class="logo">
                <img class="logo-mark" src="@/assets/images/logo-min.png" alt="Quantum Logo" />
                <div v-show="!collapsed" class="logo-text">
                    <div class="logo-name">量子平台</div>
                    <div class="logo-sub">QUANTUM CONSOLE</div>
                </div>
            </div>
            <n-menu :value="activeMenu" :options="menuOptions" :collapsed="collapsed" :collapsed-width="64"
                :collapsed-icon-size="17" :indent="18" accordion @update:value="turnToPage" />
            <div class="sider-foot" v-show="!collapsed">
                <i class="fa-solid fa-code-branch"></i>
                <span class="mono">{{ ver || '—' }}</span>
            </div>
        </aside>

        <div class="main-body">
            <header-bar :collapsed="collapsed" @on-coll-change="handleCollapsedChange">
                <user />
                <fullscreen style="margin-right: 10px" />
            </header-bar>

            <tags-nav :value="$route" @input="handleClick" :list="tagNavList" @on-close="handleCloseTag" />

            <div class="content-wrapper" :class="{ 'full-bleed': isFullBleed }" id="main-content-wrapper">
                <router-view v-slot="{ Component }">
                    <keep-alive :include="cacheList">
                        <component :is="Component" :key="$route.path" />
                    </keep-alive>
                </router-view>
            </div>
        </div>
    </div>
</template>

<script>
import { h } from 'vue'
import { useAppStore } from '@/stores/app'
import { useChatStore } from '@/stores/chat'
import HeaderBar from './components/header-bar'
import TagsNav from './components/tags-nav'
import User from './components/user'
import Fullscreen from './components/fullscreen'
import FaIcon from '@/components/FaIcon.vue'
import { Version } from '@/api/main.js'
import { loadDynamicRoutes } from '@/router'
import { getRouteTitleHandled, routeEqual } from '@/libs/util'

/**
 * 单页多视图路由（页签唯一）：会话页在同一路由内以 ?session= 切换会话，
 * 页签判重含 query，命中这些路由时页签原位更新而非追加。
 */
const SINGLE_TAG_ROUTES = ['chat-index']

const renderIcon = (iconClass) => {
    if (!iconClass) return undefined
    return () => h(FaIcon, { icon: iconClass, size: 15 })
}

export default {
    name: 'Main',
    components: {
        HeaderBar,
        TagsNav,
        User,
        Fullscreen
    },
    setup() {
        const appStore = useAppStore()
        const chatStore = useChatStore()
        return { appStore, chatStore }
    },
    data() {
        return {
            collapsed: false,
            ver: ''
        }
    },
    computed: {
        activeMenu() {
            return this.$route.name
        },
        /** 会话页自带双栏与滚动容器：去掉内容区默认内边距与滚动条 */
        isFullBleed() {
            return this.$route.name === 'chat-index'
        },
        tagNavList() {
            return this.appStore.tagNavList
        },
        menuOptions() {
            // 静态回退路由的 Main 包装层（无 meta.title）拍平为其子项，避免渲染成无 key 的 Submenu；
            // 后端动态菜单的分组节点均带 title/icon，按分组正常渲染
            const unread = this.chatStore.unreadTotal || 0
            const toOption = (item) => {
                const option = {
                    key: item.name,
                    label: this.renderMenuLabel(item, unread),
                    icon: renderIcon(item.icon)
                }
                if (item.children && item.children.length) {
                    option.children = item.children.map(toOption)
                }
                return option
            }
            const options = []
            this.appStore.menuList.forEach((item) => {
                if (
                    item.children && item.children.length &&
                    (!item.meta || !item.meta.title)
                ) {
                    options.push(...item.children.map(toOption))
                } else {
                    options.push(toOption(item))
                }
            })
            return options
        },
        cacheList() {
            // keep-alive :include 匹配的是**组件 name**（非路由 name）——路由 name 由后台菜单决定且不稳定，
            // 组件 name 经 routers.js 按稳定组件路径写入 meta.componentName，这里据此产出缓存键。
            return this.tagNavList
                .filter((item) => !(item.meta && item.meta.notCache))
                .map((item) => item.meta && item.meta.componentName)
                .filter(Boolean)
        }
    },
    methods: {
        /**
         * 跳首页：以菜单管理标记的「默认首页」为准（如会话页），未标记则回退静态首页。
         * 首页是动态菜单路由时按路径跳转——按名字跳会因路由尚未注册而失败。
         */
        goHome() {
            const path = this.appStore.homePagePath
            if (path) {
                this.$router.push({ path }).catch(() => {})
                return
            }
            this.$router.push({ name: this.$config.homeName }).catch(() => {})
        },
        /** 菜单标签：会话项带未读角标（全局未读大于 0 时常驻） */
        renderMenuLabel(item, unread) {
            const text = (item.meta && item.meta.title) || item.name
            const isChat = item.name === 'chat' || item.name === 'chat-index'
            if (!isChat || !unread) {
                return text
            }
            const isChild = item.name === 'chat-index'
            return () => h('span', { class: 'menu-label' }, [
                h('span', text),
                h('span', { class: isChild ? 'menu-badge' : 'menu-dot' },
                    isChild ? (unread > 99 ? '99+' : String(unread)) : '')
            ])
        },
        turnToPage(key) {
            try {
                this.$router.push({ name: key })
            } catch (e) {
                // 同路由重复跳转忽略
            }
        },
        handleCollapsedChange(state) {
            this.collapsed = state
        },
        handleClick(item) {
            this.turnToPage(item && item.name ? item.name : item)
        },
        handleCloseTag(res, type, route) {
            // 关闭当前页签必须先跳转再删列表：closeTag 要在完整列表里找到被关页签
            // 才能算出相邻页并切换路由，先删列表它就找不到页签而静默放弃，
            // 页面会停留在已关闭的页面上（页签没了但内容不切走）
            if (type !== 'others' && type !== 'all' && routeEqual(this.$route, route)) {
                this.appStore.closeTag(route)
            }
            this.appStore.setTagNavList(res)
            if (type === 'all') {
                this.goHome()
            }
        },
        getVersion() {
            Version().then((res) => {
                this.ver = res.Item1
            })
        }
    },
    watch: {
        $route(newRoute) {
            const { name, query, params, meta } = newRoute
            const route = { name, query, params, meta }
            if (SINGLE_TAG_ROUTES.includes(name)) {
                // 单页多视图的路由（会话页按 ?session= 切换会话）必须只保留一个页签：
                // 页签判重含 query，直接 addTag 会每切一次会话就多一个「会话」标签。
                // 这里原位替换已有页签（保留其位置），没有则插在首页之后。
                const list = [...this.tagNavList]
                const index = list.findIndex((item) => item.name === name)
                const handled = getRouteTitleHandled(route)
                if (index >= 0) {
                    list.splice(index, 1, handled)
                } else {
                    list.splice(1, 0, handled)
                }
                this.appStore.setTagNavList(list)
            } else {
                this.appStore.addTag({ route, type: 'push' })
            }
            this.appStore.setBreadCrumb(newRoute)
        }
    },
    async mounted() {
        this.getVersion()

        // 全局会话实时态：连 WS + 拉会话列表（供侧栏未读角标），失败不阻塞主框架
        this.chatStore.initRealtime().catch(() => {
            // 会话通道不可用不影响其他页面
        })

        try {
            // 进主框架即注册动态菜单路由（与 router.beforeEach 共享同一份 Promise 单点去重），
            // 保证点菜单时 push({name}) 可命中；这里同时把同一份路由写进 store 供菜单渲染。
            const dynamic = await loadDynamicRoutes()

            if (dynamic && Array.isArray(dynamic)) {
                this.appStore.setDynamicRoutes(dynamic)
                this.appStore.setHomeRoute(dynamic)
            }
        } catch (error) {
            // 菜单加载失败不阻塞骨架渲染
        }

        const { name, params, query, meta } = this.$route
        // 静态根路径（首页占位）只是登录/进站的落脚点：若菜单管理里把别的页面标成了「默认首页」，
        // 这里直接转过去，且不为占位页建页签（否则会多出一个用不到的「首页」标签）。
        const isPlaceholderRoot = name === this.$config.homeName
        const homeName = this.appStore.homePageName
        if (isPlaceholderRoot && homeName && homeName !== name) {
            this.goHome()
            return
        }
        this.appStore.addTag({
            route: { name, params, query, meta }
        })
        this.appStore.setBreadCrumb(this.$route)
    }
}
</script>

<style scoped>
.main-console {
    display: flex;
    height: 100%;
    background: var(--bg);
}

/* 侧栏：渐变 0D1526→0B1322 */
.sider {
    width: 236px;
    flex: none;
    background: linear-gradient(180deg, #0d1526 0%, #0b1322 100%);
    border-right: 1px solid var(--line);
    display: flex;
    flex-direction: column;
    padding: 14px 10px 0;
    transition: width .25s cubic-bezier(.4, 0, .2, 1);
    overflow: hidden;
}

.sider.collapsed {
    width: 64px;
}

.logo {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 10px 18px;
    flex: none;
    justify-content: center;
}

.sider:not(.collapsed) .logo {
    justify-content: flex-start;
}

.logo-mark {
    width: 36px;
    height: 36px;
    flex: none;
    border-radius: 9px;
    display: block;
    object-fit: cover;
    box-shadow: 0 4px 14px rgba(0, 0, 0, .45);
}

.logo-name {
    font-weight: 700;
    font-size: 16px;
    color: var(--text);
    white-space: nowrap;
}

.logo-sub {
    font-size: 11px;
    color: var(--text-3);
    letter-spacing: .5px;
    white-space: nowrap;
}

.sider :deep(.n-menu) {
    flex: 1;
    overflow-y: auto;
    overflow-x: hidden;
}

.sider-foot {
    border-top: 1px solid var(--line);
    padding: 10px 12px;
    display: flex;
    align-items: center;
    gap: 10px;
    color: var(--text-3);
    font-size: 12px;
    flex: none;
}

/* 会话菜单未读角标（父级分组用红点，子项用计数胶囊） */
.sider :deep(.menu-label) {
    display: inline-flex;
    align-items: center;
    gap: 6px;
}

.sider :deep(.menu-badge) {
    min-width: 18px;
    height: 18px;
    padding: 0 6px;
    border-radius: 9px;
    background: var(--danger);
    color: #fff;
    font-size: 11px;
    font-weight: 700;
    line-height: 18px;
    text-align: center;
}

.sider :deep(.menu-dot) {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--danger);
    box-shadow: 0 0 8px var(--danger);
}

/* 主区 */
.main-body {
    flex: 1;
    display: flex;
    flex-direction: column;
    min-width: 0;
}

.content-wrapper {
    flex: 1;
    padding: 18px;
    overflow: auto;
    position: relative;
}

.content-wrapper.full-bleed {
    padding: 0;
    overflow: hidden;
}
</style>
