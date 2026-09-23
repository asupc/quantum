<template>
    <div>
        <div class="filter-card">
            <n-form :show-feedback="false" label-placement="left" label-width="auto" @keyup.enter="LoadTask(1)">
                <n-grid cols="2 s:2 m:3 l:4 xl:4" responsive="screen" :x-gap="12" :y-gap="12">
                    <n-gi>
                        <n-form-item label="关键字">
                            <n-input v-model:value="QueryForm.Key" placeholder="输入搜索关键字" clearable />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="状态">
                            <n-select v-model:value="QueryForm.Enable" :options="EnableOptions" clearable
                                @update:value="LoadTask(1)" placeholder="全部" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <div class="filter-actions">
                            <n-button type="primary" @click="LoadTask(1)">
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
                <n-button type="primary" size="small" @click="editTask(-1)">
                    <template #icon>
                        <i class="fa-solid fa-plus"></i>
                    </template>
                    添加
                </n-button>
                <n-button type="primary" size="small" @click="enableSelection()">
                    <template #icon>
                        <i class="fa-solid fa-gear"></i>
                    </template>
                    启用
                </n-button>
                <n-button type="warning" size="small" @click="disableSelection()">
                    <template #icon>
                        <i class="fa-solid fa-gear"></i>
                    </template>
                    禁用
                </n-button>
                <n-button type="error" size="small" @click="deleteSelection()">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    删除
                </n-button>
                <n-upload :show-file-list="false" :custom-request="importTask" :action="BaseUrl + '/api/Task/Import?dir='"
                    :headers="UploadHeaders">
                    <n-button type="primary" size="small">
                        <template #icon>
                            <i class="fa-solid fa-cloud-arrow-up"></i>
                        </template>
                        导入
                    </n-button>
                </n-upload>
                <n-button type="primary" size="small" @click="exportTask">
                    <template #icon>
                        <i class="fa-solid fa-cloud-arrow-down"></i>
                    </template>
                    导出
                </n-button>
            </n-space>
        </div>
        <n-data-table ref="EnvTable" :columns="TaskColumns" :data="CommadnData" :row-key="(row) => row.Id"
            :checked-row-keys="CheckedRowKeys" @update:checked-row-keys="onCheckedRowKeys" :scroll-x="1200"
            :bordered="true" size="small" />
        <div style="margin: 10px; overflow: hidden">
            <div style="float: right">
                <n-pagination :item-count="PageInfo.Total" :page="QueryForm.PageIndex" :page-size="QueryForm.PageSize"
                    :page-sizes="PageInfo.PageSizeOpts" show-size-picker @update:page-size="pageSizeChange"
                    @update:page="LoadTask">
                    <template #prefix>
                        <span class="mono">共 {{ PageInfo.Total }} 条</span>
                    </template>
                </n-pagination>
            </div>
        </div>
        <n-modal preset="card" v-model:show="EditTaskModal.Show" :title="EditTaskModal.Title"
            style="width: 1000px; max-width: calc(100vw - 48px)" :mask-closable="false">
            <n-form ref="editTaskForm" :model="EditTaskModal.Data" :rules="ruleValidate" label-placement="left"
                label-width="80">
                <n-grid :cols="2" :x-gap="12">
                    <n-gi>
                        <n-form-item label="任务名称" path="Name">
                            <n-input v-model:value="EditTaskModal.Data.Name" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="执行脚本" path="FileName">
                            <n-input-group class="script-group">
                                <n-input v-model:value="EditTaskModal.Data.FileName" readonly
                                    placeholder="点击右侧「上传脚本」选择 .cs 文件" />
                                <n-upload :show-file-list="false" :custom-request="uploadScript" accept=".cs"
                                    :action="BaseUrl + '/api/Upload/scripts?dir=' + FileNameDir"
                                    :headers="UploadHeaders">
                                    <n-button type="primary" secondary>
                                        <template #icon>
                                            <i class="fa-solid fa-cloud-arrow-up"></i>
                                        </template>
                                        上传脚本
                                    </n-button>
                                </n-upload>
                            </n-input-group>
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="触发指令">
                            <n-input-group>
                                <n-input v-model:value="EditTaskModal.Data.Command" placeholder="支持正则表达式" />
                                <n-checkbox v-model:checked="EditTaskModal.Data.EnableRegex"
                                    style="flex: 0 0 auto; white-space: nowrap; padding: 0 10px">正则</n-checkbox>
                            </n-input-group>
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="指令变量">
                            <n-input v-model:value="EditTaskModal.Data.CommandEnv"
                                placeholder="将用户发送的消息赋值到这个环境变量" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="定时执行" path="Cron">
                            <n-auto-complete v-model:value="EditTaskModal.Data.Cron" :options="CronPresets"
                                :get-show="(v) => !v" :render-label="renderCronLabel"
                                :theme-overrides="{ peers: { InternalSelectMenu: { borderRadius: '0', borderRadiusSmall: '0' } } }"
                                placeholder="留空聚焦点选常用表达式，或直接输入" clearable>
                                <template #suffix>
                                    <n-tooltip trigger="hover" placement="top">
                                        <template #trigger>
                                            <a href="http://cron.ciding.cc" target="_blank"
                                                style="color: var(--text-3); display: inline-flex">
                                                <i class="fa-solid fa-circle-exclamation"></i>
                                            </a>
                                        </template>
                                        更多表达式在线生成：cron.ciding.cc
                                    </n-tooltip>
                                </template>
                            </n-auto-complete>
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="会话名">
                            <n-input v-model:value="EditTaskModal.Data.SessionName"
                                placeholder="相同会话名的任务在会话页合并为一个会话，留空不归组" clearable />
                        </n-form-item>
                    </n-gi>
                    <n-gi :span="2">
                        <n-form-item label="备注">
                            <n-input type="textarea" v-model:value="EditTaskModal.Data.Remark" :rows="2" />
                        </n-form-item>
                    </n-gi>
                    <n-gi :span="2">
                        <n-form-item label="开关选项">
                            <n-space>
                                <n-checkbox v-model:checked="EditTaskModal.Data.Enable">
                                    启用
                                    <n-tooltip trigger="hover" placement="top">
                                        <template #trigger>
                                            <i class="fa-solid fa-circle-exclamation"
                                                style="color: crimson"></i>
                                        </template>
                                        全局开关
                                    </n-tooltip>
                                </n-checkbox>
                                <n-checkbox v-model:checked="EditTaskModal.Data.EnablePush">
                                    消息推送
                                    <n-tooltip trigger="hover" placement="top">
                                        <template #trigger>
                                            <i class="fa-solid fa-circle-exclamation"
                                                style="color: crimson"></i>
                                        </template>
                                        允许脚本发送通知消息
                                    </n-tooltip>
                                </n-checkbox>
                                <n-checkbox v-show="false" v-model:checked="EditTaskModal.Data.EnableLog">记录日志
                                </n-checkbox>
                            </n-space>
                        </n-form-item>
                    </n-gi>
                </n-grid>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="handleCancel()" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="handleSubmit('editTaskForm')">
                        提交
                    </n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>
<script>/*  eslint-disable  */
import { h } from 'vue'
import { renderOpActions, OpColor } from '@/utils/op-actions'
import {
  GetTask,
  PostTask,
  UpdateTask,
  DeleteTask,
  DisableTask,
  EnableTask,
  ExecTask,
  ExportTask
} from "@/api/task.js";
import config from "@/config";

export default {
  name: 'TaskIndex',
  data() {
    return {
      listSeq: 0,
      CheckedRowKeys: [],
      BaseUrl: "",
      TaskColumns: [{
        type: 'selection'
      },
      {
        title: "任务名称",
        key: "Name",
        width: 160,
        ellipsis: { tooltip: true },
        render: (row) => this.tdTip(h, { row }, 'Name')
      },
      {
        title: "触发指令",
        key: "Command",
        ellipsis: { tooltip: true },
        minWidth: 240,
        className: 'mono',
        render: (row) => this.tdTip(h, { row }, 'Command')
      },
      {
        title: "执行脚本",
        key: "FileName",
        minWidth: 280,
        ellipsis: { tooltip: true },
        className: 'mono',
        render: (row) => {
          return h("div", [
            h(
              "a",
              {
                style: { color: 'var(--accent)', cursor: 'pointer' },
                onClick: () => {
                  this.eidtScripts(row);
                }
              },
              row.FileName
            )
          ]);
        }
      },
      {
        title: "会话名",
        key: "SessionName",
        ellipsis: { tooltip: true },
        width: 140,
        render: (row) => row.SessionName || "-"
      },
      {
        title: "定时执行",
        key: "Cron",
        ellipsis: { tooltip: true },
        width: 120,
        className: 'mono'
      },
      {
        title: "状态",
        align: "center",
        key: "Enable",
        width: 80,
        render: (row) => {
          return h("span", { class: row.Enable ? "pill ok" : "pill danger" }, [
            h("span", { class: "dot" }),
            row.Enable ? "启用" : "禁用"
          ]);
        }
      },
      {
        title: "消息推送",
        key: "EnablePush",
        align: "center",
        width: 90,
        render: (row) => {
          return h(
            "span",
            {
              style: {
                color: row.EnablePush ? "var(--ok)" : "var(--danger)"
              }
            },
            row.EnablePush ? "是" : "否"
          );
        }
      },
      {
        title: "操作",
        key: "action",
        width: 150,
        align: "center",
        fixed: "right",
        render: (row, index) =>
          renderOpActions(h, [
            { icon: "fa-play", title: "执行", color: OpColor.Run, onClick: () => this.execTask([row.Id]) },
            { icon: "fa-pencil", title: "编辑", color: OpColor.Edit, onClick: () => this.editTask(index) },
            { icon: "fa-trash", title: "删除", color: OpColor.Delete, onClick: () => this.delete([row.Id]) }
          ])
      }],
      FileNameDir: "quantum",
      UploadHeaders: { Authorization: "" },
      QueryForm: {
        PageIndex: 1,
        PageSize: 15,
        Enable: undefined
      },
      PageInfo: {
        Total: 0,
        PageSizeOpts: [15, 50, 100]
      },
      CommadnData: [],
      // 定时执行内置常用表达式（Quartz 六段格式，后端 CronExpression.TryParse 校验）
      // value 必填（选中回填走 option.value 分支）；label 即回填进输入框的表达式本体，desc 仅供下拉展示
      CronPresets: [
        { label: "0/1 * * * * ?", value: "0/1 * * * * ?", desc: "每分钟" },
        { label: "0 0/5 * * * ?", value: "0 0/5 * * * ?", desc: "每5分钟" },
        { label: "0 0/15 * * * ?", value: "0 0/15 * * * ?", desc: "每15分钟" },
        { label: "0 0/30 * * * ?", value: "0 0/30 * * * ?", desc: "每30分钟" },
        { label: "0 0 * * * ?", value: "0 0 * * * ?", desc: "每小时" },
        { label: "0 0 8 * * ?", value: "0 0 8 * * ?", desc: "每天早上8点" },
        { label: "0 0 12 * * ?", value: "0 0 12 * * ?", desc: "每天中午12点" },
        { label: "0 0 20 * * ?", value: "0 0 20 * * ?", desc: "每天晚上20点" },
        { label: "0 0 9 ? * MON-FRI", value: "0 0 9 ? * MON-FRI", desc: "工作日早上9点" },
        { label: "0 0 0 1 * ?", value: "0 0 0 1 * ?", desc: "每月1号零点" }
      ],
      EditTaskModal: {
        Show: false,
        Title: "添加脚本指令",
        Data: {
          Enable: true,
          EnablePush: true,
          EnableLog: true,
          FileName: "",
          SessionName: ""
        }
      },
      ruleValidate: {
        Name: [
          {
            required: true,
            message: "任务不能为空",
            trigger: "blur"
          }
        ],
        FileName: [
          {
            required: true,
            message: "执行脚本不能为空",
            trigger: "blur"
          }
        ]
      }
    };
  },
  computed: {
    EnableOptions() {
      return [
        { label: "启用", value: "true" },
        { label: "禁用", value: "false" }
      ];
    }
  },
  methods: {
    onCheckedRowKeys(keys) {
      this.CheckedRowKeys = keys;
    },
    // 定时执行下拉项：左侧等宽表达式 + 右侧灰色说明
    renderCronLabel(option) {
      const desc = (option.rawNode && option.rawNode.desc) || option.desc || "";
      return h("div", { style: "display:flex;align-items:center;justify-content:space-between;gap:12px" }, [
        h("span", { class: "mono", style: "color: var(--text)" }, option.label),
        h("span", { style: "color: var(--text-3); font-size: 12px; white-space: nowrap" }, desc)
      ]);
    },
    // n-upload 自定义上传：导入任务
    importTask({ file, action, headers, onFinish, onError }) {
      var that = this;
      const xhr = new XMLHttpRequest();
      xhr.open("POST", action);
      if (headers) {
        Object.keys(headers).forEach((key) => {
          if (headers[key]) xhr.setRequestHeader(key, headers[key]);
        });
      }
      const formData = new FormData();
      formData.append("file", file.file);
      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          let res = {};
          try { res = JSON.parse(xhr.responseText); } catch (e) { }
          that.ImportTaskSuccess(res);
          onFinish({ file });
        } else {
          onError();
        }
      };
      xhr.onerror = () => onError();
      xhr.send(formData);
    },
    // n-upload 自定义上传：上传脚本
    uploadScript({ file, action, headers, onFinish, onError }) {
      var that = this;
      const xhr = new XMLHttpRequest();
      xhr.open("POST", action);
      if (headers) {
        Object.keys(headers).forEach((key) => {
          if (headers[key]) xhr.setRequestHeader(key, headers[key]);
        });
      }
      const formData = new FormData();
      formData.append("file", file.file);
      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          // 后端 BusinessException 走 HTTP 200 + {Code:500, Message}，需按业务码判失败（与 importTask 一致）
          let res = {};
          try { res = JSON.parse(xhr.responseText); } catch (e) { }
          if (res.Code === 200) {
            that.EditTaskModal.Data.FileName = `${file.name}`;
            onFinish({ file });
          } else {
            that.$message.error(res.Message || "脚本上传失败");
            onError();
          }
        } else {
          onError();
        }
      };
      xhr.onerror = () => onError();
      xhr.send(formData);
    },
    exportTask() {
      // 导出含全部指令与环境变量配置，必须鉴权：改为带 token 的 blob 下载（原 window.open 匿名直开已废弃）
      ExportTask({
        Key: this.QueryForm.Key || "",
        Enable: this.QueryForm.Enable != undefined ? this.QueryForm.Enable : ""
      }).then((response) => {
        const blob = new Blob([response.data], { type: "application/octet-stream" });
        const url = window.URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = "tasks.json";
        link.click();
        window.URL.revokeObjectURL(url);
      });
    },
    disableSelection() {
      const ids = [...this.CheckedRowKeys];
      if (ids.length === 0) {
        this.$message.warning("请勾选要禁用的脚本指令！");
        return;
      }
      DisableTask(ids).then(() => {
        this.LoadTask();
      });
    },
    enableSelection() {
      var ids = [...this.CheckedRowKeys];
      if (ids.length === 0) {
        this.$message.warning("请勾选要启用的脚本指令！");
        return;
      }
      EnableTask(ids).then(() => {
        this.LoadTask();
      });
    },
    //批量删除脚本指令
    deleteSelection() {
      var ids = [...this.CheckedRowKeys];
      if (ids.length === 0) {
        this.$message.warning("请勾选要删除的脚本指令！");
        return;
      }
      this.delete(ids);
    },
    resetQuery() {
      this.QueryForm.Key = "";
      this.QueryForm.Enable = undefined;
      this.LoadTask(1);
    },
    // 编辑脚本改为整页打开（sessionStorage 传参，与外触内执一致）
    eidtScripts(row) {
      sessionStorage.setItem('openScriptFile', row.FileName);
      this.$router.push({ path: '/task/task-scripts' });
    },
    execTask(ids) {
      var that = this;
      this.$dialog.warning({
        title: "执行确认",
        content: "确定执行选择的任务吗?",
        positiveText: "确认",
        negativeText: "取消",
        onPositiveClick: function () {
          ExecTask(ids).then((res) => {
            that.$message.success(`执行请求成功!`);
          });
        }
      });
    },
    handleSubmit(name) {
      this.$refs[name].validate((errors) => {
        if (!errors) {
          var data = JSON.parse(JSON.stringify(this.EditTaskModal.Data));
          // 有 Id 为编辑（PUT），无 Id 为新增（POST）
          const savePromise = data.Id ? UpdateTask(data) : PostTask(data);
          savePromise.then((res) => {
            if (res) {
              this.$message.success(this.EditTaskModal.Title + "成功！");
              this.EditTaskModal.Show = false;
              this.LoadTask();
            }
          });
        }
      });
    },
    handleCancel() {
      this.EditTaskModal.Show = false;
    },
    ImportTaskSuccess(res) {
      if (res.Code === 200) {
        this.$message.success(`成功导入任务${res.Data}个。`);
        this.LoadTask(1);
      } else {
        this.$message.error("导入任务失败：" + res.Message);
      }
    },
    editTask(i) {
      if (i > -1) {
        this.EditTaskModal.Title = "修改脚本指令";
        this.EditTaskModal.Data = JSON.parse(
          JSON.stringify(this.CommadnData[i])
        );
      } else {
        this.initEditTaskModal();
        this.EditTaskModal.Title = "添加脚本指令";
      }
      this.EditTaskModal.Show = true;
    },
    initEditTaskModal() {
      this.EditTaskModal.Data = {
        Enable: true,
        EnablePush: true,
        EnableLog: true,
        FileName: "",
        SessionName: ""
      };
    },
    // 删除数据
    delete(ids) {
      var c = this;
      this.$dialog.warning({
        title: "删除确认",
        content: `确定删除选择的[${ids.length}]个脚本指令吗？`,
        positiveText: "确认",
        negativeText: "取消",
        onPositiveClick: function () {
          DeleteTask(ids).then((res) => {
            c.LoadTask(1);
          });
        }
      });
    },
    pageSizeChange(size) {
      this.QueryForm.PageSize = size;
      this.LoadTask(1);
    },
    LoadTask(pageIndex) {
      this.QueryForm.PageIndex = pageIndex || 1;
      const seq = ++this.listSeq;
      GetTask(this.QueryForm).then((res) => {
        if (seq !== this.listSeq) return;
        this.CommadnData = res.Data;
        this.PageInfo.Total = res.TotalCount;
      }).catch((error) => {
        if (seq !== this.listSeq) return;
        this.$message.error(String((error && error.message) || error || '脚本指令加载失败'));
      });
    }
  },
  mounted() {
    this.UploadHeaders.Authorization = localStorage.getItem("accessToken");
    this.BaseUrl = config.baseUrl.dev;
    this.LoadTask(1);
  }
};
</script>
<style scoped>
/* n-upload 包装层会隔断 input-group 的圆角拼合，手动抹平按钮左侧圆角使其与输入框无缝成组 */
.script-group :deep(.n-upload .n-button) {
    border-top-left-radius: 0 !important;
    border-bottom-left-radius: 0 !important;
}
</style>
