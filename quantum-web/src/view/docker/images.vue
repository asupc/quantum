<template>
    <div class="docker-images">
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
                    <n-button @click="removeSelectedImages" type="error" :disabled="CheckedRowKeys.length === 0">
                        <template #icon>
                            <i class="fa-solid fa-trash-can"></i>
                        </template>
                        删除选中 ({{ CheckedRowKeys.length }})
                    </n-button>
                </div>
            </div>

            <n-data-table :columns="imageColumns" :data="images" :loading="loading" :row-key="(row) => row.Id"
                :checked-row-keys="CheckedRowKeys" @update:checked-row-keys="onCheckedRowKeys" :scroll-x="900"
                :bordered="true" size="small" />
        </div>
    </div>
</template>

<script>
import { h } from 'vue'
import {
    listImages,
    removeImage
} from '@/api/docker'
import { renderOpActions, OpColor } from '@/utils/op-actions'

export default {
    name: 'DockerImages',
    data() {
        return {
            loading: false,
            images: [],
            CheckedRowKeys: [],
            imageColumns: [
                {
                    type: 'selection'
                },
                {
                    title: '仓库',
                    key: 'RepoTags',
                    render: (row) => {
                        const tags = row.RepoTags || []
                        return h('div', { style: { display: 'flex', gap: '4px', flexWrap: 'wrap' } },
                            tags.map(tag => h('span', { class: 'pill off' }, tag)))
                    }
                },
                {
                    title: 'ID',
                    key: 'Id',
                    width: 120,
                    className: 'mono',
                    render: (row) => {
                        // 显示短ID
                        return h('span', row.Id.substring(7, 19))
                    }
                },
                {
                    title: '容器数',
                    key: 'Containers',
                    width: 90
                },
                {
                    title: '创建时间',
                    key: 'Created',
                    width: 170,
                    className: 'mono'
                },
                {
                    title: '大小',
                    key: 'Size',
                    width: 120,
                    className: 'mono',
                    render: (row) => {
                        const size = (row.Size / 1024 / 1024).toFixed(2) // 转换为MB
                        return h('span', `${size} MB`)
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
                            { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.removeImage(row) }
                        ])
                }
            ]
        }
    },
    mounted() {
        this.loadImages()
    },
    methods: {
        onCheckedRowKeys(keys) {
            this.CheckedRowKeys = keys
        },
        async loadImages() {
            const res = await listImages(false)
            this.images = res;
        },

        refresh() {
            this.loadImages()
        },

        resetPullForm() {
            this.pullForm = {
                image: '',
                tag: 'latest'
            }
        },

        removeImage(image) {
            // 获取第一个标签用于显示
            const tagName = image.RepoTags && image.RepoTags.length > 0 ? image.RepoTags[0] : image.Id.substring(7, 19)

            this.$dialog.warning({
                title: '确认删除',
                content: `确定要删除镜像 ${tagName} 吗？`,
                positiveText: '删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        await removeImage(image.Id, false)
                        this.$message.success('镜像删除成功')
                        this.loadImages()
                    } catch (err) {
                        console.error('删除镜像失败:', err)
                        let errorMessage = '未知错误'
                        if (err.response && err.response.data && err.response.data.message) {
                            errorMessage = err.response.data.message
                        } else if (err.message) {
                            errorMessage = err.message
                        }
                        this.$message.error('删除镜像失败: ' + errorMessage)
                    }
                }
            })
        },

        removeSelectedImages() {
            if (this.CheckedRowKeys.length === 0) {
                this.$message.warning('请至少选择一个镜像');
                return;
            }

            const selected = this.images.filter(img => this.CheckedRowKeys.includes(img.Id))
            const imageNames = selected.map(image => {
                return image.RepoTags && image.RepoTags.length > 0 ? image.RepoTags[0] : image.Id.substring(7, 19);
            }).join(', ');

            this.$dialog.warning({
                title: '确认删除',
                content: `确定要删除选中的 ${this.CheckedRowKeys.length} 个镜像吗？${imageNames}`,
                positiveText: '删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        const promises = selected.map(image => removeImage(image.Id, false));
                        await Promise.all(promises);
                        this.$message.success('选中的镜像删除成功');
                        this.CheckedRowKeys = [];
                        this.loadImages();
                    } catch (err) {
                        console.error('删除镜像失败:', err);
                        let errorMessage = '未知错误';
                        if (err.response && err.response.data && err.response.data.message) {
                            errorMessage = err.response.data.message;
                        } else if (err.message) {
                            errorMessage = err.message;
                        }
                        this.$message.error('删除镜像失败: ' + errorMessage);
                        this.loadImages(); // 重新加载以同步状态
                    }
                }
            });
        }
    }
}
</script>

<style scoped>
.docker-images {
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
