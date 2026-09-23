import { GetMenus } from '@/api/menu'

// Main 布局懒加载：避免 routers.js → components/main → main.vue → @/router 的循环依赖
//（该循环曾致 HMR 报 "Cannot access 'Main' before initialization" 并触发页面整页重载竞态）
const Main = () => import('@/components/main')

// Vite 不支持运行时拼接的 import(`@/view/${path}.vue`)（会被展开为全量 glob，
// 把未迁移的 Vue2 页一并卷进编译）。改为显式注册表：M4 每迁移一批页面在此登记，
// 后端菜单 component 字符串 → 懒加载函数；未登记的组件渲染占位页。
const VIEW_COMPONENTS = {
    // M4 批3：navigation 首页看板
    'navigation/index': () => import('@/view/navigation/index.vue'),
    // Web 会话页（复用 App 会话通道：WS 实时 + REST 首屏/补拉，见 docs/Web端会话交互与UI预览方案.md）
    'chat/index': () => import('@/view/chat/index.vue'),
    // M4 批1
    'database-migration/index': () => import('@/view/database-migration/index.vue'),
    'logs/index': () => import('@/view/logs/index.vue'),
    'setting/menu': () => import('@/view/setting/menu.vue'),
    // M4 批2
    'env/index': () => import('@/view/env/index.vue'),
    'open-trigger-task/index': () => import('@/view/open-trigger-task/index.vue'),
    'replay/index': () => import('@/view/replay/index.vue'),
    'custom-data/index': () => import('@/view/custom-data/index.vue'),
    'custom-data/title': () => import('@/view/custom-data/title.vue'),
    // M4 批4（提前）：脚本编辑整页
    'taskScripts/index': () => import('@/view/taskScripts/index.vue'),
    // 脚本版本管理（历史快照查看/对比/回滚，AI 脚本修复 Agent 计划阶段一）
    'taskScripts/versions': () => import('@/view/taskScripts/versions.vue'),
    // AI 助手：供应商与模型配置（阶段二；设置拆到 ai/setting，本页只管供应商/模型）
    'ai/providers': () => import('@/view/ai/providers.vue'),
    // AI 助手：全局设置与 AI 写权限（自 providers 页拆出）
    'ai/setting': () => import('@/view/ai/setting.vue'),
    // AI 助手：会话（Agent 分析/提案，阶段三/四）
    'ai/index': () => import('@/view/ai/index.vue'),
    // M4 批3
    'task/index': () => import('@/view/task/index.vue'),
    // M4 批4
    'setting/index': () => import('@/view/setting/index.vue'),
    'docker/index': () => import('@/view/docker/index.vue'),
    'docker/containers': () => import('@/view/docker/containers.vue'),
    'docker/images': () => import('@/view/docker/images.vue'),
    'docker/networks': () => import('@/view/docker/networks.vue'),
    'docker/volumes': () => import('@/view/docker/volumes.vue')
}

// 组件路径 → 组件自身 `name`（Options API 的 name 选项）。keep-alive 的 :include 只匹配**组件 name**、
// 不匹配路由 name，而路由 name 由后台菜单配置决定（可被改名、不稳定），故以稳定的组件路径为键登记组件名，
// 在 mapMenuItemToRoute 里写进 route.meta.componentName，main.vue 的 cacheList 据此产出 include 列表。
// 未登记的组件（taskScripts 编辑器、placeholder 占位页）不缓存。
const VIEW_COMPONENT_NAMES = {
    'navigation/index': 'NavigationPage',
    'chat/index': 'ChatIndex',
    'database-migration/index': 'DatabaseMigration',
    'logs/index': 'LogsIndex',
    'setting/menu': 'SettingsMenu',
    'env/index': 'EnvIndex',
    'open-trigger-task/index': 'OpenTriggerTaskIndex',
    'replay/index': 'ReplayIndex',
    'custom-data/index': 'CustomDataIndex',
    'custom-data/title': 'CustomDataTitle',
    'task/index': 'TaskIndex',
    'taskScripts/versions': 'ScriptVersions',
    'ai/providers': 'AiProviders',
    'ai/setting': 'AiSetting',
    'ai/index': 'AiIndex',
    'setting/index': 'SettingsIndex',
    'docker/index': 'DockerManagement',
    'docker/containers': 'DockerContainers',
    'docker/images': 'DockerImages',
    'docker/networks': 'DockerNetworks',
    'docker/volumes': 'DockerVolumes'
}

// 已移除功能对应的菜单 component：库中旧菜单（menu.json 仅空库时重播）由前端统一过滤，
// 父级子项清空后一并剔除。青龙管理、指令分组、Docker 系统信息、更新日志均已整体移除。
const REMOVED_MENU_COMPONENTS = [
    'qinglong/',
    'taskGroup/index',
    'docker/system',
    'versions/index'
]

function filterRemovedMenus(items) {
    if (!Array.isArray(items)) return items
    return items
        .filter((item) => {
            const comp = item.component && String(item.component)
            return !comp || !REMOVED_MENU_COMPONENTS.some((c) => comp === c || comp.startsWith(c))
        })
        .map((item) => {
            if (item.children && Array.isArray(item.children)) {
                const children = filterRemovedMenus(item.children)
                return { ...item, children }
            }
            return item
        })
        .filter((item) => !(item.children && item.children.length === 0 && item.component === 'Main'))
}

// 将后端菜单转换为 vue-router route record
function mapMenuItemToRoute(item) {
    const loader =
        item.component && item.component !== 'Main'
            ? VIEW_COMPONENTS[item.component] ||
              (() => import('@/view/placeholder.vue'))
            : Main
    const meta = { ...(item.meta || {}) }
    // 脚本编辑页含 monaco 实例，不进 keep-alive（每次进入都是全新实例）
    if (item.component === 'taskScripts/index') {
        meta.notCache = true
    }
    // keep-alive 缓存键：把组件自身 name 挂到 meta，main.vue 的 cacheList 据此产出 :include
    if (item.component && item.component !== 'Main') {
        meta.componentName = VIEW_COMPONENT_NAMES[item.component]
    }
    const route = {
        path: item.path,
        name: item.name,
        meta,
        component: loader
    }
    if (item.children && Array.isArray(item.children) && item.children.length) {
        route.children = item.children.map(mapMenuItemToRoute)
    }
    return route
}

// 从后端获取菜单并转换为路由数组，失败时返回空数组
export async function fetchMenuRoutes() {
    try {
        const menu = await GetMenus()

        if (!menu || !Array.isArray(menu)) {
            return []
        }
        return filterRemovedMenus(menu).map(mapMenuItemToRoute)
    } catch (error) {
        return []
    }
}

export default [
    {
        path: '/login',
        name: 'login',
        meta: { title: '登录', hideInMenu: true },
        component: () => import('@/view/login/login.vue')
    },
    {
        path: '/',
        component: Main,
        children: [
            {
                path: '/',
                name: 'home-index',
                meta: {
                    title: '首页',
                    icon: 'fa-solid fa-file-lines',
                    componentName: 'NavigationPage'
                },
                component: VIEW_COMPONENTS['navigation/index']
            }
        ]
    },
    {
        path: '/401',
        name: 'error_401',
        meta: {
            title: '401',
            hideInMenu: true
        },
        component: () => import('@/view/error-page/401.vue')
    },
    {
        path: '/500',
        name: 'error_500',
        meta: {
            title: '500',
            hideInMenu: true
        },
        component: () => import('@/view/error-page/500.vue')
    },
    {
        path: '/:pathMatch(.*)*',
        name: 'error_404',
        meta: {
            title: '404',
            hideInMenu: true
        },
        component: () => import('@/view/error-page/404.vue')
    }
]
