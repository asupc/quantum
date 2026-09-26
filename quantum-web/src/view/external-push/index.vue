<template>
    <div class="external-push-page">
        <div class="toolbar">
            <n-space align="center">
                <n-button type="primary" size="small" @click="openCreate">新建接入凭据</n-button>
                <n-button size="small" @click="load">刷新</n-button>
                <span class="muted">共 {{ Rows.length }} 个接入方 · 新建凭据默认禁用，核对无误后再启用</span>
            </n-space>
        </div>

        <n-data-table :columns="Columns" :data="Rows" :bordered="true" size="small" :row-key="(r) => r.Id"
            :loading="Loading" :scroll-x="1100" />

        <n-modal preset="card" v-model:show="CreateModal.Show" title="新建外部推送凭据"
            style="width: 560px; max-width: calc(100vw - 48px)" :mask-closable="false">
            <n-form label-placement="left" label-width="110">
                <n-form-item label="接入方名称">
                    <n-input v-model:value="CreateModal.Data.DisplayName" placeholder="1~80 个字符，用于会话标题缺省与列表识别" />
                </n-form-item>
                <n-form-item label="每分钟配额">
                    <n-input-number v-model:value="CreateModal.Data.RateLimitPerMinute" :min="1" :max="6000" />
                </n-form-item>
                <n-form-item label="每日配额">
                    <n-input-number v-model:value="CreateModal.Data.DailyQuota" :min="1" :max="1000000" />
                </n-form-item>
                <n-form-item label="备注">
                    <n-input v-model:value="CreateModal.Data.Remark" type="textarea" :rows="2" />
                </n-form-item>
            </n-form>
            <template #footer>
                <n-space>
                    <n-button type="primary" size="small" @click="submitCreate">创建</n-button>
                    <n-button size="small" @click="CreateModal.Show = false">取消</n-button>
                </n-space>
            </template>
        </n-modal>

        <n-modal preset="card" v-model:show="SecretModal.Show" title="密钥仅此一次可见"
            style="width: 620px; max-width: calc(100vw - 48px)">
            <n-alert type="warning" :bordered="false">
                请立即复制保存。平台只保存密钥的 SHA-256 摘要，遗失后无法找回，只能轮换。
            </n-alert>
            <n-form label-placement="left" label-width="110" style="margin-top: 10px">
                <n-form-item label="凭据 Id">
                    <n-input :value="SecretModal.Id" readonly />
                </n-form-item>
                <n-form-item label="明文密钥">
                    <n-input :value="SecretModal.Secret" readonly type="textarea" :rows="2" />
                </n-form-item>
            </n-form>
            <div class="mono sample">{{ Sample }}</div>
            <template #footer>
                <n-space>
                    <n-button size="small" @click="copySample">复制调用示例</n-button>
                    <n-button type="primary" size="small" @click="SecretModal.Show = false">我已保存</n-button>
                </n-space>
            </template>
        </n-modal>
    </div>
</template>

<script>
import { h } from 'vue'
import { useMessage } from 'naive-ui'
import { renderOpActions, OpColor } from '@/utils/op-actions'
import {
    GetExternalPushCredentials,
    CreateExternalPushCredential,
    RotateExternalPushCredential,
    SetExternalPushEnabled,
    DeleteExternalPushCredential
} from '@/api/externalPush.js'

export default {
    name: 'ExternalPushIndex',
    data() {
        return {
            Loading: false,
            Rows: [],
            CreateModal: { Show: false, Data: { DisplayName: '', RateLimitPerMinute: 60, DailyQuota: 1000, Remark: '' } },
            SecretModal: { Show: false, Id: '', Secret: '' },
            Columns: [
                { title: '接入方', key: 'DisplayName', minWidth: 150, ellipsis: { tooltip: true } },
                { title: '凭据 Id', key: 'Id', minWidth: 200, className: 'mono', ellipsis: { tooltip: true } },
                {
                    title: '状态', key: 'Enabled', width: 90, align: 'center', render: (row) => h('span',
                        { style: { color: row.Enabled ? 'var(--ok)' : 'var(--muted)' } },
                        row.Enabled ? '已启用' : '已禁用')
                },
                { title: '每分钟', key: 'RateLimitPerMinute', width: 80, align: 'center' },
                { title: '每日', key: 'DailyQuota', width: 80, align: 'center' },
                { title: '累计受理', key: 'TotalSent', width: 90, align: 'center' },
                {
                    title: '最后使用', key: 'LastUsedAtUtc', width: 160,
                    render: (row) => this.timeText(row.LastUsedAtUtc)
                },
                {
                    title: '操作', key: 'action', width: 170, align: 'center', fixed: 'right',
                    render: (row) => renderOpActions(h, [
                        {
                            icon: row.Enabled ? 'fa-toggle-off' : 'fa-toggle-on',
                            title: row.Enabled ? '禁用' : '启用',
                            color: row.Enabled ? OpColor.Delete : OpColor.Run,
                            onClick: () => this.setEnabled(row, !row.Enabled)
                        },
                        { icon: "fa-rotate", title: "轮换密钥", color: OpColor.Edit, onClick: () => this.rotate(row) },
                        { icon: "fa-trash", title: "删除", color: OpColor.Delete, onClick: () => this.remove(row) }
                    ])
                }
            ]
        }
    },
    computed: {
        Sample() {
            return [
                `curl -X POST https://<host>/api/ExternalPush/messages \\`,
                `  -H 'Authorization: PushKey ${this.SecretModal.Id}.${this.SecretModal.Secret}' \\`,
                `  -H 'Idempotency-Key: demo-00000001' \\`,
                `  -H 'Content-Type: application/json' \\`,
                `  -d '{"Title":"设备告警","SessionTitle":"机房监控","Content":"{{tag:red|紧急}} 温度过高"}'`
            ].join('\n')
        }
    },
    methods: {
        timeText(value) {
            if (!value) return '-'
            const date = new Date(value.endsWith('Z') ? value : `${value}Z`)
            return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
        },
        load() {
            this.Loading = true
            return GetExternalPushCredentials().then((res) => {
                this.Rows = res || []
            }).finally(() => {
                this.Loading = false
            })
        },
        openCreate() {
            this.CreateModal.Data = { DisplayName: '', RateLimitPerMinute: 60, DailyQuota: 1000, Remark: '' }
            this.CreateModal.Show = true
        },
        submitCreate() {
            const that = this
            if (!this.CreateModal.Data.DisplayName.trim()) {
                this.$message.warning('请填写接入方名称')
                return
            }
            CreateExternalPushCredential(this.CreateModal.Data).then((res) => {
                this.CreateModal.Show = false
                this.showSecret(res)
                that.$message.success('凭据已创建（默认禁用）')
                this.load()
            })
        },
        rotate(row) {
            const that = this
            this.$dialog.warning({
                title: '轮换密钥',
                content: `旧密钥立即失效，接入方需改用新密钥。确认轮换「${row.DisplayName}」？`,
                positiveText: '确认轮换',
                negativeText: '取消',
                onPositiveClick: () => RotateExternalPushCredential(row.Id).then((res) => {
                    this.showSecret(res)
                    that.$message.success('已轮换')
                })
            })
        },
        setEnabled(row, enabled) {
            const that = this
            SetExternalPushEnabled(row.Id, enabled).then(() => {
                that.$message.success(enabled ? '已启用' : '已禁用（停止新投递，历史消息保留）')
                this.load()
            })
        },
        remove(row) {
            const that = this
            this.$dialog.error({
                title: '删除凭据',
                content: `删除后该接入方无法再推送，其幂等记录一并清理；已投递的通知与会话消息保留。`,
                positiveText: '确认删除',
                negativeText: '取消',
                onPositiveClick: () => DeleteExternalPushCredential(row.Id).then(() => {
                    that.$message.success('已删除')
                    this.load()
                })
            })
        },
        showSecret(res) {
            if (!res || !res.Secret) return
            this.SecretModal.Id = res.Id
            this.SecretModal.Secret = res.Secret
            this.SecretModal.Show = true
        },
        copySample() {
            navigator.clipboard?.writeText(this.Sample)
            this.$message.success('调用示例已复制')
        }
    },
    mounted() {
        this.$message = useMessage()
        this.load()
    }
}
</script>

<style scoped>
.toolbar {
    margin-bottom: 10px;
}

.muted {
    color: var(--muted);
    font-size: 12px;
}

.sample {
    margin-top: 8px;
    padding: 8px;
    background: var(--bg-elevated, rgba(255, 255, 255, 0.04));
    border-radius: 6px;
    white-space: pre-wrap;
    word-break: break-all;
    font-size: 12px;
}
</style>
