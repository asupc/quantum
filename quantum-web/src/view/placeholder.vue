<template>
    <div class="placeholder">
        <div class="glass-card demo">
            <h2>{{ title }}</h2>
            <p class="mono">量子助手 · Vue 3 + Naive UI 深色主题（M2 设计系统演示）</p>
            <div class="row">
                <n-button type="primary">主要操作</n-button>
                <n-button>次要</n-button>
                <n-button type="error">危险</n-button>
                <span class="pill ok"><span class="dot"></span>运行中</span>
                <span class="pill warn"><span class="dot"></span>已禁用</span>
            </div>
            <n-data-table :columns="columns" :data="data" :pagination="false" size="small" />
        </div>
    </div>
</template>

<script>
import { h } from 'vue'

export default {
    name: 'Placeholder',
    computed: {
        title() {
            return (this.$route && this.$route.meta && this.$route.meta.title) || '页面'
        }
    },
    data() {
        return {
            columns: [
                { title: '任务名称', key: 'name' },
                { title: 'Cron', key: 'cron', className: 'mono' },
                {
                    title: '状态',
                    key: 'status',
                    render(row) {
                        return h('span', { class: row.ok ? 'pill ok' : 'pill warn' }, [
                            h('span', { class: 'dot' }),
                            row.ok ? '启用' : '禁用'
                        ])
                    }
                }
            ],
            data: [
                { name: '每日数据备份', cron: '0 2 * * *', ok: true },
                { name: '库存监控推送', cron: '*/10 * * * *', ok: false }
            ]
        }
    }
}
</script>

<style scoped>
.placeholder {
    height: 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--bg);
}

.demo {
    width: 640px;
    padding: 28px;
}

.demo h2 {
    color: var(--text);
    margin: 0 0 6px;
}

.demo p {
    margin: 0 0 16px;
}

.row {
    display: flex;
    align-items: center;
    gap: 10px;
    margin-bottom: 16px;
}
</style>
