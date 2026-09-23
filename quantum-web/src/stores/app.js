import { defineStore } from 'pinia'
import {
    getBreadCrumbList,
    getMenuByRouter,
    getHomeRoute,
    getNextRoute,
    routeHasExist,
    routeEqual,
    getRouteTitleHandled,
    localRead
} from '@/libs/util'
import { saveErrorLogger } from '@/api/data'
import router from '@/router'
import routers from '@/router/routers'
import config from '@/config'

const { homeName } = config

export const useAppStore = defineStore('app', {
    state: () => ({
        breadCrumbList: [],
        tagNavList: [],
        homeRoute: {},
        dynamicRoutes: [],
        local: localRead('local'),
        errorList: [],
        hasReadErrorPage: false
    }),
    getters: {
        menuList(state) {
            return getMenuByRouter(
                state.dynamicRoutes && state.dynamicRoutes.length
                    ? state.dynamicRoutes
                    : routers,
                ''
            )
        },
        /**
         * 首页（菜单管理 → 默认首页标记）解析结果：菜单路由里 meta.isDefault 的那一项，
         * 未标记时回退静态首页。名称/路径供登录落点、关闭全部页签、页签不可关闭判定共用。
         */
        homePageName(state) {
            return state.homeRoute && state.homeRoute.name ? state.homeRoute.name : ''
        },
        homePagePath(state) {
            if (!state.homeRoute || !state.homeRoute.name) {
                return ''
            }
            // 菜单子路由的 path 是相对父级的（如 'index'），必须经 router 解析成绝对路径
            const resolved = router.resolve({ name: state.homeRoute.name })
            return resolved && resolved.matched && resolved.matched.length ? resolved.fullPath : ''
        },
        errorCount: (state) => state.errorList.length
    },
    actions: {
        setDynamicRoutes(routes) {
            this.dynamicRoutes = routes || []
        },
        setBreadCrumb(route) {
            this.breadCrumbList = getBreadCrumbList(route, this.homeRoute)
        },
        setHomeRoute(routes) {
            this.homeRoute = getHomeRoute(routes, homeName)
        },
        closeTag(route) {
            const tag = this.tagNavList.filter((item) => routeEqual(item, route))[0]
            if (!tag) return
            this.closePage(tag)
        },
        closePage(route) {
            const nextRoute = getNextRoute(this.tagNavList, route)
            this.tagNavList = this.tagNavList.filter((item) => !routeEqual(item, route))
            router.push(nextRoute)
        },
        addTag({ route, type = 'unshift' }) {
            const handled = getRouteTitleHandled(route)
            if (!routeHasExist(this.tagNavList, handled)) {
                if (type === 'push') this.tagNavList.push(handled)
                else {
                    if (handled.name === homeName) this.tagNavList.unshift(handled)
                    else this.tagNavList.splice(1, 0, handled)
                }
            }
        },
        setTagNavList(list) {
            this.tagNavList = list
        },
        addError(error) {
            this.errorList.push(error)
        },
        setHasReadErrorLoggerStatus(status = true) {
            this.hasReadErrorPage = status
        },
        addErrorLog(info) {
            if (!window.location.href.includes('error_logger_page'))
                this.setHasReadErrorLoggerStatus(false)
            const data = {
                ...info,
                time: Date.parse(new Date())
            }
            saveErrorLogger(info).then(() => {
                this.addError(data)
            })
        }
    }
})
