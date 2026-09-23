export default {
    title: '量子助手',
    cookieExpires: 1,
    baseUrl: {
        // dev 用相对路径走 vite proxy（vite.config.js /api → 5088），避免后端生产模式 CORS 白名单拦截
        dev: ''
    },
    homeName: 'home-index',
    plugin: {
        mixin: true
    }
}
