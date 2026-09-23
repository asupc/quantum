<template>
    <div class="docker-networks">
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
                    <n-button @click="showCreateModal = true" type="primary">
                        <template #icon>
                            <i class="fa-solid fa-plus"></i>
                        </template>
                        创建网络
                    </n-button>
                </div>
            </div>

            <n-data-table :columns="networkColumns" :data="networks" :loading="loading" :scroll-x="800"
                :bordered="true" size="small" />
        </div>

        <!-- 创建网络模态框 -->
        <n-modal preset="card" v-model:show="showCreateModal" title="创建网络" style="width: 480px"
            @after-leave="resetCreateForm">
            <n-form :model="createForm" label-placement="left" label-width="80">
                <n-form-item label="网络名称">
                    <n-input v-model:value="createForm.name" placeholder="网络名称" />
                </n-form-item>
                <n-form-item label="驱动">
                    <n-select v-model:value="createForm.driver" :options="[
                        { label: 'bridge', value: 'bridge' },
                        { label: 'host', value: 'host' },
                        { label: 'none', value: 'none' }
                    ]" />
                </n-form-item>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="showCreateModal = false" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="createNetwork">确定</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>

<script>
import { h } from 'vue'
import { renderOpActions, OpColor } from '@/utils/op-actions'
import {
    listNetworks,
    createNetwork,
    removeNetwork
} from '@/api/docker'

export default {
    name: 'DockerNetworks',
    data() {
        return {
            loading: false,
            networks: [],
            showCreateModal: false,
            createForm: {
                name: '',
                driver: 'bridge'
            },
            networkColumns: [
                {
                    title: 'ID',
                    key: 'Id',
                    width: 150,
                    className: 'mono',
                    render: (row) => {
                        // 显示短ID
                        return h('span', row.Id.substring(0, 12))
                    }
                },
                {
                    title: '名称',
                    key: 'Name'
                },
                {
                    title: '驱动',
                    width: 150,
                    key: 'Driver'
                },
                {
                    title: '范围',
                    width: 150,
                    key: 'Scope'
                },
                {
                    title: '创建时间',
                    width: 170,
                    key: 'Created',
                    className: 'mono',
                    render: (row) => {
                        return h('span', new Date(row.Created).toLocaleString())
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
                            { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.removeNetwork(row) }
                        ])
                }
            ]
        }
    },
    mounted() {
        this.loadNetworks()
    },
    methods: {
        async loadNetworks() {
            const res = await listNetworks()
            this.networks = res
        },

        refresh() {
            this.loadNetworks()
        },

        resetCreateForm() {
            this.createForm = {
                name: '',
                driver: 'bridge'
            }
        },

        async createNetwork() {
            if (!this.createForm.name) {
                this.$message.error('请输入网络名称')
                return
            }

            try {
                await createNetwork(this.createForm.name, this.createForm.driver)
                this.$message.success('网络创建成功')
                this.resetCreateForm()
                this.showCreateModal = false
                this.loadNetworks()
            } catch (err) {
                console.error('创建网络失败:', err)
                let errorMessage = '未知错误'
                if (err.response && err.response.data && err.response.data.message) {
                    errorMessage = err.response.data.message
                } else if (err.message) {
                    errorMessage = err.message
                }
                this.$message.error('创建网络失败: ' + errorMessage)
            }
        },

        removeNetwork(network) {
            this.$dialog.warning({
                title: '确认删除',
                content: `确定要删除网络 ${network.Name} 吗？`,
                positiveText: '删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        await removeNetwork(network.Id)
                        this.$message.success('网络删除成功')
                        this.loadNetworks()
                    } catch (err) {
                        console.error('删除网络失败:', err)
                        let errorMessage = '未知错误'
                        if (err.response && err.response.data && err.response.data.message) {
                            errorMessage = err.response.data.message
                        } else if (err.message) {
                            errorMessage = err.message
                        }
                        this.$message.error('删除网络失败: ' + errorMessage)
                    }
                }
            })
        }
    }
}
</script>

<style scoped>
.docker-networks {
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
