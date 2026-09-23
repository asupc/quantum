<template>
    <div>
        <div class="filter-card">
            <n-form :show-feedback="false" label-placement="left" label-width="auto" @keyup.enter="LoadCommand(1)">
                <n-grid cols="2 s:3 m:4 l:4 xl:4" responsive="screen" :x-gap="12" :y-gap="12">
                    <n-gi>
                        <n-form-item label="关键字">
                            <n-input v-model:value="QueryForm.Key" placeholder="输入搜索关键字" clearable />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <div class="filter-actions">
                            <n-button type="primary" @click="LoadCommand(1)">
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
                <n-button type="primary" size="small" @click="editCommand(-1)">
                    <template #icon>
                        <i class="fa-solid fa-plus"></i>
                    </template>
                    添加
                </n-button>
                <n-button type="error" size="small" @click="deleteSelection">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    删除
                </n-button>
            </n-space>
        </div>
        <n-data-table ref="EnvTable" :columns="CommandColumns" :data="CommandData" :row-key="(row) => row.Id"
            :checked-row-keys="CheckedRowKeys" @update:checked-row-keys="onCheckedRowKeys" :scroll-x="1100"
            :bordered="true" size="small" />

        <div style="margin: 10px; overflow: hidden">
            <div style="float: right">
                <n-pagination :item-count="PageInfo.Total" :page="QueryForm.PageIndex" :page-size="QueryForm.PageSize"
                    :page-sizes="PageInfo.PageSizeOpts" show-size-picker @update:page-size="pageSizeChange"
                    @update:page="LoadCommand">
                    <template #prefix>
                        <span class="mono">共 {{ PageInfo.Total }} 条</span>
                    </template>
                </n-pagination>
            </div>
        </div>
        <n-modal preset="card" v-model:show="EditCommandModal.Show" :title="EditCommandModal.Title" style="width: 600px"
            :mask-closable="false">
            <n-form ref="editCommandForm" :model="EditCommandModal.Data" :rules="ruleValidate" label-placement="left"
                label-width="80">
                <n-form-item label="指令" path="Key">
                    <n-input-group>
                        <n-input v-model:value="EditCommandModal.Data.Key" placeholder="输入快捷回复指令" />
                        <n-checkbox v-model:checked="EditCommandModal.Data.EnableRegex" style="padding: 0 10px">正则
                        </n-checkbox>
                    </n-input-group>
                </n-form-item>
                <n-form-item label="回复内容" path="Message">
                    <n-input type="textarea" v-model:value="EditCommandModal.Data.Message" :rows="3" />
                </n-form-item>
                <n-form-item label="消息类型" path="MessageType">
                    <n-select clearable v-model:value="EditCommandModal.Data.MessageType"
                        :options="enumOptions('MessageType')" />
                </n-form-item>
                <n-form-item label="通信类型" path="CommunicationType">
                    <n-select clearable placeholder="指定通讯类型，如选择QQ，则只有QQ用户发送指令才回复"
                        v-model:value="EditCommandModal.Data.CommunicationType"
                        :options="enumOptions('CommunicationType')" />
                </n-form-item>
                <n-form-item label="是否启用">
                    <n-checkbox v-model:checked="EditCommandModal.Data.Enable">启用</n-checkbox>
                </n-form-item>
                <n-form-item label="备注">
                    <n-input type="textarea" v-model:value="EditCommandModal.Data.Remark" :rows="2" />
                </n-form-item>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="handleCancel()" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="handleSubmit('editCommandForm')">提交</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>
<script>
  import { h } from 'vue'
  import { renderOpActions, OpColor } from '@/utils/op-actions'
  import {
    GetCommand,
    PostCommand,
    UpdateCommand,
    DeleteCommand
  } from '@/api/command.js'
  import { getEnumKey } from '@/libs/util.js'
  import { AllEnumsCached } from '@/api/enum.js'
  export default {
    name: 'ReplayIndex',
    data() {
      return {
        listSeq: 0,
        CheckedRowKeys: [],
        CommandColumns: [
          {
            type: 'selection'
          }, {
            title: '序号',
            width: 60,
            align: 'center',
            render: (row, index) => index + 1
          },
          {
            title: '指令',
            key: 'Key',
            width: 140,
            className: 'mono'
          },
          {
            title: '消息类型',
            key: 'MessageType',
            width: 90,
            align: 'center',
            render: (row) => {
              return h('div', [
                h(
                  'span',
                  getEnumKey(
                    this.AllEnums,
                    'MessageType',
                    row.MessageType
                  ) || '文本'
                )
              ])
            }
          },
          {
            title: '通讯类型',
            key: 'CommunicationType',
            width: 90,
            align: 'center',
            render: (row) => {
              return h('div', [
                h(
                  'span',
                  getEnumKey(
                    this.AllEnums,
                    'CommunicationType',
                    row.CommunicationType
                  ) || '全部'
                )
              ])
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
            title: '回复内容',
            key: 'Message',
            minWidth: 280,
            render: (row) => {
              return this.tdTip(h, { row }, 'Message')
            }
          },
          {
            title: '备注',
            key: 'Remark',
            minWidth: 150,
            render: (row) => {
              return this.tdTip(h, { row }, 'Remark')
            }
          },
          {
            title: '操作',
            key: 'action',
            width: 100,
            align: 'center',
            fixed: 'right',
            render: (row, index) =>
              renderOpActions(h, [
                { icon: 'fa-pencil', title: '编辑', color: OpColor.Edit, onClick: () => this.editCommand(index) },
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
          PageSizeOpts: [15, 50, 100]
        },
        AllEnums: {},
        CommandData: [],
        EditCommandModal: {
          Show: false,
          Title: '添加快捷回复',
          Data: {
            Enable: true,
            EnableAll: false,
            EnvType: '',
            Weight: 1
          }
        },
        ruleValidate: {
          Name: [
            {
              required: true,
              message: '指令不能为空',
              trigger: 'blur'
            }
          ],
          Value: [
            {
              required: true,
              message: '回复内容不能为空',
              trigger: 'blur'
            }
          ]
        }
      }
    },
    methods: {
      onCheckedRowKeys(keys) {
        this.CheckedRowKeys = keys
      },
      enumOptions(type) {
        return (this.AllEnums[type] || []).map((t) => ({
          label: t.Key,
          value: t.Value
        }))
      },
      // 批量删除快捷回复
      deleteSelection() {
        var ids = [...this.CheckedRowKeys]
        if (ids.length === 0) {
          this.$message.warning('请选中后再操作！')
          return
        }
        this.delete(ids)
      },
      handleSubmit(name) {
        this.$refs[name].validate((errors) => {
          if (!errors) {
            var data = JSON.parse(JSON.stringify(this.EditCommandModal.Data))
            // 有 Id 为编辑（PUT），无 Id 为新增（POST）
            const savePromise = data.Id ? UpdateCommand(data) : PostCommand(data);
            savePromise.then(res => {
              if (res) {
                this.$message.success(this.EditCommandModal.Title + '成功！')
                this.EditCommandModal.Show = false
                this.LoadCommand(1)
              }
            })
          }
        })
      },
      handleCancel() {
        this.EditCommandModal.Show = false
      },
      editCommand(i) {
        if (i > -1) {
          this.EditCommandModal.Title = '修改快捷回复'
          this.EditCommandModal.Data = JSON.parse(
            JSON.stringify(this.CommandData[i])
          )
        } else {
          this.EditCommandModal.Data = {
            Key: '',
            Message: '',
            CommunicationType: null,
            MessageType: 1,
            Enable: true
          }
          this.EditCommandModal.Title = '添加快捷回复'
        }
        this.EditCommandModal.Show = true
      },
      // 删除数据
      delete(ids) {
        var c = this
        this.$dialog.warning({
          title: '删除确认',
          content: '确定删除快捷回复吗？',
          positiveText: '确认',
          negativeText: '取消',
          onPositiveClick: function () {
            DeleteCommand(ids).then(res => {
              c.LoadCommand(1)
            })
          }
        })
      },
      loadAllEnums() {
        return AllEnumsCached().then(res => {
          this.AllEnums = res
        })
      },
      pageSizeChange(size) {
        this.QueryForm.PageSize = size
        this.LoadCommand(1)
      },
      LoadCommand(pageIndex) {
        this.QueryForm.PageIndex = pageIndex || 1
        const seq = ++this.listSeq
        return GetCommand(this.QueryForm).then(res => {
          if (seq !== this.listSeq) return
          this.CommandData = res.Data
          this.PageInfo.Total = res.TotalCount
        }).catch((error) => {
          if (seq !== this.listSeq) return
          this.$message.error(String((error && error.message) || error || '快捷回复加载失败'))
        })
      },
      resetQuery() {
        this.QueryForm.Key = ""
        this.LoadCommand(1)
      }
    },
    mounted() {
      // 枚举与首屏列表并行拉取，消除请求瀑布（§3-12）
      Promise.all([this.loadAllEnums(), this.LoadCommand(1)])
    }
  }
</script>
