<style scoped>
.showTitle {
    margin: 18px 0;
}

.showTitle p {
    margin-bottom: 12px;
}
</style>
<template>
    <div>
        <div class="filter-card">
            <n-form :show-feedback="false" label-placement="left" label-width="auto" @keyup.enter="loadCustomData(1)">
                <n-grid cols="2 s:2 m:3 l:4 xl:4" responsive="screen" :x-gap="12" :y-gap="12">
                    <n-gi v-for="tt in CustomDataUpdateModal.Columns" :key="tt.key">
                        <n-form-item :label="tt.title">
                            <n-input v-model:value="QueryForm[tt.key]" clearable
                                :placeholder="'请输入' + tt.title" />
                        </n-form-item>
                    </n-gi>
                    <n-gi span="1 m:2">
                        <n-form-item label="时间范围">
                            <n-date-picker style="width: 100%;" type="datetimerange" :shortcuts="DateRangeShortcuts"
                                v-model:value="DataRangeValue" clearable />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <div class="filter-actions">
                            <n-button type="primary" @click="loadCustomData(1)">
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
        <div style="margin-bottom: 10px">
            <n-space>
                <n-button type="primary" size="small" @click="exportExcel">
                    <template #icon>
                        <i class="fa-solid fa-file-csv"></i>
                    </template>
                    导出Excel
                </n-button>
                <n-button type="primary" size="small" @click="addCustomData">
                    <template #icon>
                        <i class="fa-solid fa-plus"></i>
                    </template>
                    添加
                </n-button>
                <n-button type="primary" size="small" @click="showEcharts">
                    <template #icon>
                        <i class="fa-solid fa-heart-pulse"></i>
                    </template>
                    可视化
                </n-button>
                <n-button type="error" size="small" @click="deleteSelection">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    删除
                </n-button>
                <n-button type="error" size="small" @click="clearSelection">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    清空
                </n-button>
            </n-space>
        </div>
        <div class="showTitle">
            <n-checkbox v-model:checked="item.Enable" @update:checked="(v) => changeShowTitles(item, v)"
                v-for="item in ShowTitles" :key="item.Key">{{ item.Title }}
            </n-checkbox>
        </div>
        <n-data-table border ref="CustomDataTable" :columns="CustomDataTable.Columns" :data="CustomDataTable.Datas"
            :row-key="(row) => row.Id" :checked-row-keys="CheckedRowKeys" @update:checked-row-keys="onCheckedRowKeys"
            :scroll-x="1200" size="small" />

        <div style="margin: 10px; overflow: hidden">
            <div style="float: right">
                <n-pagination :item-count="PageInfo.Total" :page="QueryForm.PageIndex" :page-size="QueryForm.PageSize"
                    @update:page-size="pageSizeChange" :page-sizes="PageInfo.PageSizeOpts" show-size-picker
                    @update:page="loadCustomData">
                    <template #prefix>
                        <span class="mono">共 {{ PageInfo.Total }} 条</span>
                    </template>
                </n-pagination>
            </div>
        </div>
        <n-modal preset="card" v-model:show="CustomDataUpdateModal.Show" :title="CustomDataUpdateModal.Title"
            style="width: 800px" :mask-closable="false">
            <n-form ref="EditCustomDataForm" label-placement="left" label-width="140">
                <n-grid :cols="2" :x-gap="12">
                    <n-gi v-for="tt in CustomDataUpdateModal.Columns" :key="tt.key">
                        <n-form-item :label="tt.title">
                            <n-input v-model:value="CustomDataUpdateModal.Data[tt.key]" :placeholder="tt.title" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="创建时间">
                            <n-input v-model:value="CustomDataUpdateModal.Data.CreateTime" placeholder="创建时间" />
                        </n-form-item>
                    </n-gi>
                </n-grid>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="CustomDataUpdateModal.Show = false" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="submitCustomData()">提交</n-button>
                </div>
            </template>
        </n-modal>
        <n-modal preset="card" v-model:show="CustomDataUpdateColumnModal.Show"
            :title="CustomDataUpdateColumnModal.Title" style="width: 600px" :mask-closable="false">
            <n-form label-placement="left" label-width="120">
                <n-form-item :label="CustomDataUpdateColumnModal.ColumnText">
                    <n-input v-model:value="CustomDataUpdateColumnModal.Data" />
                </n-form-item>
            </n-form>

            <template #footer>
                <div style="text-align: right">
                    <n-button @click="CustomDataUpdateColumnModal.Show = false" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="updateColumnData()">提交</n-button>
                </div>
            </template>
        </n-modal>

        <n-modal preset="card" v-model:show="echarts.Show" :title="echarts.Title" style="width: 1200px"
            :mask-closable="false">
            <n-form :model="echarts" label-placement="left" label-width="80" :show-feedback="false">
                <n-grid :cols="24" :x-gap="12">
                    <n-gi :span="6">
                        <n-form-item label="图表类型">
                            <n-select clearable v-model:value="echarts.Type" :options="chartTypeOptions"
                                @update:value="echartsTypeChange" />
                        </n-form-item>
                    </n-gi>
                    <template v-if="echarts.Type == 'line'">
                        <n-gi :span="6">
                            <n-form-item label="X轴">
                                <n-select clearable v-model:value="echarts.lineData.X" :options="columnOptions"
                                    @update:value="setEchartOption" />
                            </n-form-item>
                        </n-gi>
                        <n-gi :span="12">
                            <n-form-item label="Y轴" :show-feedback="false">
                                <n-select clearable v-model:value="echarts.lineData.Y" multiple
                                    :options="columnYOptions('line')" @update:value="setEchartOption" />
                            </n-form-item>
                        </n-gi>
                    </template>
                    <template v-if="echarts.Type == 'pie'">
                        <n-gi :span="6">
                            <n-form-item label="标题">
                                <n-select clearable v-model:value="echarts.pieData.X" :options="columnOptions"
                                    @update:value="setEchartOption" />
                            </n-form-item>
                        </n-gi>
                        <n-gi :span="12">
                            <n-form-item label="数值" :show-feedback="false">
                                <n-select clearable v-model:value="echarts.pieData.Y" multiple
                                    :options="columnYOptions('pie')" @update:value="setEchartOption" />
                            </n-form-item>
                        </n-gi>
                    </template>
                </n-grid>
            </n-form>
            <div id="csdEcharts" style="height: 400px; width: 100%"></div>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="echarts.Show = false" style="margin-left: 8px">关闭</n-button>
                </div>
            </template>
        </n-modal>

    </div>
</template>
<script>
  import { h } from 'vue'
  import { renderOpActions, OpColor } from '@/utils/op-actions'
  import {
    GetCustomData,
    DeleteCustomData,
    UpdateCustomData,
    GetCustomTypes,
    AddCustomData,
    UpdateCustomDatas,
    ClearCustomData,
    ExportCustomData
  } from '@/api/customerData.js'

  import dayjs from 'dayjs';

  import * as echarts from 'echarts';
  import { markRaw } from 'vue';

  export default {
    name: 'CustomDataIndex',
    data() {
      return {
        listSeq: 0,
        allChartData: [], // 存储所有用于图表的数据
        echarts: {
          obj: {},
          Show: false,
          lineData: {
            X: null,
            Y: []
          },
          barData: {
            X: null,
            Y: []
          },
          pieData: {
            X: null,
            Y: []
          },
          Title: "数据可视化",
          Type: "line"
        },
        ShowTitles: [],
        TypeName: null,
        CheckedRowKeys: [],
        CustomDataUpdateColumnModal: {
          Show: false,
          Title: '',
          ColumnName: '',
          Data: '',
          ColumnText: ''
        },
        CustomDataTable: {
          Columns: [],
          Datas: []
        },
        QueryForm: {
          PageSize: 15,
          PageIndex: 1,
          CustomDataType: null,
          Key: ''
        },
        DataRangeValue: null,
        DateRangeShortcuts: [
          {
            text: '今天',
            value: () => {
              const end = new Date()
              const start = new Date()
              end.setTime(start.getTime() + 3600 * 1000 * 24)
              return [start.getTime(), end.getTime()]
            }
          },
          {
            text: '最近一周',
            value: () => {
              const end = new Date()
              const start = new Date()
              start.setTime(start.getTime() - 3600 * 1000 * 24 * 7)
              return [start.getTime(), end.getTime()]
            }
          },
          {
            text: '最近一个月',
            value: () => {
              const end = new Date()
              const start = new Date()
              start.setTime(start.getTime() - 3600 * 1000 * 24 * 30)
              return [start.getTime(), end.getTime()]
            }
          },
          {
            text: '最近三个月',
            value: () => {
              const end = new Date()
              const start = new Date()
              start.setTime(start.getTime() - 3600 * 1000 * 24 * 90)
              return [start.getTime(), end.getTime()]
            }
          },
          {
            text: '最近半年',
            value: () => {
              const end = new Date()
              const start = new Date()
              start.setTime(start.getTime() - 3600 * 1000 * 24 * 180)
              return [start.getTime(), end.getTime()]
            }
          },
          {
            text: '最近一年',
            value: () => {
              const end = new Date()
              const start = new Date()
              start.setTime(start.getTime() - 3600 * 1000 * 24 * 365)
              return [start.getTime(), end.getTime()]
            }
          }
        ],
        PageInfo: {
          PageSizeOpts: [
            15, 50, 100, 200, 500, 1000, 2000, 5000
          ],
          Total: 0
        },
        CustomDataUpdateModal: {
          Title: '修改数据信息',
          Show: false,
          Data: {},
          Columns: [],
          Types: [{
            Key: "折线图",
            Value: "line"
          }, {
            Key: "饼图",
            Value: "pie"
          }]
        },
        CustomDataTypes: []
      }
    },
    computed: {
      chartTypeOptions() {
        return this.CustomDataUpdateModal.Types.map((t) => ({
          label: t.Key,
          value: t.Value
        }))
      },
      columnOptions() {
        return this.CustomDataUpdateModal.Columns.map((t) => ({
          label: t.title,
          value: t.key
        }))
      }
    },
    methods: {
      onCheckedRowKeys(keys) {
        this.CheckedRowKeys = keys
      },
      columnYOptions(type) {
        const x = type === 'line' ? this.echarts.lineData.X : this.echarts.pieData.X
        return this.CustomDataUpdateModal.Columns
          .filter((t) => t.key !== x)
          .map((t) => ({ label: t.title, value: t.key }))
      },
      echartsTypeChange() {
        this.echarts.lineData.X = null;
        this.echarts.lineData.Y = [];
        this.setEchartOption();
      },

      showEcharts() {
        // 显示图表前先获取所有符合条件的数据
        this.loadAllCustomDataForChart().then(() => {
          this.echarts.Show = true;
          this.$nextTick(() => {
            let d = document.getElementById('csdEcharts');
            if (d) {
              this.echarts.obj = markRaw(echarts.init(d, 'dark'));
              this.setEchartOption();
            }
          });
        });
      },

      // 获取所有符合条件的数据用于图表展示
      loadAllCustomDataForChart() {
        return new Promise((resolve) => {
          // 创建一个新的查询对象，设置较大的PageSize以获取所有数据
          const queryForAllData = {
            ...this.QueryForm,
            PageIndex: 1,
            PageSize: 999999 // 设置一个足够大的数字以获取所有数据
          };

          // 处理时间范围
          queryForAllData.createTimeStart = this.DataRangeValue && this.DataRangeValue[0] ? dayjs(this.DataRangeValue[0]).format('YYYY-MM-DD') : null;
          queryForAllData.createTimeEnd = this.DataRangeValue && this.DataRangeValue[1] ? dayjs(this.DataRangeValue[1]).format('YYYY-MM-DD') : null;

          GetCustomData(this.QueryForm.CustomDataType, queryForAllData).then((res) => {
            // 临时存储所有数据用于图表展示
            this.allChartData = Object.freeze(res.Data || []);
            resolve();
          }).catch(() => {
            // 如果获取失败，使用当前页面数据
            this.allChartData = Object.freeze([...this.CustomDataTable.Datas]);
            resolve();
          });
        });
      },

      lineEcharts() {
        if (this.echarts.lineData.X && this.echarts.lineData.Y && this.echarts.lineData.Y.length > 0) {
          // 绘制图表，使用所有数据而不是当前页数据
          let datat = (this.allChartData || this.CustomDataTable.Datas).slice().reverse();
          let option = {
            backgroundColor: 'transparent',
            tooltip: {
              trigger: 'axis'
            },
            legend: {
              data: []
            },
            grid: {
              left: '3%',
              right: '4%',
              bottom: '3%',
              containLabel: true
            },
            toolbox: {
              feature: {
                saveAsImage: {
                  backgroundColor: '#111A2E'
                }
              }
            },
            xAxis: {
              type: 'category',
              boundaryGap: false,
              data: datat.map(res => res[this.echarts.lineData.X])
            },
            yAxis: {
              type: 'value'
            },
            series: []
          };


          for (let yy = 0; yy < this.echarts.lineData.Y.length; yy++) {
            const element = this.echarts.lineData.Y[yy];
            let c = this.CustomDataUpdateModal.Columns.filter(n => n.key == element)[0]
            option.legend.data.push(c.title)
            option.series.push({
              name: c.title,
              type: "line",
              stack: 'total',
              label: {
                show: true
              },
              emphasis: {
                focus: 'series'
              },
              data: datat.map(res => res[element])
            })
          }

          this.echarts.obj.clear();
          this.echarts.obj.setOption(option);
        } else {
          this.echarts.obj.clear();
        }
      },
      pieEcharts() {
        // 使用所有数据而不是当前页数据
        let datat = this.allChartData || this.CustomDataTable.Datas;

        if (this.echarts.pieData.X && this.echarts.pieData.Y && this.echarts.pieData.Y.length > 0 && datat.length > 0) {
          const xField = this.echarts.pieData.X;
          const yField = this.echarts.pieData.Y[0]; // 饼图通常只需要一个Y轴字段


          var t = this.CustomDataUpdateModal.Columns.find(n => n.key === xField)

          // 获取字段标题
          const xTitle = t && t.title || xField;
          const yTitle = t && t.title || yField;

          // 构造饼图数据
          const seriesData = datat.map(item => ({
            name: item[xField],
            value: parseFloat(item[yField]) || 0
          })).filter(item => !isNaN(item.value));

          const legendData = seriesData.map(item => item.name);

          const option = {
            backgroundColor: 'transparent',
            title: {
              text: `${yTitle}分布`,
              left: 'center'
            },
            tooltip: {
              trigger: 'item',
              formatter: '{a} <br/>{b} : {c} ({d}%)'
            },
            legend: {
              type: 'scroll',
              orient: 'vertical',
              right: 10,
              top: 20,
              bottom: 20,
              data: legendData
            },
            series: [
              {
                name: yTitle,
                type: 'pie',
                radius: '55%',
                center: ['40%', '50%'],
                data: seriesData,
                emphasis: {
                  itemStyle: {
                    shadowBlur: 10,
                    shadowOffsetX: 0,
                    shadowColor: 'rgba(0, 0, 0, 0.5)'
                  }
                }
              }
            ]
          };

          this.echarts.obj.clear();
          this.echarts.obj.setOption(option);
        } else {
          this.echarts.obj.clear();
        }
      },


      /**
       * 设置Echarts
       */
      setEchartOption() {
        switch (this.echarts.Type) {
          case "pie":
            this.pieEcharts();
            break;
          case "line":
            this.lineEcharts();
            break;
          default:
            this.echarts.obj.clear();
            break
        }
      },
      exportExcel() {
        // 导出改为后端生成 CSV：与列表同筛选条件、全量不分页（表头由后端按类型标题定义生成，不再跟随本页显隐偏好），前端仅做带 token 的 blob 落盘
        const query = {
          ...this.QueryForm,
          createTimeStart: this.DataRangeValue && this.DataRangeValue[0] ? dayjs(this.DataRangeValue[0]).format('YYYY-MM-DD') : null,
          createTimeEnd: this.DataRangeValue && this.DataRangeValue[1] ? dayjs(this.DataRangeValue[1]).format('YYYY-MM-DD') : null
        }
        ExportCustomData(this.QueryForm.CustomDataType, query).then((response) => {
          const blob = new Blob([response.data], { type: 'text/csv;charset=utf-8;' })
          const url = window.URL.createObjectURL(blob)
          const link = document.createElement('a')
          link.href = url
          link.download = (this.TypeName || 'custom-data') + '.csv'
          link.click()
          window.URL.revokeObjectURL(url)
        })
      },
      pageSizeChange(pageSize) {
        this.QueryForm.PageSize = pageSize
        this.loadCustomData(1)
      },
      loadCustomData(pageIndex) {
        if (!pageIndex) {
          pageIndex = 1
        }
        this.QueryForm.PageIndex = pageIndex
        this.QueryForm.createTimeStart = this.DataRangeValue && this.DataRangeValue[0] ? dayjs(this.DataRangeValue[0]).format('YYYY-MM-DD') : null
        this.QueryForm.createTimeEnd = this.DataRangeValue && this.DataRangeValue[1] ? dayjs(this.DataRangeValue[1]).format('YYYY-MM-DD') : null
        const seq = ++this.listSeq
        GetCustomData(this.QueryForm.CustomDataType, this.QueryForm).then(
          (res) => {
            if (seq !== this.listSeq) return
            this.CustomDataTable.Datas = res.Data
            this.PageInfo.Total = res.TotalCount
          }
        ).catch((error) => {
          if (seq !== this.listSeq) return
          this.$message.error(String((error && error.message) || error || '数据加载失败'))
        })
      },
      resetQuery() {
        const type = this.QueryForm.CustomDataType;
        const size = this.QueryForm.PageSize || 15;
        this.QueryForm = {
          PageIndex: 1,
          PageSize: size,
          CustomDataType: type
        };
        this.DataRangeValue = null;
        this.loadCustomData(1);
      },
      updateColumnData() {
        var that = this
        var ids = [...this.CheckedRowKeys]
        this.$dialog.warning({
          title: '操作确认',
          content: `确认批量修改${(ids.length > 0) ? '选中的' : '本页'}【${this.TypeName}】-【${this.CustomDataUpdateColumnModal.ColumnText}】字段数据为：【${this.CustomDataUpdateColumnModal.Data}】？`,
          positiveText: '确定',
          negativeText: '取消',
          onPositiveClick: function () {
            var updateDatas = []

            that.CustomDataTable.Datas.forEach(element => {
              var item = JSON.parse(JSON.stringify(element))
              item[that.CustomDataUpdateColumnModal.ColumnName] = that.CustomDataUpdateColumnModal.Data
              if (!ids || ids.length == 0 || ids.indexOf(element.Id) > -1) {
                updateDatas.push(item)
              }
            })
            UpdateCustomDatas(updateDatas).then((res) => {
              that.loadCustomData(1)
              that.CustomDataUpdateColumnModal.Show = false
            })
          }
        })
      },
      submitCustomData() {
        if (this.CustomDataUpdateModal.Data.Id) {
          UpdateCustomData(this.CustomDataUpdateModal.Data).then((res) => {
            this.loadCustomData(1)
            this.CustomDataUpdateModal.Show = false
          })
        } else {
          AddCustomData([this.CustomDataUpdateModal.Data]).then((res) => {
            this.loadCustomData(1)
            this.CustomDataUpdateModal.Show = false
          })
        }
      },
      Update(row) {
        this.CustomDataUpdateModal.Data = JSON.parse(JSON.stringify(row))
        this.CustomDataUpdateModal.Show = true
      },
      getSelectIds() {
        var ids = this.CheckedRowKeys
        if (ids === null || ids.length === 0) {
          this.$message.warning('请选中数据后再操作！')
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
      clearSelection() {
        var c = this
        this.$dialog.warning({
          title: '清除确认',
          content: `确认清除：【${this.TypeName}】所有数据？该操作不可逆！`,
          positiveText: '清除',
          negativeText: '取消',
          onPositiveClick: function () {
            ClearCustomData(c.QueryForm.CustomDataType).then((res) => {
              c.$message.success(`清除：【${c.TypeName}】所有数据完成！`)
              c.loadCustomData(1)
            })
          }
        })
      },
      addCustomData() {
        this.CustomDataUpdateModal.Data = {
          Type: this.QueryForm.CustomDataType
        }
        this.CustomDataUpdateModal.Show = true
      },
      delete(ids) {
        var c = this
        this.$dialog.warning({
          title: '删除确认',
          content: '确定删除选中的数据？',
          positiveText: '确认',
          negativeText: '取消',
          onPositiveClick: function () {
            DeleteCustomData(ids).then((res) => {
              c.loadCustomData(c.QueryForm.PageIndex)
            })
          }
        })
      },
      async getAllCustomDataTypes() {
        var data = await GetCustomTypes()
        this.CustomDataTypes = data
        if (this.CustomDataTypes && this.CustomDataTypes.length > 0) {
          this.getTitles()
          this.customDataTypeChange()
          this.changeTableHeader()
        }
      },
      customDataTypeChange() {
        this.loadCustomData(1)
        this.CustomDataUpdateModal.Columns = []
        var t = this.CustomDataTypes.filter(
          (d) => d.Type == this.QueryForm.CustomDataType
        )[0]
        if (!t) return
        for (var s in t) {
          for (
            let index = 0;
            index < this.CustomDataUpdateModal.Columns.length;
            index++
          ) {
            this.QueryForm[this.CustomDataUpdateModal.Columns[index].key] = ''
          }
          if (t[s] && s != 'Id' && s != 'Type' && s != 'TypeName') {
            var n = s.replace('Title', 'Data')
            var ru = {
              title: t[s],
              key: n
            }
            this.CustomDataUpdateModal.Columns.push(ru)
          }
        }
      },
      getTitles() {
        this.ShowTitles = []
        var t = this.CustomDataTypes.filter(
          (d) => d.Type == this.QueryForm.CustomDataType
        )[0]
        if (!t) return

        var sssss = localStorage.getItem('ShowTitles:' + this.QueryForm.CustomDataType)
        if (sssss) {
          try {
            sssss = JSON.parse(sssss)
          } catch (e) {
            // 脏数据自愈：清掉坏 key
            localStorage.removeItem('ShowTitles:' + this.QueryForm.CustomDataType)
            sssss = null
          }
        }
        for (let key in t) {
          if (key.indexOf('Title') > -1 && t[key]) {
            this.ShowTitles.push({
              Key: key,
              Title: t[key],
              Enable: !sssss || sssss.length == 0 || sssss.indexOf(key) > -1
            })
          }
        }
        this.ShowTitles.push({
          Key: 'CreateTime',
          Title: '创建时间',
          Enable: !sssss || sssss.length == 0 || sssss.indexOf('CreateTime') > -1
        })
      },
      changeTableHeader() {
        var t = this.CustomDataTypes.filter(
          (d) => d.Type == this.QueryForm.CustomDataType
        )[0]
        if (!t) return
        this.TypeName = t.TypeName
        this.CustomDataTable.Columns = [
          {
            type: 'selection'
          }, {
            title: '序号',
            width: 60,
            align: 'center',
            render: (row, index) => index + 1
          }
        ]
        for (let xsi = 0; xsi < this.ShowTitles.length; xsi++) {
          const itemx = this.ShowTitles[xsi]
          if (itemx.Enable) {
            // 列标题/单元格渲染均为闭包，必须用块级变量，否则所有列都捕获到最后一列的值
            const columnKey = itemx.Key.replace('Title', 'Data')
            const columnTitle = itemx.Title
            var ru = {
              title: () => {
                return h('div', [
                  h('a',
                    {
                      style: {
                        width: '100%',
                        overflow: 'hidden',
                        textOverflow: 'ellipsis',
                        whiteSpace: 'nowrap',
                        cursor: 'pointer'
                      },
                      title: columnTitle,
                      onClick: () => {
                        if (!this.CustomDataTable.Datas || this.CustomDataTable.Datas.length == 0) {
                          this.$message.warning('当前页面无数据，无法进行批量操作！')
                          return
                        }

                        this.CustomDataUpdateColumnModal.ColumnText = columnTitle
                        this.CustomDataUpdateColumnModal.Data = this.CustomDataTable.Datas[0][columnKey]
                        this.CustomDataUpdateColumnModal.ColumnName = columnKey
                        this.CustomDataUpdateColumnModal.Show = true
                        this.CustomDataUpdateColumnModal.Title = `批量修改【${this.TypeName}】【${columnTitle}】数据`
                      }
                    }, columnTitle)
                ])
              },
              key: columnKey,
              ellipsis: { tooltip: true },
              minWidth: 150,
              render: (row) => {
                return this.tdTip(h, { row }, columnKey)
              }
            }
            this.CustomDataTable.Columns.push(ru)
          }
        }
        this.CustomDataTable.Columns.push({
          title: '操作',
          key: 'action',
          width: 100,
          align: 'center',
          fixed: 'right',
          render: (row) =>
            renderOpActions(h, [
              { icon: 'fa-pencil', title: '编辑', color: OpColor.Edit, onClick: () => this.Update(row) },
              { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.delete([row.Id]) }
            ])
        })
      },
      changeShowTitles(item, checked) {
        // 显式接收本次勾选值：同名 onUpdate:checked 上 v-model 与该回调的触发顺序不保证，
        // 依赖 item.Enable 会读到旧值，导致「勾上的列反而被隐藏」的反转
        item.Enable = checked
        var selects = this.ShowTitles.filter((t) => t.Enable).map((t) => t.Key)
        localStorage.setItem('ShowTitles:' + this.QueryForm.CustomDataType, JSON.stringify(selects))
        this.changeTableHeader()
      }
    },
    mounted: function () {
      this.QueryForm.CustomDataType = location.href.split('/')[location.href.split('/').length - 1]
      // 类型加载内部已触发首屏数据拉取（customDataTypeChange → loadCustomData），不再重复请求（§3-12）
      this.getAllCustomDataTypes()
    },
    beforeUnmount() {
      try {
        // 销毁图表实例，防止内存泄漏
        if (this.echarts.obj && this.echarts.obj.dispose) {
          this.echarts.obj.dispose();
        }
      } catch (e) {

      }
    }
  }
</script>
