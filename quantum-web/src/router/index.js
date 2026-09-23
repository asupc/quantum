import { createRouter, createWebHashHistory } from 'vue-router'
import routes, { fetchMenuRoutes } from './routers'
import { getToken } from '@/libs/util'
import { loadingBar } from '@/libs/naive-discrete'
import config from '@/config'

const { homeName } = config

const router = createRouter({
    routes,
    history: createWebHashHistory(),
    base: ''
})

const LOGIN_PAGE_NAME = 'login'

// 动态菜单路由只加载一次：多个导航共享同一份 Promise，避免重复请求与重复 addRoute
export let dynamicRoutesTask = null

export function loadDynamicRoutes() {
    if (!dynamicRoutesTask) {
        dynamicRoutesTask = fetchMenuRoutes()
            .then((routes) => {
                routes.forEach((route) => {
                    // 已注册的路由跳过（同一路由刷新/多次导航时 addRoute 会静默覆盖，显式去重更稳）
                    const resolved = router.resolve(route.path)
                    if (
                        resolved.name === 'error_404' ||
                        resolved.matched.length === 0
                    ) {
                        router.addRoute(route)
                    }
                })
                return routes
            })
            .catch((error) => {
                console.error('加载动态路由失败:', error)
                dynamicRoutesTask = null // 失败允许重试
                return []
            })
    }
    return dynamicRoutesTask
}

/**
 * 强制重载菜单路由（菜单管理里改了「默认首页」等标记后调用）：
 * 丢掉缓存重新拉取并按名字覆盖注册，避免要刷新整页才生效。
 */
export function reloadDynamicRoutes() {
    dynamicRoutesTask = null
    return loadDynamicRoutes()
}

router.beforeEach(async (to) => {
    loadingBar.start()
    const token = getToken()

    // 检查目标路由是否匹配到有效路由（不是 404）
    const matchResult = router.resolve(to.fullPath)
    const is404Route =
        matchResult.name === 'error_404' || matchResult.matched.length === 0

    // 已登录且目标未知 → 加载一次动态路由；加载后若能解析到真实路由则按原路径重新导航
    //（Promise 单点去重，不会反复请求；解析不到不再重导航，避免死循环）
    if (token && is404Route) {
        const dynamic = await loadDynamicRoutes()
        if (dynamic.length) {
            const rematched = router.resolve(to.fullPath)
            if (rematched.name !== 'error_404' && rematched.matched.length > 0) {
                return { path: to.path, query: to.query, hash: to.hash, replace: true }
            }
        }
    }

    if (!token && to.name !== LOGIN_PAGE_NAME) {
        // 未登录且要跳转的页面不是登录页
        return { name: LOGIN_PAGE_NAME }
    } else if (token && to.name === LOGIN_PAGE_NAME) {
        // 已登录且要跳转的页面是登录页 → 回根路径，由主框架按「默认首页」标记解析最终落点
        //（首页可能是动态菜单路由，此处尚未注册，不能按名字跳）
        return { path: '/' }
    }
    return true
})

router.afterEach((to) => {
    document.title = to.meta && to.meta.title ? `${to.meta.title} - 量子助手` : '量子助手'
    loadingBar.finish()
    window.scrollTo(0, 0)
})

export default router
