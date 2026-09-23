<template>
    <div class="docker-volumes">
        <div class="glass-card main-card">
            <div class="header">
                <h2></h2>
                <div class="actions">
                    <n-button @click="refresh" type="primary">
                        <template #icon>
                            <i class="fa-solid fa-arrows-rotate"></i>
                        </template>
                        刷新
                    </n-button>
                </div>
            </div>

            <n-data-table :columns="volumeColumns" :data="volumes" :loading="loading" :scroll-x="800" :bordered="true"
                size="small" />
        </div>
    </div>
</template>

<script>
import { h } from 'vue'
import { renderOpActions, OpColor } from '@/utils/op-actions'
import {
    listVolumes,
    removeVolume
} from '@/api/docker'

export default {
    name: 'DockerVolumes',
    data() {
        return {
            loading: false,
            volumes: [],
            volumeColumns: [
                {
                    title: '名称',
                    key: 'Name',
                    className: 'mono',
                    render: (row) => this.tdTip(h, { row }, 'Name')
                },
                {
                    title: '驱动',
                    key: 'Driver',
                    width: 120
                },
                {
                    title: '挂载点',
                    key: 'Mountpoint',
                    className: 'mono',
                    ellipsis: { tooltip: true },
                    render: (row) => this.tdTip(h, { row }, 'Mountpoint')
                },
                {
                    title: '作用域',
                    key: 'Scope',
                    width: 120
                },
                {
                    title: '创建时间',
                    key: 'CreatedAt',
                    width: 170,
                    className: 'mono',
                    render: (row) => {
                        return h('span', new Date(row.CreatedAt).toLocaleString())
                    }
                },
                {
                    title: '操作',
                    key: 'actions',
                    width: 80,
                    align: 'center',
                    fixed: 'right',
                    render: (row) =>
                        renderOpActions(h, [
                            { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.removeVolume(row) }
                        ])
                }
            ]
        }
    },
    mounted() {
        this.loadVolumes()
    },
    methods: {
        async loadVolumes() {
            const res = await listVolumes()
            this.volumes = res.Volumes || []
        },

        refresh() {
            this.loadVolumes()
        },

        removeVolume(volume) {
            this.$dialog.warning({
                title: '确认删除',
                content: `确定要删除卷 ${volume.Name} 吗？`,
                positiveText: '删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        await removeVolume(volume.Name)
                        this.$message.success('卷删除成功')
                        this.loadVolumes()
                    } catch (err) {
                        console.error('删除卷失败:', err)
                        let errorMessage = '未知错误'
                        if (err.response && err.response.data && err.response.data.message) {
                            errorMessage = err.response.data.message
                        } else if (err.message) {
                            errorMessage = err.message
                        }
                        this.$message.error('删除卷失败: ' + errorMessage)
                    }
                }
            })
        }
    }
}
</script>

<style scoped>
.docker-volumes {
    padding: 16px;
}

.main-card {
    padding: 16px 20px;
}

.header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 16px;
}

.actions {
    display: flex;
    gap: 8px;
}
</style>
