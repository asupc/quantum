<style scoped>
pre {
    white-space: pre-wrap;
    word-wrap: break-word;
}

.log-container {
    max-height: 70vh;
    overflow: auto;
}
</style>
<template>
    <div>
        <div class="filter-card">
            <n-form :show-feedback="false" label-placement="left" label-width="auto" @keyup.enter="loadLogs(1)">
                <n-grid cols="2 s:2 m:3 l:4 xl:5" responsive="screen" :x-gap="12" :y-gap="12">
                    <n-gi>
                        <n-form-item label="关键字">
                            <n-input v-model:value="QueryForm.Key" placeholder="输入搜索关键字" clearable />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="日志类型">
                            <n-select v-model:value="QueryForm.LogType" :options="LogTypeOptions" clearable
                                placeholder="全部" />
                        </n-form-item>
                    </n-gi>
                    <n-gi span="1 m:2">
                        <n-form-item label="时间">
                            <n-date-picker v-model:value="QueryForm.DateRange" type="datetimerange" clearable
                                style="width: 100%" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <div class="filter-actions">
                            <n-button type="primary" @click="loadLogs(1)">
                                <template #icon>
                                    <i class="fa-solid fa-magnifying-glass"></i>
                                </template>
                                搜索
                            </n-button>
                            <n-button secondary @click="resetQuery">
                                <template #icon>
                                    <i class="fa-solid fa-rotate-right"></i>
                                </template>
                                重置
                            </n-button>
                        </div>
                    </n-gi>
                </n-grid>
            </n-form>
        </div>

        <div style="margin: 20px 0">
            <n-space>
                <n-button type="error" size="small" @click="deletLogs">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    删除
                </n-button>
                <n-button type="error" size="small" @click="clearLogs">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    清空日志
                </n-button>
            </n-space>
        </div>
        <n-data-table ref="LogTable" :columns="LogsColumns" :data="LogsData" :row-key="(row) => row.Id"
            :checked-row-keys="CheckedRowKeys" @update:checked-row-keys="onCheckedRowKeys" :scroll-x="1100"
            :bordered="true" size="small" />
        <div style="margin: 10px; overflow: hidden">
            <div style="float: right">
                <n-pagination :item-count="PageInfo.Total" :page="QueryForm.PageIndex" :page-size="QueryForm.PageSize"
                    :page-sizes="PageInfo.PageSizeOpts" show-size-picker @update:page-size="pageSizeChange"
                    @update:page="loadLogs" show-quick-jumper>
                    <template #prefix>
                        <span class="mono">共 {{ PageInfo.Total }} 条</span>
                    </template>
                </n-pagination>
            </div>
        </div>

        <div class="logModal">
            <n-modal preset="card" v-model:show="LogModal.Show" :title="LogModal.Title" style="width: 800px">
                <pre class="log-container">{{ LogModal.LogContent }}</pre>
            </n-modal>
        </div>
    </div>
</template>
<script>
import { h } from 'vue'
import dayjs from 'dayjs'
import { AllEnumsCached } from '@/api/enum.js'

import { LogDetails, DeleteLogs, ClearLogs, GetLogs } from '@/api/logs.js'
import { getEnumKey } from '@/libs/util.js'
export default {
    name: 'LogsIndex',
    data() {
        return {
            listSeq: 0,
            LogModal: {
                Show: false,
                LogContent: ''
            },
            CheckedRowKeys: [],
            LogsColumns: [
                {
                    type: 'selection'
                }, {
                    title: '序号',
                    width: 60,
                    align: 'center',
                    render: (row, index) => index + 1
                },
                {
                    title: '标题',
                    width: 150,
                    key: 'Title',
                    ellipsis: { tooltip: true },
                    render: (row) => {
                        if (row.LogPath) {
                            return h('div', [
                                h(
                                    'a',
                                    {
                                        style: { color: 'var(--accent)', cursor: 'pointer' },
                                        onClick: () => {
                                            this.logDetails(row)
                                        }
                                    },
                                    row.Title
                                )
                            ])
                        }
                        return h('div', [h('span', row.Title)])
                    }
                },
                {
                    title: '操作人',
                    key: 'Operator',
                    width: 180,
                    render: (row) => {
                        return this.tdTip(h, { row }, 'Operator', false)
                    }
                },
                {
                    title: '说明',
                    key: 'Remark',
                    width: 420,
                    ellipsis: { tooltip: true },
                    render: (row) => {
                        return this.tdTip(h, { row }, 'Remark')
                    }
                },
                {
                    title: '时间',
                    key: 'CreateTime',
                    width: 160,
                    className: 'mono'
                },
                {
                    title: '日志类型',
                    key: 'LogType',
                    width: 100,
                    ellipsis: { tooltip: true },
                    render: (row) => {
                        return h('div', [
                            h(
                                'span',
                                getEnumKey(this.AllEnums, 'LogType', row.LogType)
                            )
                        ])
                    }
                }
            ],
            LogsData: [],
            AllEnums: {},
            PageInfo: {
                PageSizeOpts: [15, 50, 100, 200, 500, 1000],
                Total: 0
            },
            QueryForm: {
                LogType: null,
                Key: '',
                StartTime: null,
                EndTime: null,
                PageIndex: 1,
                PageSize: 15,
                DateRange: null
            }
        }
    },
    computed: {
        LogTypeOptions() {
            return (this.AllEnums.LogType || []).map((t) => ({
                label: t.Key,
                value: t.Value
            }))
        }
    },
    methods: {
        onCheckedRowKeys(keys) {
            this.CheckedRowKeys = keys
        },
        loadLogs(pageIndex) {
            this.QueryForm.PageIndex = pageIndex || 1
            if (this.QueryForm.DateRange && this.QueryForm.DateRange.length === 2) {
                // §7.4：发本地墙钟格式串（不带 Z）。若沿用 new Date(ms)，axios 会以 toISOString() 序列化成带 Z 的 UTC 串，
                // 真实 MVC 绑定按 RoundtripKind 保留 Kind=Utc、不转本地，与后端 DateTime.Now 本地入库的 CreateTime 不同源 → 偏时区
                this.QueryForm.StartTime = dayjs(this.QueryForm.DateRange[0]).format('YYYY-MM-DD HH:mm:ss')
                this.QueryForm.EndTime = dayjs(this.QueryForm.DateRange[1]).format('YYYY-MM-DD HH:mm:ss')
            } else {
                this.QueryForm.StartTime = null
                this.QueryForm.EndTime = null
            }
            const seq = ++this.listSeq
            return GetLogs(this.QueryForm).then((res) => {
                if (seq !== this.listSeq) return
                this.LogsData = res.Data
                this.PageInfo.Total = res.TotalCount
            }).catch((error) => {
                if (seq !== this.listSeq) return
                this.$message.error(String((error && error.message) || error || '日志加载失败'))
            })
        },
        resetQuery() {
            this.QueryForm.Key = ""
            this.QueryForm.LogType = null
            this.QueryForm.DateRange = null
            this.loadLogs(1)
        },

        clearLogs() {
            var c = this
            this.$dialog.warning({
                title: '清空确认',
                content: '确定清空所有的日志吗？',
                positiveText: '确认',
                negativeText: '取消',
                onPositiveClick: function () {
                    ClearLogs().then((res) => {
                        c.loadLogs(1)
                        c.$message.success('日志删除成功！')
                    })
                }
            })
        },
        deletLogs() {
            var ids = this.CheckedRowKeys
            if (ids == null || ids.length == 0) {
                this.$message.warning('请选中日志后再操作！')
                return
            }
            var c = this
            this.$dialog.warning({
                title: '删除确认',
                content: '确定删除选中的日志吗？',
                positiveText: '确认',
                negativeText: '取消',
                onPositiveClick: function () {
                    DeleteLogs(ids).then((res) => {
                        c.loadLogs(1)
                        c.$message.success('日志删除成功！')
                    })
                }
            })
        },
        logDetails(row) {
            LogDetails(row.Id).then((res) => {
                if (res) {
                    this.LogModal.Show = true
                    this.LogModal.LogContent = res
                    this.LogModal.Title = row.Title + '-----详细日志'
                }
            })
        },
        pageSizeChange(size) {
            this.QueryForm.PageSize = size
            this.loadLogs(1)
        },
        loadAllEnums() {
            return AllEnumsCached().then((res) => {
                this.AllEnums = res
            })
        }
    },
    mounted() {
        // 枚举与首屏列表并行拉取，避免「先等枚举再拉列表」的请求瀑布（§3-12）
        Promise.all([this.loadAllEnums(), this.loadLogs(1)])
    }
}
</script>
