<template>
    <n-drawer :show="show" :width="760" placement="right" @update:show="$emit('update:show', $event)">
        <n-drawer-content closable :title="`执行记录 · ${task && task.Name ? task.Name : ''}`">
            <n-space vertical size="small">
                <!-- NDrawerContent 只有 header/default/footer 三个插槽，没有 header-extra，
                     故刷新按钮放在正文首行（写在 #header-extra 里会被静默丢弃） -->
                <n-space align="center" justify="space-between" style="width: 100%">
                    <n-space align="center" size="small">
                        <span class="muted">状态</span>
                        <n-select v-model:value="StatusFilter" :options="StatusOptions" size="tiny"
                            style="width: 150px" clearable @update:value="() => loadRuns(1)" />
                        <span class="muted">共 {{ Total }} 条</span>
                    </n-space>
                    <n-button size="tiny" tertiary @click="reload">刷新</n-button>
                </n-space>

                <!-- 固定布局限制状态、摘要等列的宽度；摘要超长时由 tooltip 展示全文 -->
                <n-data-table :columns="RunColumns" :data="Runs" size="small" :bordered="true" :row-key="r => r.Id"
                    :max-height="300" table-layout="fixed" @update:page="loadRuns" :pagination="Pagination" />

                <template v-if="Detail">
                    <n-divider style="margin: 6px 0">执行详情</n-divider>
                    <n-descriptions label-placement="left" :column="2" size="small" bordered>
                        <n-descriptions-item label="RunId">
                            <span class="mono">{{ Detail.Run.Id }}</span>
                        </n-descriptions-item>
                        <n-descriptions-item label="状态">
                            <span :style="{ color: statusMeta(Detail.Run.Status).color }">
                                {{ statusMeta(Detail.Run.Status).label }}
                            </span>
                        </n-descriptions-item>
                        <n-descriptions-item label="触发源">
                            {{ triggerLabel(Detail.Run.TriggerSource) }}
                            <span v-if="Detail.Run.IsRetry" class="muted">（第 {{ Detail.Run.Attempt }} 次尝试）</span>
                        </n-descriptions-item>
                        <n-descriptions-item label="耗时">{{ elapsed(Detail.Run.ElapsedMs) }}</n-descriptions-item>
                        <n-descriptions-item label="开始">{{ local(Detail.Run.StartedAtUtc) }}</n-descriptions-item>
                        <n-descriptions-item label="结束">{{ local(Detail.Run.FinishedAtUtc) }}</n-descriptions-item>
                        <n-descriptions-item label="安全摘要" :span="2">
                            {{ summary(Detail.Run) }}
                        </n-descriptions-item>
                        <n-descriptions-item v-if="Detail.Run.CancelReason" label="中止原因" :span="2">
                            {{ Detail.Run.CancelReason }}
                        </n-descriptions-item>
                        <n-descriptions-item label="日志" :span="2">
                            <n-button v-if="Detail.LogAvailable" size="tiny" tertiary
                                @click="openLog(Detail.LogId)">查看实时日志</n-button>
                            <span v-else class="muted">日志已清理或尚未落库</span>
                        </n-descriptions-item>
                        <n-descriptions-item label="重新执行" :span="2">
                            <n-button size="tiny" tertiary @click="rerun">按当前脚本与环境变量再跑一次</n-button>
                            <span class="muted" style="margin-left: 8px">产生新的执行记录，不并入本条执行链、不占重试次数</span>
                        </n-descriptions-item>
                    </n-descriptions>

                    <n-timeline v-if="Detail.Attempts && Detail.Attempts.length > 1" size="small">
                        <n-timeline-item v-for="item in Detail.Attempts" :key="item.Id"
                            :type="tone(item.Status)" :title="`第 ${item.Attempt} 次尝试 · ${statusMeta(item.Status).label}`"
                            :time="local(item.StartedAtUtc || item.CreatedAtUtc)">
                            <span class="muted">{{ summary(item) }}</span>
                        </n-timeline-item>
                    </n-timeline>
                </template>

                <n-divider style="margin: 6px 0">失败策略</n-divider>
                <n-form v-if="Policy" label-placement="left" label-width="110" size="small">
                    <n-form-item label="启用策略">
                        <n-switch v-model:value="Policy.Enabled" />
                        <span class="muted" style="margin-left: 8px">关闭时该任务只跑一次，且保留旧式一次失败通知</span>
                    </n-form-item>
                    <n-form-item label="重试次数">
                        <n-input-number v-model:value="Policy.RetryCount" :min="0" :max="3" :disabled="!Policy.Enabled" />
                        <span class="muted" style="margin-left: 8px">上限 3 次；仅脚本抛异常才重试</span>
                    </n-form-item>
                    <n-form-item label="退避基数(秒)">
                        <n-input-number v-model:value="Policy.BackoffSeconds" :min="30" :max="3600"
                            :disabled="!Policy.Enabled" />
                        <span class="muted" style="margin-left: 8px">
                            指数退避封顶 3600 秒，第 2 次约 {{ backoff(Policy.BackoffSeconds, 2) }} 秒
                        </span>
                    </n-form-item>
                    <n-form-item label="告警阈值(次)">
                        <n-input-number v-model:value="Policy.AlertAfterConsecutiveFailures" :min="1" :max="10"
                            :disabled="!Policy.Enabled" />
                    </n-form-item>
                    <n-form-item label="告警冷却(分钟)">
                        <n-input-number v-model:value="Policy.CooldownMinutes" :min="0" :max="1440"
                            :disabled="!Policy.Enabled" />
                    </n-form-item>
                    <n-form-item label="恢复通知">
                        <n-switch v-model:value="Policy.SendRecovery" :disabled="!Policy.Enabled" />
                    </n-form-item>
                    <n-alert type="warning" :bordered="false" size="small" style="margin-bottom: 8px">
                        自动重试会重放脚本的外部副作用，仅适合幂等脚本；默认关闭。
                    </n-alert>
                    <n-button type="primary" size="small" :disabled="!canWritePolicy" @click="savePolicy">保存策略</n-button>
                    <span v-if="!canWritePolicy" class="muted" style="margin-left: 8px">策略修改仅管理员可用</span>
                </n-form>
            </n-space>

            <n-modal v-model:show="LogModal.Show" preset="card" title="执行日志" style="width: 900px">
                <n-scrollbar style="max-height: 60vh">
                    <pre class="mono log-body">{{ LogModal.Text }}</pre>
                </n-scrollbar>
            </n-modal>
        </n-drawer-content>
    </n-drawer>
</template>

<script>
import { h } from 'vue'
import { useMessage } from 'naive-ui'
import { GetTaskRuns, GetTaskRunDetail, GetTaskRunPolicy, PutTaskRunPolicy, PostTaskRunRetry } from '@/api/taskRun.js'
import { LogDetails } from '@/api/logs.js'
import { runStatusMeta, triggerSourceLabel, formatElapsed, formatUtcToLocal, summaryOr, backoffSecondsFor, hasOpenChain } from '@/utils/taskRun.js'

const AutoIntervalMs = 2000
const AutoWindowMs = 5 * 60 * 1000
// 连续判空多少轮才停轮询（覆盖「已判失败但重试尚未排定」的写入间隙）
const AutoIdleTicksToStop = 4

export default {
    name: 'RunHistoryDrawer',
    props: {
        show: { type: Boolean, default: false },
        task: { type: Object, default: null },
        // 客户端不做管理端显隐（权限由服务端 [ManagerOnly] 拦截，非 Manager 保存返回信封 401）
        canWritePolicy: { type: Boolean, default: true },
        focusRunId: { type: String, default: '' }
    },
    emits: ['update:show'],
    data() {
        return {
            Runs: [],
            Total: 0,
            Page: 1,
            PageSize: 20,
            // 自动刷新：抽屉内有活动/待重试的行时按轮询刷新，超过 AutoUntil 即停（退避可达小时级，不能无限轮询）
            AutoTimer: null,
            AutoIdleTicks: 0,
            AutoUntil: 0,
            StatusFilter: null,
            Detail: null,
            Policy: null,
            LogModal: { Show: false, Text: '' },
            StatusOptions: ['Pending', 'Running', 'Succeeded', 'Failed', 'Rejected', 'Canceled', 'Interrupted']
                .map(k => ({ label: runStatusMeta(k).label, value: k })),
            RunColumns: [
                {
                    title: '状态', key: 'Status', width: 64, render: (row) => h('span',
                        { style: { color: runStatusMeta(row.Status).color } }, runStatusMeta(row.Status).label)
                },
                { title: '尝试', key: 'Attempt', width: 56, align: 'center' },
                { title: '触发源', key: 'TriggerSource', width: 82, render: (row) => triggerSourceLabel(row.TriggerSource) },
                { title: '开始(本地)', key: 'StartedAtUtc', width: 190, render: (row) => formatUtcToLocal(row.StartedAtUtc || row.CreatedAtUtc) },
                { title: '耗时', key: 'ElapsedMs', width: 120, render: (row) => formatElapsed(row.ElapsedMs) },
                {
                    title: '摘要', key: 'SafeSummary', ellipsis: { tooltip: true }, width: 100,
                    render: (row) => summaryOr(row.Status, row.SafeSummary)
                },
                {
                    title: '', key: 'op', width: 68, align: 'center', render: (row) => h('a',
                        { style: { cursor: 'pointer', color: 'var(--accent)' }, onClick: () => this.openRun(row.Id) }, '详情')
                }
            ]
        }
    },
    computed: {
        Pagination() {
            return { page: this.Page, pageSize: this.PageSize, itemCount: this.Total, showSizePicker: false }
        }
    },
    watch: {
        show(v) {
            if (v) this.reload()
            else this.stopAutoRefresh()
        },
        task() {
            if (this.show) this.reload()
        }
    },
    unmounted() {
        this.stopAutoRefresh()
    },
    methods: {
        statusMeta: runStatusMeta,
        triggerLabel: triggerSourceLabel,
        elapsed: formatElapsed,
        local: formatUtcToLocal,
        backoff: backoffSecondsFor,
        tone(status) {
            const tone = runStatusMeta(status).tone
            return tone === 'ok' ? 'success' : tone === 'danger' ? 'error' : tone === 'warn' ? 'warning' : 'info'
        },
        summary(run) {
            return summaryOr(run.Status, run.SafeSummary)
        },
        reload() {
            this.AutoUntil = Date.now() + AutoWindowMs
            if (this.focusRunId) {
                this.openRun(this.focusRunId)
            } else {
                this.Detail = null
            }
            if (this.task && this.task.Id) {
                this.loadPolicy()
            }
            return this.loadRuns(1)
        },
        syncAutoRefresh() {
            if (!this.show || Date.now() > this.AutoUntil) {
                return this.stopAutoRefresh()
            }
            if (hasOpenChain(this.Runs)) {
                this.AutoIdleTicks = 0
                if (!this.AutoTimer) this.AutoTimer = setInterval(() => this.refreshAuto(), AutoIntervalMs)
                return
            }
            // 「判失败」与「排定下一次重试」是先后两次写入：刚失败的那一瞬可能既非活动行也没有
            // NextAttemptAtUtc，故连续多轮判空才停轮询，否则重试链会在页面上永远刷不出来
            if (!this.AutoTimer) return
            this.AutoIdleTicks += 1
            if (this.AutoIdleTicks >= AutoIdleTicksToStop) this.stopAutoRefresh()
        },
        stopAutoRefresh() {
            this.AutoIdleTicks = 0
            if (this.AutoTimer) {
                clearInterval(this.AutoTimer)
                this.AutoTimer = null
            }
        },
        refreshAuto() {
            if (!this.show || Date.now() > this.AutoUntil) {
                return this.stopAutoRefresh()
            }
            const detailId = this.Detail ? this.Detail.Run.Id : ''
            this.loadRuns(this.Page).then(() => detailId ? this.openRun(detailId) : null)
        },
        loadRuns(page) {
            this.Page = page || 1
            return GetTaskRuns({
                taskId: this.task ? this.task.Id : undefined,
                status: this.StatusFilter || undefined,
                page: this.Page,
                pageSize: this.PageSize
            }).then(res => {
                this.Runs = (res && res.Data) || []
                this.Total = (res && res.TotalCount) || 0
                this.syncAutoRefresh()
            })
        },
        openRun(runId) {
            return GetTaskRunDetail(runId).then(res => {
                this.Detail = res || null
            })
        },
        loadPolicy() {
            return GetTaskRunPolicy(this.task.Id).then(res => {
                this.Policy = res || null
            })
        },
        savePolicy() {
            const that = this
            return PutTaskRunPolicy(this.task.Id, this.Policy).then(res => {
                this.Policy = res || this.Policy
                that.$message.success('策略已保存')
                return this.loadRuns(this.Page)
            }).catch(() => {
                that.$message.error('策略保存失败')
            })
        },
        // 手动重新执行：受理式，返回新的根执行 Id；不复用旧执行链、不占旧链重试次数
        rerun() {
            const that = this
            if (!this.Detail) {
                return
            }
            this.$dialog.warning({
                title: '重新执行确认',
                content: '按当前脚本与环境变量重新执行一次，产生新的执行记录（不并入本条执行链，不占自动重试次数）。',
                positiveText: '确认',
                negativeText: '取消',
                onPositiveClick: () => {
                    PostTaskRunRetry(that.Detail.Run.Id).then(res => {
                        that.$message.success('重新执行已受理')
                        that.AutoUntil = Date.now() + AutoWindowMs
                        that.openRun(res && res.RunId ? res.RunId : that.Detail.Run.Id)
                        that.loadRuns(1)
                    }).catch(() => {
                        that.$message.error('重新执行受理失败')
                    })
                }
            })
        },
        openLog(logId) {
            return LogDetails(logId).then(res => {
                this.LogModal.Text = typeof res === 'string' ? res : JSON.stringify(res, null, 2)
                this.LogModal.Show = true
            })
        }
    },
    mounted() {
        // message 需显式引入（本项目未注册 unplugin-auto-import 的 API 级自动引入）
        this.$message = useMessage()
        if (this.show) this.reload()
    }
}
</script>

<style scoped>
.muted {
    color: var(--muted);
    font-size: 12px;
}

.log-body {
    margin: 0;
    white-space: pre-wrap;
    word-break: break-all;
    font-size: 12px;
}
</style>
