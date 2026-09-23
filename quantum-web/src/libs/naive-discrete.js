import { createDiscreteApi } from 'naive-ui'
import { theme, themeOverrides } from '@/theme/naive'

// 非组件上下文（axios 拦截器等）使用的全局消息/弹窗/加载条，
// 与 App.vue 内的 provider 使用同一套主题；同时挂到 window 供存量代码 this.$message 调用
const { message, dialog, notification, loadingBar } = createDiscreteApi(
    ['message', 'dialog', 'notification', 'loadingBar'],
    {
        configProviderProps: {
            theme,
            themeOverrides
        }
    }
)

window.$message = message
window.$dialog = dialog
window.$notification = notification
window.$loadingBar = loadingBar

export { message, dialog, notification, loadingBar }
