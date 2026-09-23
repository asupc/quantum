<template>
    <div>
        <n-alert type="warning">
            友情提示：因字段过多，隐藏部分标题，要查看请通过编辑功能查看
        </n-alert>

        <div class="filter-card" style="margin-top: 12px">
            <n-form :show-feedback="false" label-placement="left" label-width="auto" @keyup.enter="loadCustomTypePages(1)">
                <n-grid cols="2 s:3 m:4 l:4 xl:4" responsive="screen" :x-gap="12" :y-gap="12">
                    <n-gi>
                        <n-form-item label="关键字">
                            <n-input v-model:value="QueryForm.Key" placeholder="输入数据标题或编码搜索" clearable />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <div class="filter-actions">
                            <n-button type="primary" @click="loadCustomTypePages(1)">
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
        <n-data-table ref="customDataTitleTable" :columns="customDataTitleTable.Columns"
            :data="customDataTitleTable.Datas" :scroll-x="1600" :bordered="true" size="small" />
        <div style="margin: 10px; overflow: hidden">
            <div style="float: right">
                <n-pagination :item-count="PageInfo.Total" :page="QueryForm.PageIndex" :page-size="QueryForm.PageSize"
                    @update:page-size="pageSizeChange" :page-sizes="PageInfo.PageSizeOpts" show-size-picker
                    @update:page="loadCustomTypePages">
                    <template #prefix>
                        <span class="mono">共 {{ PageInfo.Total }} 条</span>
                    </template>
                </n-pagination>
            </div>
        </div>
        <n-modal preset="card" v-model:show="customDataTitleUpdateModal.Show"
            :title="customDataTitleUpdateModal.Title" style="width: 800px" :mask-closable="false">
            <n-form ref="EditUserForm" :model="customDataTitleUpdateModal.Data" label-placement="left" label-width="80">
                <n-grid :cols="2" :x-gap="12">
                    <n-gi>
                        <n-form-item label="类型编码">
                            <n-input v-model:value="customDataTitleUpdateModal.Data.Type" disabled />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="类型名称">
                            <n-input v-model:value="customDataTitleUpdateModal.Data.TypeName" />
                        </n-form-item>
                    </n-gi>
                    <n-gi v-for="idx in 15" :key="idx">
                        <n-form-item :label="'标题' + idx">
                            <n-input v-model:value="customDataTitleUpdateModal.Data['Title' + idx]" />
                        </n-form-item>
                    </n-gi>
                </n-grid>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="customDataTitleUpdateModal.Show = false" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="submitCustomTypePages()">提交</n-button>
                </div>
            </template>
        </n-modal>

        <n-modal preset="card" v-model:show="deleteModal.show" style="width: 500px">
            <template #header>
                <p style="color: var(--warn); text-align: center">
                    <i class="fa-solid fa-circle-info"></i>
                    <span> 删除数据标题</span>
                </p>
            </template>
            <div style="font-size: 14px">
                删除数据标题后无法查看对应数据，或者您可以选择直接删除对应数据，但是该操作无法恢复，请谨慎操作！
            </div>
            <template #footer>
                <div style="text-align: center">
                    <n-button type="error" @click="deleteCustomDataTitle()" style="margin-right: 8px">删除标题</n-button>
                    <n-button type="error" @click="deleteCustomDataTitle(true)">删除标题及数据</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>
<script>
  import { h } from 'vue'
  import { renderOpActions, OpColor } from '@/utils/op-actions'
  import {
    GetCustomTypePages,
    UpdateCustomeDataTitle,
    DeleteCustomeDataTitle
  } from '@/api/customerData.js'

  export default {
    name: 'CustomDataTitle',
    data() {
      return {
        deleteModal: {
          show: false
        },
        customDataTitleTable: {
          Columns: [{
            title: '序号',
            width: 60,
            align: 'center',
            render: (row, index) => index + 1
          },
          {
            title: '类型编码',
            key: 'Type',
            ellipsis: { tooltip: true },
            width: 180,
            className: 'mono',
            render: (row) => {
              return this.tdTip(h, { row }, 'Type')
            }
          },
          {
            title: '类型名称',
            key: 'TypeName',
            ellipsis: { tooltip: true },
            minWidth: 180,
            render: (row) => {
              return this.tdTip(h, { row }, 'TypeName')
            }
          },
          {
            title: '标题1',
            key: 'Title1',
            minWidth: 150,
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'Title1')
            }
          },
          {
            title: '标题2',
            key: 'Title2',
            minWidth: 150,
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'Title2')
            }
          },
          {
            title: '标题3',
            key: 'Title3',
            minWidth: 150,
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'Title3')
            }
          },
          {
            title: '标题4',
            minWidth: 150,
            key: 'Title4',
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'Title4')
            }
          },
          {
            title: '标题5',
            key: 'Title5',
            minWidth: 150,
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'Title5')
            }
          },
          {
            title: '标题6',
            key: 'Title6',
            minWidth: 150,
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'Title6')
            }
          },
          {
            title: '标题7',
            key: 'Title7',
            minWidth: 150,
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'Title7')
            }
          },
          {
            title: '标题8',
            key: 'Title8',
            minWidth: 150,
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'Title8')
            }
          },
          {
            title: '操作',
            key: 'action',
            width: 100,
            align: 'center',
            fixed: 'right',
            render: (row) =>
              renderOpActions(h, [
                { icon: 'fa-pencil', title: '编辑', color: OpColor.Edit, onClick: () => this.UpdateEvent(row) },
                { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.deleteEvent(row) }
              ])
          }
          ],
          Datas: []
        },
        QueryForm: {
          PageSize: 15,
          PageIndex: 1,
          CommunicationType: null,
          Key: ''
        },
        PageInfo: {
          PageSizeOpts: [15, 50, 100, 200, 500, 1000],
          Total: 0
        },
        customDataTitleUpdateModal: {
          Title: '修改数据标题',
          Show: false,
          Data: {}
        }
      }
    },
    methods: {
      deleteCustomDataTitle(d) {
        var that = this
        DeleteCustomeDataTitle(this.deleteModal.type, d).then(res => {
          that.loadCustomTypePages(1);
          that.deleteModal.show = false
          that.$message.success('删除成功！')
        })
      },
      deleteEvent(row) {
        this.deleteModal.show = true;
        this.deleteModal.type = row.Type;
      },
      pageSizeChange(pageSize) {
        this.QueryForm.PageSize = pageSize
        this.loadCustomTypePages(1)
      },
      loadCustomTypePages(pageIndex) {
        this.QueryForm.PageIndex = pageIndex || 1
        GetCustomTypePages(this.QueryForm).then((res) => {
          this.customDataTitleTable.Datas = res.Data
          this.PageInfo.Total = res.TotalCount
        })
      },
      resetQuery() {
        this.QueryForm.Key = '';
        this.loadCustomTypePages(1);
      },
      submitCustomTypePages() {
        UpdateCustomeDataTitle(this.customDataTitleUpdateModal.Data).then((res) => {
          this.loadCustomTypePages(1)
          this.customDataTitleUpdateModal.Show = false
        })
      },
      UpdateEvent(row) {
        this.customDataTitleUpdateModal.Data = JSON.parse(JSON.stringify(row))
        this.customDataTitleUpdateModal.Show = true
      }
    },
    mounted() {
      this.loadCustomTypePages(1)
    }
  }
</script>
