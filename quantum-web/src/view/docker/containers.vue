<template>
    <div class="docker-containers">
        <div class="glass-card main-card">
            <n-tabs type="line" v-model:value="tab" @update:value="handleTabChange">
                <n-tab name="running" tab="运行中"></n-tab>
                <n-tab name="all" tab="全部"></n-tab>
            </n-tabs>

            <div class="view-toggle">
                <n-input v-model:value="searchText" placeholder="请输入容器名称或镜像名称进行搜索" clearable
                    style="width: 300px; margin-right: 20px;" />
                <n-radio-group v-model:value="viewMode" @update:value="handleViewModeChange">
                    <n-radio-button value="table">表格视图</n-radio-button>
                    <n-radio-button value="grid">网格视图</n-radio-button>
                </n-radio-group>
            </div>

            <!-- 表格视图 -->
            <n-data-table v-if="viewMode === 'table'" :columns="containerColumns" :data="filteredContainers"
                :scroll-x="1100" :bordered="true" size="small" />

            <!-- 网格视图 -->
            <div v-if="viewMode === 'grid'" class="grid-view">
                <div class="container-grid">
                    <div v-for="(container, index) in filteredContainers" :key="container.Id" class="container-card"
                        :class="{ 'container-running': container.State === 'running', 'container-stopped': container.State !== 'running' }">
                        <div class="card-header">
                            <div class="container-name" :title="getContainerName(container)">
                                {{ getContainerName(container) }}
                            </div>
                            <span class="pill" :class="container.State === 'running' ? 'ok' : 'danger'">
                                <span class="dot"></span>
                                {{ container.State === 'running' ? '运行中' : container.State === 'exited' ? '已停止' :
                                    container.State }}
                            </span>
                        </div>

                        <div class="card-body">
                            <div class="container-image mono" :title="container.Image">
                                <strong>镜像:</strong> {{ container.Image }}
                            </div>

                            <div class="container-ports" v-if="container.Ports && container.Ports.length > 0">
                                <strong>端口:</strong> {{ formatPorts(container.Ports) }}
                            </div>

                            <div class="container-resources" v-if="container.State === 'running'">
                                <div class="resource-item">
                                    <i class="fa-solid fa-gauge-high" :class="getCpuClass(container)"></i>
                                    <span class="resource-label">CPU:</span>
                                    <span class="resource-value mono">{{ container.CPU || '0%' }}</span>
                                </div>
                                <div class="resource-item">
                                    <i class="fa-solid fa-chart-column" :class="getMemoryClass(container)"></i>
                                    <span class="resource-label">内存:</span>
                                    <span class="resource-value mono">{{ container.Memory || '0%' }}</span>
                                </div>
                            </div>

                            <div class="container-created">
                                <strong>创建时间:</strong> {{ container.Created }}
                            </div>
                        </div>

                        <div class="card-footer">
                            <n-space size="small" class="action-buttons">
                                <n-button v-if="container.State === 'running'" @click.stop="stopContainer(container)"
                                    type="warning" size="small">
                                    停止
                                </n-button>
                                <n-button v-if="container.State !== 'running'" @click.stop="startContainer(container)"
                                    type="success" size="small">
                                    启动
                                </n-button>
                                <n-button @click.stop="restartContainer(container)" type="primary" size="small">
                                    重启
                                </n-button>
                                <n-button @click.stop="showStats(container)" type="info" size="small">
                                    详情
                                </n-button>
                                <n-button @click.stop="showLogs(container)" type="info" size="small">
                                    日志
                                </n-button>
                                <n-button @click.stop="removeContainer(container)" type="error" size="small">
                                    删除
                                </n-button>
                            </n-space>
                        </div>
                    </div>

                    <!-- 无容器提示 -->
                    <div v-if="filteredContainers.length === 0" class="no-containers">
                        暂无容器数据
                    </div>
                </div>
            </div>
        </div>

        <!-- 容器统计信息模态框 -->
        <n-modal preset="card" v-model:show="showStatsModal"
            :title="'容器详细信息 - ' + (currentStatsContainer ? getContainerName(currentStatsContainer) : '')"
            style="width: 85%" @after-enter="handleStatsModalVisibleChange(true)"
            @after-leave="handleStatsModalVisibleChange(false)">
            <div v-if="containerStats" class="stats-container">

                <div class="stats-section">
                    <div class="stat-item">
                        <span class="label">名称:</span>
                        <span class="value">{{ getContainerNameFromStats(containerStats) }}</span>
                    </div>
                    <div class="stat-item">
                        <span class="label">镜像:</span>
                        <span class="value mono">{{ containerStats.Config ? containerStats.Config.Image : 'N/A' }}</span>
                    </div>
                    <div class="stat-item">
                        <span class="label">状态:</span>
                        <span class="value">{{ (containerStats.State && containerStats.State.Status) ?
                            containerStats.State.Status : '未知' }}</span>
                    </div>
                    <div class="stat-item">
                        <span class="label">创建时间:</span>
                        <span class="value mono">{{ containerStats.Created }}</span>
                    </div>
                    <div class="stat-item">
                        <span class="label">启动时间:</span>
                        <span class="value mono">{{ containerStats.State ? containerStats.State.StartedAt : 'N/A'
                        }}</span>
                    </div>
                </div>
                <div class="stats-section" v-if="containerStats.State.Running">
                    <h3>资源使用情况</h3>
                    <div class="charts-container">
                        <div ref="cpuChart" class="chart-container"></div>
                        <div ref="memoryChart" class="chart-container"></div>
                    </div>
                </div>
                <div class="stats-section">
                    <h3>端口映射</h3>

                    <div class="port-grid">
                        <div class="stat-item"
                            v-for="(ports, privatePort) in (containerStats.NetworkSettings ? containerStats.NetworkSettings.Ports : {})"
                            :key="privatePort">
                            <span class="label">{{ privatePort }}:</span>
                            <span class="value mono">{{ Array.isArray(ports) && ports.length > 0 ? ports[0].HostIp +
                                ':' + ports[0].HostPort : '未映射' }}</span>
                        </div>
                    </div>
                    <div
                        v-if="!containerStats.NetworkSettings || !containerStats.NetworkSettings.Ports || Object.keys(containerStats.NetworkSettings.Ports).length === 0">
                        <span class="value">无端口映射</span>
                    </div>
                </div>

                <div class="stats-section">
                    <h3>挂载信息</h3>
                    <div class="stat-item" v-for="(mount, index) in (containerStats.Mounts || [])" :key="index">
                        <span class="label">{{ mount.Type }}:</span>
                        <span class="value mono">{{ mount.Source }} -> {{ mount.Destination }} ({{ mount.RW ? '读写' :
                            '只读' }})</span>
                    </div>
                    <div v-if="!containerStats.Mounts || containerStats.Mounts.length === 0">
                        <span class="value">无挂载卷</span>
                    </div>
                </div>
                <div class="stats-section">
                    <h3>环境变量</h3>
                    <div class="env-grid">
                        <span class="stat-item" v-for="(env, index) in (containerStats.Config ? containerStats.Config.Env : [])"
                            :key="index">
                            <span class="value mono">{{ env }}</span>
                        </span>
                    </div>
                    <div v-if="!containerStats.Config || !containerStats.Config.Env || containerStats.Config.Env.length === 0">
                        <span class="value">无环境变量</span>
                    </div>
                </div>
                <div class="stats-section">
                    <h3>命令信息</h3>
                    <div class="stat-item">
                        <span class="label">入口点:</span>
                        <span class="value mono">{{ containerStats.Config && containerStats.Config.Entrypoint ?
                            containerStats.Config.Entrypoint.join(' ') : '无' }}</span>
                    </div>
                    <div class="stat-item">
                        <span class="label">命令:</span>
                        <span class="value mono">{{ containerStats.Config && containerStats.Config.Cmd ?
                            containerStats.Config.Cmd.join('') : '无' }}</span>
                    </div>
                </div>

            </div>
            <div v-else class="no-stats">
                暂无详细信息
            </div>
        </n-modal>

        <!-- 容器日志模态框 -->
        <n-modal preset="card" v-model:show="showLogsModal" title="容器日志" style="width: 90%">
            <div class="logs-container">
                <div class="logs-actions">
                    <h2></h2>
                    <n-button @click="loadLogs(currentLogContainer)" type="primary">刷新</n-button>
                </div>
                <div class="logs-content" ref="logsContent">
                    <pre>{{ containerLogs }}</pre>
                </div>
            </div>
        </n-modal>
    </div>
</template>

<script>
import { h, markRaw } from 'vue'
import {
    listContainers,
    startContainer,
    stopContainer,
    restartContainer,
    removeContainer,
    getContainerLogs,
    getContainerStats,
    inspectContainer,
    getContainersStats
} from '@/api/docker'
import * as echarts from 'echarts'
import { renderOpActions, OpColor } from '@/utils/op-actions'

export default {
    name: 'DockerContainers',
    data() {
        return {
            tab: 'running',
            viewMode: 'table',
            searchText: '',
            containers: [],
            showCreateModal: false,
            showLogsModal: false,
            showStatsModal: false,
            statsLoading: false,
            containerLogs: '',
            currentLogContainer: null,
            currentStatsContainer: null,
            containerStats: null,
            followLogs: false,
            logRefreshTimer: null,
            statsRefreshTimer: null,
            // 历史数据
            cpuHistory: [],
            memoryHistory: [],
            cpuChart: null,
            memoryChart: null,
            resourceUsageTimer: null,
            previousCpuStats: {},
            createForm: {
                image: '',
                name: '',
                cmd: ''
            },
            containerColumns: [
                {
                    title: '名称',
                    key: 'Names',
                    minWidth: 140,
                    render: (row) => {
                        const names = row.Names.map(name => name.startsWith('/') ? name.substring(1) : name)
                        return h('span', names.join(', '))
                    }
                },
                {
                    title: '镜像',
                    key: 'Image',
                    minWidth: 180,
                    ellipsis: { tooltip: true },
                    className: 'mono',
                    render: (row) => this.tdTip(h, { row }, 'Image')
                },
                {
                    title: '状态',
                    key: 'Status',
                    width: 160,
                    render: (row) => {
                        return h('span', {
                            title: row.Status,
                            class: 'pill ' + (row.State === 'running' ? 'ok' : row.State === 'exited' ? 'danger' : 'off')
                        }, [
                            h('span', { class: 'dot' }),
                            row.Status
                        ])
                    }
                },
                {
                    title: '资源使用',
                    key: 'resource',
                    width: 140,
                    render: (row) => {
                        if (row.State !== 'running') {
                            return h('div', '-')
                        }
                        return h('div', { class: 'resource-container' }, [
                            h('div', { class: 'resource-item' }, [
                                h('i', { class: 'fa-solid fa-gauge-high ' + this.getCpuClass(row) }),
                                h('span', { class: 'resource-text mono' }, row.CPU || '0%')
                            ]),
                            h('div', { class: 'resource-item' }, [
                                h('i', { class: 'fa-solid fa-chart-column ' + this.getMemoryClass(row) }),
                                h('span', { class: 'resource-text mono' }, row.Memory || '0%')
                            ])
                        ])
                    }
                },
                {
                    title: '创建时间',
                    key: 'Created',
                    width: 170,
                    className: 'mono'
                },
                {
                    title: '端口',
                    ellipsis: { tooltip: true },
                    key: 'Ports',
                    minWidth: 160,
                    className: 'mono',
                    render: (row) => {
                        if (!row.Ports || row.Ports.length === 0) {
                            return h('span', '-')
                        }
                        const ports = row.Ports.map(port => {
                            if (port.PublicPort) {
                                return `${port.PublicPort}:${port.PrivatePort}/${port.Type}`
                            } else {
                                return `${port.PrivatePort}/${port.Type}`
                            }
                        })
                        return h('span', ports.join(', '))
                    }
                },
                {
                    title: '操作',
                    key: 'actions',
                    width: 160,
                    align: 'center',
                    fixed: 'right',
                    render: (row) =>
                        renderOpActions(h, [
                            row.State === 'running'
                                ? { icon: 'fa-stop', title: '停止', color: OpColor.Edit, onClick: () => this.stopContainer(row) }
                                : { icon: 'fa-play', title: '启动', color: OpColor.Run, onClick: () => this.startContainer(row) },
                            { icon: 'fa-arrows-rotate', title: '重启', color: OpColor.Info, onClick: () => this.restartContainer(row) },
                            { icon: 'fa-circle-info', title: '详情', color: OpColor.Info, onClick: () => this.showStats(row) },
                            { icon: 'fa-file-lines', title: '日志', color: OpColor.Info, onClick: () => this.showLogs(row) },
                            { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.removeContainer(row) }
                        ])
                }
            ]
        }
    },
    computed: {
        filteredContainers() {
            let filtered = [];

            // 根据标签页筛选容器
            if (this.tab === 'running') {
                filtered = this.containers.filter(container => container.State === 'running');
            } else {
                filtered = [...this.containers];
            }

            // 如果有搜索文本，则进一步筛选
            if (this.searchText) {
                const searchText = this.searchText.toLowerCase();
                filtered = filtered.filter(container => {
                    // 检查容器名称
                    const nameMatch = container.Names && container.Names.some(name =>
                        name.toLowerCase().includes(searchText)
                    );

                    // 检查镜像名称
                    const imageMatch = container.Image && container.Image.toLowerCase().includes(searchText);

                    return nameMatch || imageMatch;
                });
            }

            return filtered;
        }
    },
    mounted() {
        this.loadContainers()
        // 设置定时刷新资源使用情况，每5秒刷新一次
        this.startResourceUsagePolling();
    },
    activated() {
        // keep-alive 缓存命中后 mounted 不再跑，返回本页时重启 5s 轮询并立即各拉一次
        this.loadContainers();
        this.startResourceUsagePolling();
        // 资源详情弹窗若仍开着，恢复其每秒刷新
        if (this.showStatsModal) {
            this.startStatsRefresh();
        }
    },
    deactivated() {
        // 页签切走：停掉资源轮询，避免隐藏页持续打后端（keep-alive 常驻后 mounted 里的定时器不会自清）
        if (this.resourceUsageTimer) {
            clearInterval(this.resourceUsageTimer)
            this.resourceUsageTimer = null
        }
        // 隐藏时同样停掉详情弹窗的每秒轮询（弹窗状态随 keep-alive 常驻，回调会误判为可见继续打）
        this.stopStatsRefresh();
    },
    beforeUnmount() {
        if (this.logRefreshTimer) {
            clearInterval(this.logRefreshTimer)
        }
        // 清除stats刷新定时器
        this.stopStatsRefresh();
        // 清除资源使用情况定时器
        if (this.resourceUsageTimer) {
            clearInterval(this.resourceUsageTimer)
        }
        // 清除保存的CPU统计数据
        this.previousCpuStats = {};
        this.destroyCharts();
    },
    methods: {
        startResourceUsagePolling() {
            // 幂等重启：先清旧定时器，立即拉一次再进入 5s 周期（activated 首帧紧随 mounted，避免叠加）
            if (this.resourceUsageTimer) {
                clearInterval(this.resourceUsageTimer)
            }
            this.loadResourceUsage();
            this.resourceUsageTimer = setInterval(() => {
                this.loadResourceUsage();
            }, 5000);
        },
        getCpuClass(container) {
            if (!container.CPU) return '';
            const cpuPercentMatch = container.CPU.match(/^(\d+\.?\d*)%/);
            if (!cpuPercentMatch) return '';
            const cpuPercent = parseFloat(cpuPercentMatch[1]);
            if (isNaN(cpuPercent)) return '';
            return cpuPercent > 80 ? 'high-usage' : cpuPercent > 50 ? 'medium-usage' : 'low-usage';
        },

        getMemoryClass(container) {
            if (!container.Memory) return '';
            const memoryMatch = container.Memory.match(/^([\d.]+)\s*(\w+)/);
            if (!memoryMatch) return '';

            const value = parseFloat(memoryMatch[1]);
            const unit = memoryMatch[2];

            if (isNaN(value)) return '';

            let memoryBytes = value;
            switch (unit.toUpperCase()) {
                case 'KB':
                    memoryBytes = value * 1024;
                    break;
                case 'MB':
                    memoryBytes = value * 1024 * 1024;
                    break;
                case 'GB':
                    memoryBytes = value * 1024 * 1024 * 1024;
                    break;
                case 'TB':
                    memoryBytes = value * 1024 * 1024 * 1024 * 1024;
                    break;
            }

            return memoryBytes > 5 * 1024 * 1024 * 1024 ? 'high-usage' :
                memoryBytes > 1 * 1024 * 1024 * 1024 ? 'medium-usage' : 'low-usage';
        },

        async refreshStats() {
            if (!this.currentStatsContainer) return;

            try {
                const response = await inspectContainer(this.currentStatsContainer.Id);
                this.containerStats = response;
            } catch (error) {
                console.error('刷新容器详细信息失败:', error);
            }
        },

        async loadResourceUsage() {
            // 只获取正在运行的容器
            const runningContainers = this.containers.filter(container => container.State === 'running');

            // 如果没有运行中的容器，直接返回
            if (runningContainers.length === 0) {
                return;
            }

            // 收集所有运行中容器的ID
            const containerIds = runningContainers.map(container => container.Id);

            try {
                // 一次性获取所有容器的资源使用情况
                const statsResults = await getContainersStats(containerIds);

                // 处理每个容器的统计信息
                runningContainers.forEach(container => {
                    const stats = statsResults[container.Id];

                    if (stats && stats.cpu_stats && stats.memory_stats) {
                        // 计算 CPU 使用率
                        let cpuPercent = this.calculateCpuPercentage(container.Id, stats);

                        // 获取内存使用量（而不是使用率）
                        let memoryUsage = 0;
                        if (stats.memory_stats) {
                            memoryUsage = stats.memory_stats.usage || 0;
                        }

                        // 获取IO读写数据
                        let diskRead = 0;
                        let diskWrite = 0;
                        if (stats.blkio_stats && stats.blkio_stats.io_service_bytes_recursive) {
                            const ioStats = stats.blkio_stats.io_service_bytes_recursive;
                            diskRead = ioStats
                                .filter(item => item.op && item.op.toLowerCase() === 'read')
                                .reduce((total, item) => total + (item.value || 0), 0);

                            diskWrite = ioStats
                                .filter(item => item.op && item.op.toLowerCase() === 'write')
                                .reduce((total, item) => total + (item.value || 0), 0);
                        }

                        // 更新容器数据
                        if (!isNaN(cpuPercent) && isFinite(cpuPercent)) {
                            container.CPU = cpuPercent.toFixed(2) + '%';
                        } else {
                            container.CPU = '0.00%';
                        }

                        container.Memory = this.formatBytes(memoryUsage);
                        container.DiskRead = this.formatBytes(diskRead);
                        container.DiskWrite = this.formatBytes(diskWrite);
                    } else {
                        // 如果统计数据不完整，设置默认值
                        container.CPU = '0.00%';
                        container.Memory = this.formatBytes(0);
                        container.DiskRead = this.formatBytes(0);
                        container.DiskWrite = this.formatBytes(0);

                        if (this.previousCpuStats[container.Id]) {
                            delete this.previousCpuStats[container.Id];
                        }
                    }
                });
            } catch (err) {
                console.error('批量获取容器资源使用情况失败:', err);
                runningContainers.forEach(container => {
                    container.CPU = '0.00%';
                    container.Memory = this.formatBytes(0);
                    container.DiskRead = this.formatBytes(0);
                    container.DiskWrite = this.formatBytes(0);
                });
            }
        },

        /**
         * 计算容器CPU使用率
         */
        calculateCpuPercentage(containerId, currentStats) {
            let cpuPercent = 0;

            if (this.previousCpuStats[containerId]) {
                const previousStats = this.previousCpuStats[containerId];

                if (currentStats.cpu_stats && currentStats.cpu_stats.cpu_usage && currentStats.cpu_stats.system_cpu_usage &&
                    previousStats.cpu_usage && previousStats.system_cpu_usage) {

                    const cpuDelta = currentStats.cpu_stats.cpu_usage.total_usage - previousStats.cpu_usage.total_usage;
                    const systemDelta = currentStats.cpu_stats.system_cpu_usage - previousStats.system_cpu_usage;

                    if (systemDelta > 0 && cpuDelta > 0) {
                        const onlineCpus = currentStats.cpu_stats.online_cpus ||
                            (currentStats.cpu_stats.cpu_usage.percpu_usage ?
                                currentStats.cpu_stats.cpu_usage.percpu_usage.length : 1);
                        cpuPercent = (cpuDelta / systemDelta) * onlineCpus * 100;
                    }
                }
            }

            if (currentStats.cpu_stats) {
                this.previousCpuStats[containerId] = {
                    cpu_usage: currentStats.cpu_stats.cpu_usage ? { ...currentStats.cpu_stats.cpu_usage } : null,
                    system_cpu_usage: currentStats.cpu_stats.system_cpu_usage
                };
            }

            return cpuPercent;
        },

        handleViewModeChange(mode) {
            this.viewMode = mode;
        },

        getContainerName(container) {
            if (!container.Names || container.Names.length === 0) {
                return container.Id.substring(0, 12);
            }
            const name = container.Names[0];
            return name.startsWith('/') ? name.substring(1) : name;
        },

        formatPorts(ports) {
            if (!ports || ports.length === 0) {
                return '-';
            }
            const formatted = ports.map(port => {
                if (port.PublicPort) {
                    return `${port.PublicPort}:${port.PrivatePort}/${port.Type}`;
                } else {
                    return `${port.PrivatePort}/${port.Type}`;
                }
            });
            return formatted.join(', ');
        },

        async loadContainers() {
            const all = this.tab === 'all'
            const res = await listContainers(all)
            // 初始化容器的资源使用数据
            this.containers = res.map(container => {
                return {
                    ...container,
                    CPU: '0.00%',
                    Memory: this.formatBytes(0),
                    DiskRead: this.formatBytes(0),
                    DiskWrite: this.formatBytes(0)
                };
            });

            // 加载资源使用情况
            await this.loadResourceUsage();
        },

        handleTabChange() {
            this.loadContainers()
        },

        async startContainer(container) {
            await startContainer(container.Id)
            this.$message.success('容器启动成功')
            this.loadContainers()
        },

        async stopContainer(container) {
            await stopContainer(container.Id)
            this.$message.success('容器停止成功')
            this.loadContainers()
        },

        async restartContainer(container) {
            await restartContainer(container.Id)
            this.$message.success('容器重启成功')
            this.loadContainers()
        },

        removeContainer(container) {
            this.$dialog.warning({
                title: '确认删除',
                content: `确定要删除容器 ${container.Names && container.Names.length > 0 ? container.Names[0].substring(1) : container.Id.substring(0, 12)} 吗？`,
                positiveText: '删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    await removeContainer(container.Id, false, false)
                    this.$message.success('容器删除成功')
                    this.loadContainers()
                }
            })
        },

        async showStats(container) {
            this.currentStatsContainer = container;
            this.showStatsModal = true;
            this.statsLoading = true;
            try {
                const response = await inspectContainer(container.Id);
                this.containerStats = response;
            } catch (error) {
                console.error('获取容器详细信息失败:', error);
                this.$message.error('获取容器详细信息失败: ' + (error.message || '未知错误'));
                this.containerStats = null;
            } finally {
                this.statsLoading = false;
            }
        },

        getContainerNameFromStats(stats) {
            if (!stats) return 'N/A';
            if (stats.Name) {
                return stats.Name.startsWith('/') ? stats.Name.substring(1) : stats.Name;
            }
            if (stats.name) return stats.name;
            return '未知容器';
        },

        formatBytes(bytes) {
            if (bytes === undefined || bytes === null || bytes === 0) return '0 Bytes';
            if (isNaN(bytes) || bytes < 0) return 'N/A';
            const k = 1024;
            const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB'];
            const i = Math.floor(Math.log(bytes) / Math.log(k));
            return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
        },

        async showLogs(container) {
            this.currentLogContainer = container
            this.showLogsModal = true
            await this.loadLogs(container)
        },

        async loadLogs(container) {
            if (!container)
                return

            const res = await getContainerLogs(container.Id, this.followLogs, true, true, '100')
            this.containerLogs = res
            this.$nextTick(() => {
                if (this.$refs.logsContent) {
                    this.$refs.logsContent.scrollTop = this.$refs.logsContent.scrollHeight
                }
            })
        },

        handleStatsModalVisibleChange(visible) {
            if (visible) {
                // 模态框打开时，开始定时刷新
                this.startStatsRefresh();
            } else {
                // 模态框关闭时，清除定时器并销毁图表
                this.stopStatsRefresh();
                this.destroyCharts();
                // 清空历史数据
                this.cpuHistory = [];
                this.memoryHistory = [];
            }
        },

        initCharts() {
            // 初始化CPU图表
            if (this.$refs.cpuChart && !this.cpuChart) {
                this.cpuChart = markRaw(echarts.init(this.$refs.cpuChart, 'dark'));
            }

            // 初始化内存图表
            if (this.$refs.memoryChart && !this.memoryChart) {
                this.memoryChart = markRaw(echarts.init(this.$refs.memoryChart, 'dark'));
            }

            // 更新图表数据
            this.updateCpuChart();
            this.updateMemoryChart();
        },

        destroyCharts() {
            if (this.cpuChart) {
                this.cpuChart.dispose();
                this.cpuChart = null;
            }
            if (this.memoryChart) {
                this.memoryChart.dispose();
                this.memoryChart = null;
            }
        },

        updateCpuChart() {
            if (!this.cpuChart) return;

            const option = {
                backgroundColor: 'transparent',
                title: {
                    text: 'CPU 使用率 (%)',
                    textStyle: {
                        fontSize: 14
                    }
                },
                tooltip: {
                    trigger: 'axis',
                    formatter: (params) => {
                        const param = params[0];
                        return `${param.seriesName}<br/>${param.value}%`;
                    }
                },
                xAxis: {
                    type: 'category',
                    data: this.cpuHistory.map((_, index) => index + 1)
                },
                yAxis: {
                    type: 'value',
                    axisLabel: {
                        formatter: '{value}%'
                    }
                },
                series: [{
                    name: 'CPU使用率',
                    data: this.cpuHistory,
                    type: 'line',
                    smooth: true,
                    itemStyle: {
                        color: '#22d3ee'
                    },
                    areaStyle: {
                        color: 'rgba(34, 211, 238, .25)'
                    }
                }],
                grid: {
                    left: '3%',
                    right: '4%',
                    bottom: '3%',
                    containLabel: true
                }
            };

            this.cpuChart.setOption(option, true);
        },

        updateMemoryChart() {
            if (!this.memoryChart) return;

            const option = {
                backgroundColor: 'transparent',
                title: {
                    text: '内存使用情况',
                    textStyle: {
                        fontSize: 14
                    }
                },
                tooltip: {
                    trigger: 'axis',
                    formatter: (params) => {
                        const param = params[0];
                        return `${param.seriesName}<br/>${this.formatBytes(param.value)}`;
                    }
                },
                xAxis: {
                    type: 'category',
                    data: this.memoryHistory.map((_, index) => index + 1)
                },
                yAxis: {
                    type: 'value',
                    axisLabel: {
                        formatter: (value) => {
                            return this.formatBytes(value);
                        }
                    }
                },
                series: [{
                    name: '内存使用',
                    data: this.memoryHistory,
                    type: 'line',
                    smooth: true,
                    itemStyle: {
                        color: '#38bdf8'
                    },
                    areaStyle: {
                        color: 'rgba(56, 189, 248, .25)'
                    }
                }],
                grid: {
                    left: '3%',
                    right: '4%',
                    bottom: '3%',
                    containLabel: true
                }
            };

            this.memoryChart.setOption(option, true);
        },

        startStatsRefresh() {
            // 清除之前的定时器（如果有的话）
            if (this.statsRefreshTimer) {
                clearInterval(this.statsRefreshTimer);
            }

            // 每秒刷新一次统计数据
            this.statsRefreshTimer = setInterval(() => {
                if (this.currentStatsContainer && this.showStatsModal && this.containerStats && this.containerStats.State && this.containerStats.State.Running) {
                    this.refreshStatsData();
                }
            }, 1000);
        },

        async refreshStatsData() {
            if (!this.currentStatsContainer) return;

            try {
                const stats = await getContainerStats(this.currentStatsContainer.Id);

                // 计算CPU使用率
                let cpuPercent = this.calculateCpuPercentage(this.currentStatsContainer.Id, stats);

                // 获取内存使用量
                let memoryUsage = 0;
                if (stats && stats.memory_stats) {
                    memoryUsage = stats.memory_stats.usage || 0;
                }

                // 添加到历史数据中（最多保存60个数据点）
                this.cpuHistory.push(parseFloat(cpuPercent.toFixed(2)));
                this.memoryHistory.push(memoryUsage);

                // 保持历史数据在合理范围内（最多60个点）
                if (this.cpuHistory.length > 60) {
                    this.cpuHistory.shift();
                }
                if (this.memoryHistory.length > 60) {
                    this.memoryHistory.shift();
                }

                // 初始化并更新图表
                this.initCharts();
            } catch (error) {
                console.error('获取容器统计信息失败:', error);
            }
        },

        stopStatsRefresh() {
            if (this.statsRefreshTimer) {
                clearInterval(this.statsRefreshTimer);
                this.statsRefreshTimer = null;
            }
        }
    }
}
</script>

<style scoped>
.docker-containers {
    padding: 16px;
}

.main-card {
    padding: 16px 20px;
}

.view-toggle {
    margin: 16px 0;
    display: flex;
    align-items: center;
}

.resource-container {
    display: flex;
    flex-direction: column;
    gap: 4px;
}

.resource-item {
    display: flex;
    align-items: center;
    gap: 4px;
}

.resource-text {
    font-size: 12px;
}

.resource-label {
    font-weight: bold;
    margin-right: 4px;
    color: var(--text-2);
}

.resource-value {
    font-size: 12px;
}

.high-usage {
    color: var(--danger);
}

.medium-usage {
    color: var(--warn);
}

.low-usage {
    color: var(--ok);
}

.grid-view {
    padding: 10px 0;
}

.container-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(350px, 1fr));
    gap: 16px;
}

.container-card {
    border: 1px solid var(--line);
    border-radius: var(--radius);
    padding: 16px;
    transition: all 0.3s ease;
    background: linear-gradient(180deg, var(--card) 0%, var(--card-2) 100%);
}

.container-card:hover {
    border-color: rgba(34, 211, 238, .4);
    transform: translateY(-2px);
    box-shadow: 0 0 20px rgba(34, 211, 238, .1);
}

.container-card.container-running {
    border-left: 4px solid var(--ok);
}

.container-card.container-stopped {
    border-left: 4px solid var(--danger);
}

.card-header {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    margin-bottom: 12px;
}

.container-name {
    font-size: 16px;
    font-weight: bold;
    flex: 1;
    margin-right: 10px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--text);
}

.card-body {
    margin-bottom: 16px;
    color: var(--text-2);
}

.card-body>div {
    margin-bottom: 8px;
}

.card-body>div:last-child {
    margin-bottom: 0;
}

.container-image,
.container-ports,
.container-resources,
.container-created {
    font-size: 13px;
    line-height: 1.4;
}

.container-image,
.container-created {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.container-resources {
    display: flex;
    gap: 10px;
}

.card-footer {
    border-top: 1px solid var(--line);
    padding-top: 12px;
}

.action-buttons {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
}

.no-containers {
    grid-column: 1 / -1;
    text-align: center;
    padding: 40px;
    color: var(--text-3);
    font-size: 14px;
}

.logs-container {
    height: 600px;
    display: flex;
    flex-direction: column;
}

.logs-actions {
    display: flex;
    justify-content: space-between;
    margin-bottom: 8px;
}

.logs-content {
    flex: 1;
    overflow-y: auto;
    background-color: var(--card-2);
    padding: 8px;
    border-radius: 4px;
    font-family: monospace;
    white-space: pre-wrap;
    word-break: break-all;
    color: var(--text-2);
}

.stats-container {
    padding: 0 8px;
}

.port-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
}

.env-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(500px, 1fr));
}

.stats-section {
    margin-bottom: 20px;
}

.stats-section h3 {
    margin-bottom: 10px;
    border-bottom: 1px solid var(--line);
    padding-bottom: 5px;
    color: var(--text);
}

.stat-item {
    display: flex;
    align-items: center;
    padding: 2px 0;
}

.stat-item .label {
    font-weight: bold;
    margin-right: 10px;
    min-width: 100px;
    color: var(--text-2);
}

.stat-item .value {
    font-weight: bold;
    color: var(--accent-2);
}

.no-stats {
    text-align: center;
    padding: 40px 0;
    color: var(--text-3);
}

.charts-container {
    display: flex;
}

.chart-container {
    width: 50%;
    height: 200px;
}
</style>
