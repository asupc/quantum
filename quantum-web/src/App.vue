<template>
    <n-config-provider :theme="theme" :theme-overrides="themeOverrides" :locale="zhCN" :date-locale="dateZhCN"
        class="app-provider">
        <n-message-provider>
            <n-dialog-provider>
                <n-notification-provider>
                    <n-loading-bar-provider>
                        <div id="app" class="grid-bg">
                            <router-view />
                        </div>
                    </n-loading-bar-provider>
                </n-notification-provider>
            </n-dialog-provider>
        </n-message-provider>
    </n-config-provider>
</template>

<script>
import { defineComponent } from 'vue'
import { zhCN, dateZhCN } from 'naive-ui'
import { theme, themeOverrides } from '@/theme/naive'
import '@/libs/naive-discrete'

export default defineComponent({
    name: 'App',
    setup() {
        return {
            theme,
            themeOverrides,
            zhCN,
            dateZhCN
        }
    }
})
</script>

<style>
html,
body,
#app {
    width: 100%;
    height: 100%;
    margin: 0;
    padding: 0;
}

html,
body {
    overflow: hidden;
    background: var(--bg);
    color: var(--text);
}

#app {
    position: relative;
    overflow: hidden;
}

/* 网格底纹铺满应用底层（内容层需自身 position:relative 置于其上） */
#app.grid-bg::before {
    z-index: 0;
}

#app>* {
    position: relative;
    z-index: 1;
}

.app-provider,
.n-config-provider {
    height: 100%;
}
</style>
