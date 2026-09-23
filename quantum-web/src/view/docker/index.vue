<template>
    <div class="docker-management">
        <div class="glass-card main-card">
            <div class="header">
                <h2>Docker 管理中心</h2>
                <p>管理您的 Docker 容器、镜像、网络和卷</p>
            </div>

            <div class="dashboard">
                <div class="stats-grid">
                    <div class="stat">
                        <div class="stat-k">
                            <span class="badge b-cyan"><i class="fa-brands fa-docker" style="font-size: 16px"></i></span>
                            容器总数
                        </div>
                        <div class="stat-v mono">{{ containers.length }}</div>
                    </div>
                    <div class="stat">
                        <div class="stat-k">
                            <span class="badge b-green"><i class="fa-solid fa-play" style="font-size: 14px"></i></span>
                            运行中
                        </div>
                        <div class="stat-v mono">{{ runningContainers.length }}</div>
                    </div>
                    <div class="stat">
                        <div class="stat-k">
                            <span class="badge b-amber"><i class="fa-regular fa-images" style="font-size: 14px"></i></span>
                            镜像数量
                        </div>
                        <div class="stat-v mono">{{ images.length }}</div>
                    </div>
                    <div class="stat">
                        <div class="stat-k">
                            <span class="badge b-red"><i class="fa-solid fa-circle-nodes" style="font-size: 14px"></i></span>
                            网络数量
                        </div>
                        <div class="stat-v mono">{{ networks.length }}</div>
                    </div>
                </div>
            </div>

            <div class="quick-actions">
                <h3>快捷操作</h3>
                <div class="actions-grid">
                    <div class="action-item" @click="$router.push('/docker/containers')">
                        <i class="fa-brands fa-docker"></i>
                        <span>容器管理</span>
                    </div>

                    <div class="action-item" @click="$router.push('/docker/images')">
                        <i class="fa-regular fa-images"></i>
                        <span>镜像管理</span>
                    </div>

                    <div class="action-item" @click="$router.push('/docker/networks')">
                        <i class="fa-solid fa-circle-nodes"></i>
                        <span>网络管理</span>
                    </div>

                    <div class="action-item" @click="$router.push('/docker/volumes')">
                        <i class="fa-solid fa-folder"></i>
                        <span>卷管理</span>
                    </div>
                </div>
            </div>
        </div>
    </div>
</template>

<script>
import { listContainers, listImages, listNetworks } from '@/api/docker'

export default {
    name: 'DockerManagement',
    data() {
        return {
            containers: [],
            images: [],
            networks: [],
            loading: false
        }
    },
    computed: {
        runningContainers() {
            return this.containers.filter(c => c.State === 'running')
        }
    },
    mounted() {
        this.loadData()
    },
    methods: {
        async loadData() {
            // 并行加载统计数据
            const [containersRes, imagesRes, networksRes] = await Promise.all([
                listContainers(true),
                listImages(false),
                listNetworks()
            ])
            this.containers = containersRes || []
            this.images = imagesRes || []
            this.networks = networksRes || []
        }
    }
}
</script>

<style scoped>
.docker-management {
    padding: 16px;
}

.main-card {
    padding: 24px;
}

.header {
    margin-bottom: 24px;
}

.header h2 {
    margin-bottom: 8px;
    color: var(--text);
}

.header p {
    color: var(--text-2);
    font-size: 13px;
}

.stats-grid {
    display: grid;
    grid-template-columns: repeat(4, 1fr);
    gap: 14px;
    margin-bottom: 24px;
}

.stat {
    background: linear-gradient(180deg, var(--card-2) 0%, var(--card) 100%);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    padding: 16px;
}

.stat-k {
    color: var(--text-2);
    font-size: 12px;
    display: flex;
    align-items: center;
    gap: 8px;
}

.stat-v {
    font-size: 26px;
    font-weight: 700;
    margin-top: 8px;
    color: var(--text);
}

.badge {
    width: 30px;
    height: 30px;
    border-radius: 9px;
    display: flex;
    align-items: center;
    justify-content: center;
}

.b-cyan {
    background: var(--accent-weak);
    color: var(--accent);
}

.b-green {
    background: rgba(52, 211, 153, .12);
    color: var(--ok);
}

.b-amber {
    background: rgba(251, 191, 36, .12);
    color: var(--warn);
}

.b-red {
    background: rgba(248, 113, 113, .12);
    color: var(--danger);
}

.quick-actions h3 {
    margin-bottom: 16px;
    color: var(--text);
}

.actions-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
    gap: 16px;
}

.action-item {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: 20px;
    border-radius: 8px;
    background: var(--card-2);
    border: 1px solid var(--line);
    cursor: pointer;
    transition: all 0.3s;
    color: var(--text-2);
}

.action-item:hover {
    border-color: rgba(34, 211, 238, .45);
    color: var(--accent);
    transform: translateY(-2px);
    box-shadow: 0 0 20px rgba(34, 211, 238, .12);
}

.action-item i {
    margin-bottom: 8px;
    font-size: 24px;
}

.action-item span {
    font-size: 14px;
}
</style>
