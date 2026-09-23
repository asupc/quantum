<template>
    <div>
        <div class="filter-card">
            <n-form :show-feedback="false" label-placement="left" label-width="auto" @keyup.enter="loadEnvs(1)">
                <n-grid cols="2 s:2 m:3 l:4 xl:5" responsive="screen" :x-gap="12" :y-gap="12">
                    <n-gi>
                        <n-form-item label="关键字">
                            <n-input v-model:value="QueryForm.Key" placeholder="输入搜索关键字" clearable />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="状态">
                            <n-select v-model:value="QueryForm.Enable" :options="EnableOptions" clearable
                                @update:value="loadEnvs(1)" placeholder="全部" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <div class="filter-actions">
                            <n-button type="primary" @click="loadEnvs(1)">
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
                <n-button type="primary" size="small" @click="editEnv(-1)">
                    <template #icon>
                        <i class="fa-solid fa-plus"></i>
                    </template>
                    添加
                </n-button>
                <n-button type="primary" size="small" @click="exportEnvs">
                    <template #icon>
                        <i class="fa-solid fa-cloud-arrow-down"></i>
                    </template>
                    导出
                </n-button>
                <n-button type="success" size="small" @click="enableEnvs">
                    <template #icon>
                        <i class="fa-solid fa-check"></i>
                    </template>
                    启用
                </n-button>
                <n-button type="warning" size="small" @click="disableEnvs">
                    <template #icon>
                        <i class="fa-solid fa-ban"></i>
                    </template>
                    禁用
                </n-button>
                <n-button type="error" size="small" @click="deleteSelection">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    删除
                </n-button>
            </n-space>
        </div>
        <n-data-table ref="EnvTable" :columns="EnvColumns" :data="EnvData" :row-key="(row) => row.Id"
            :checked-row-keys="CheckedRowKeys" @update:checked-row-keys="onCheckedRowKeys" :scroll-x="1200"
            :bordered="true" size="small" />

        <div style="margin: 10px; overflow: hidden">
            <div style="float: right">
                <n-pagination :item-count="PageInfo.Total" :page="QueryForm.PageIndex" :page-size="QueryForm.PageSize"
                    :page-sizes="PageInfo.PageSizeOpts" show-size-picker @update:page-size="pageSizeChange"
                    @update:page="loadEnvs">
                    <template #prefix>
                        <span class="mono">共 {{ PageInfo.Total }} 条</span>
                    </template>
                </n-pagination>
            </div>
        </div>
        <n-modal preset="card" v-model:show="EditEnvModal.Show" :title="EditEnvModal.Title" style="width: 600px"
            :mask-closable="false">
            <n-form ref="EditQLPanelForm" :model="EditEnvModal.Data" :rules="ruleValidate" label-placement="left"
                label-width="80">
                <n-form-item label="变量名称" path="Name">
                    <n-input :maxlength="64" v-model:value="EditEnvModal.Data.Name" placeholder="输入环境变量名称" />
                </n-form-item>
                <n-form-item label="变量值" path="Value">
                    <n-input type="textarea" v-model:value="EditEnvModal.Data.Value" :rows="3" />
                </n-form-item>
                <n-form-item label="开关选项">
                    <n-checkbox v-model:checked="EditEnvModal.Data.Enable">启用</n-checkbox>
                </n-form-item>
                <n-form-item label="系统备注">
                    <n-input type="textarea" v-model:value="EditEnvModal.Data.Remark" :rows="2" />
                </n-form-item>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="handleCancel()" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="handleSubmit('EditQLPanelForm')">提交</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>
<script>
  import { h } from 'vue'
  import { renderOpActions, OpColor } from '@/utils/op-actions'

  import {
    GetEnvs,
    PostEnvs,
    DeleteEnvs,
    Enable,
    Disable,
    ExportEnvs
  } from '@/api/envs.js'

  export default {
    name: 'EnvIndex',
    data() {
      return {
        listSeq: 0,
        CheckedRowKeys: [],
        EnvColumns: [
          {
            type: 'selection'
          }, {
            title: '序号',
            width: 60,
            align: 'center',
            render: (row, index) => index + 1
          },
          {
            title: '名称',
            key: 'Name',
            width: 140,
            className: 'mono',
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'Name')
            }
          },
          {
            title: '变量值',
            key: 'Value',
            minWidth: 400,
            render: (row) => {
              return this.tdTip(h, { row }, 'Value')
            }
          },
          {
            title: '状态',
            key: 'Enable',
            width: 80,
            align: 'center',
            render: (row) => {
              return h('span', { class: row.Enable ? 'pill ok' : 'pill danger' }, [
                h('span', { class: 'dot' }),
                row.Enable ? '启用' : '禁用'
              ])
            }
          },
          {
            title: '系统备注',
            key: 'Remark',
            minWidth: 180,
            render: (row) => {
              return this.tdTip(h, { row }, 'Remark')
            }
          },
          {
            title: '更新时间',
            key: 'UpdateTime',
            width: 160,
            render: (row) => (row.UpdateTime ? String(row.UpdateTime).replace('T', ' ') : '')
          },
          {
            title: '操作',
            key: 'action',
            width: 100,
            align: 'center',
            fixed: 'right',
            render: (row, index) =>
              renderOpActions(h, [
                { icon: 'fa-pencil', title: '编辑', color: OpColor.Edit, onClick: () => this.editEnv(index) },
                { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.delete([row.Id]) }
              ])
          }
        ],
        QueryForm: {
          PageIndex: 1,
          PageSize: 15
        },
        PageInfo: {
          Total: 0,
          PageSizeOpts: [15, 20, 50, 100, 200, 500, 1000]
        },
        EnvData: [],
        EditEnvModal: {
          Show: false,
          Title: '添加环境变量',
          Data: {
            Enable: true
          }
        },
        ruleValidate: {
          Name: [
            {
              required: true,
              message: '变量名称不能为空',
              trigger: 'blur'
            },
            {
              type: 'string',
              pattern: /^[a-zA-Z][a-zA-Z0-9_]{1,64}$/,
              message: '只能包含数字和字母下划线长度1-64位',
              trigger: 'blur'
            }
          ],
          Value: [
            {
              required: true,
              message: '变量值不能为空',
              trigger: 'blur'
            }
          ]
        }
      }
    },
    computed: {
      EnableOptions() {
        return [
          { label: '启用', value: 'true' },
          { label: '禁用', value: 'false' }
        ]
      }
    },
    methods: {
      onCheckedRowKeys(keys) {
        this.CheckedRowKeys = keys
      },
      resetQuery() {
        this.QueryForm.Key = ""
        this.QueryForm.Enable = null
        this.loadEnvs(1)
      },
      exportEnvs() {
        // 导出改为后端生成：与列表同筛选条件、全量，前端仅做带 token 的 blob 落盘
        ExportEnvs(this.QueryForm).then((response) => {
          const blob = new Blob([response.data], { type: 'text/plain;charset=utf-8' })
          const url = window.URL.createObjectURL(blob)
          const link = document.createElement('a')
          link.href = url
          link.download = 'envs.txt'
          link.click()
          window.URL.revokeObjectURL(url)
        })
      },
      pageSizeChange(size) {
        this.QueryForm.PageSize = size
        this.loadEnvs(1)
      },
      enableEnvs() {
        var ids = this.getSelectIds()
        if (ids && ids.length > 0) {
          var c = this
          this.$dialog.warning({
            title: '启用环境变量',
            content: '确定启用选择的环境变量？',
            positiveText: '是的',
            negativeText: '算了',
            onPositiveClick: function () {
              Enable(ids).then((res) => {
                c.loadEnvs(1)
              })
            }
          })
        }
      },
      disableEnvs() {
        var ids = this.getSelectIds()
        if (ids && ids.length > 0) {
          var c = this
          this.$dialog.warning({
            title: '禁用环境变量',
            content: '确定禁用选择的环境变量？',
            positiveText: '是的',
            negativeText: '算了',
            onPositiveClick: function () {
              Disable(ids).then((res) => {
                c.loadEnvs(1)
              })
            }
          })
        }
      },
      getSelectIds() {
        var ids = this.CheckedRowKeys
        if (ids === null || ids.length === 0) {
          this.$message.warning('请选中环境变量后再操作！')
          return null
        }
        return ids
      },
      deleteSelection() {
        var ids = this.getSelectIds()
        if (ids && ids.length > 0) {
          this.delete(ids)
        }
      },
      handleSubmit(name) {
        this.$refs[name].validate((errors) => {
          if (!errors) {
            this.EditEnvModal.Data.UpdateTime = null
            var data = JSON.parse(JSON.stringify(this.EditEnvModal.Data))
            PostEnvs([data]).then((res) => {
              if (res) {
                this.$message.success(this.EditEnvModal.Title + '成功！')
                this.EditEnvModal.Show = false
                this.loadEnvs()
              }
            })
          }
        })
      },
      handleCancel() {
        this.EditEnvModal.Show = false
      },
      editEnv(i) {
        if (i > -1) {
          this.EditEnvModal.Title = '修改环境变量'
          this.EditEnvModal.Data = JSON.parse(JSON.stringify(this.EnvData[i]))
        } else {
          this.EditEnvModal.Data = {
            Enable: true
          }
          this.EditEnvModal.Title = '添加环境变量'
        }
        this.EditEnvModal.Show = true
      },
      // 删除数据
      delete(ids) {
        var c = this
        this.$dialog.warning({
          title: '删除确认',
          content: '确定删除环境变量吗？',
          positiveText: '确认',
          negativeText: '取消',
          onPositiveClick: function () {
            DeleteEnvs(ids).then((res) => {
              c.loadEnvs(1)
            })
          }
        })
      },
      loadEnvs(pageIndex) {
        this.QueryForm.PageIndex = pageIndex || 1
        const seq = ++this.listSeq
        GetEnvs(this.QueryForm).then((res) => {
          if (seq !== this.listSeq) return
          this.EnvData = res.Data
          this.PageInfo.Total = res.TotalCount
        }).catch((error) => {
          if (seq !== this.listSeq) return
          this.$message.error(String((error && error.message) || error || '环境变量加载失败'))
        })
      }
    },
    async mounted() {
      this.loadEnvs(1)
    }
  }
</script>
