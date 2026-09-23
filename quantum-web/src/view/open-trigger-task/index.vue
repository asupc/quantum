<template>
    <div>
        <div class="filter-card">
            <n-form :show-feedback="false" label-placement="left" label-width="auto" @keyup.enter="LoadTask(1)">
                <n-grid cols="2 s:3 m:3 l:4 xl:4" responsive="screen" :x-gap="12" :y-gap="12">
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
            </n-space>
        </div>
        <n-data-table ref="EnvTable" :columns="TaskColumns" :data="CommadnData" :row-key="(row) => row.Id"
            :checked-row-keys="CheckedRowKeys" @update:checked-row-keys="onCheckedRowKeys" :scroll-x="1300"
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
        <n-modal preset="card" v-model:show="EditTaskModal.Show" :title="EditTaskModal.Title" style="width: 800px"
            :mask-closable="false">
            <n-form ref="editTaskForm" :model="EditTaskModal.Data" :rules="ruleValidate" label-placement="left"
                label-width="80">
                <n-form-item label="名称" path="Name">
                    <n-input v-model:value="EditTaskModal.Data.Name" />
                </n-form-item>
                <n-form-item label="密钥" path="Secret">
                    <n-input :maxlength="30" v-model:value="EditTaskModal.Data.Secret"
                        placeholder="验证和匹配触发执行脚本的标识，不能重复，建议复杂度高一点，OK？" />
                </n-form-item>
                <n-form-item label="触发方式" path="HttpMethod">
                    <n-select clearable v-model:value="EditTaskModal.Data.HttpMethod" :options="HttpMethodOptions" />
                </n-form-item>
                <n-form-item label="触发地址">
                    <n-input readonly :value="getTriggerUrl" />
                </n-form-item>
                <n-form-item label="执行脚本" path="SrciptFile">
                    <n-input-group>
                        <n-input v-model:value="EditTaskModal.Data.SrciptFile" />
                        <n-upload :show-file-list="false" :custom-request="uploadScript" accept=".cs"
                            :action="BaseUrl + '/api/Upload/scripts?dir=' + FileNameDir + '/' + ScriptSubDir"
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
                <n-form-item label="白名单IP">
                    <n-input type="textarea" v-model:value="EditTaskModal.Data.Whitelist" :rows="2"
                        placeholder="验证来源IP，不设置则验证，多个用逗号隔开或换行" />
                </n-form-item>
                <n-form-item label="备注">
                    <n-input type="textarea" v-model:value="EditTaskModal.Data.Remark" :rows="2" />
                </n-form-item>
                <n-form-item label="开关选项">
                    <n-space>
                        <n-checkbox v-model:checked="EditTaskModal.Data.Enable">
                            启用
                            <n-tooltip trigger="hover" placement="top">
                                <template #trigger>
                                    <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                </template>
                                全局开关，是否启用
                            </n-tooltip>
                        </n-checkbox>
                        <n-checkbox v-model:checked="EditTaskModal.Data.EnablePush">
                            消息推送
                            <n-tooltip trigger="hover" placement="top">
                                <template #trigger>
                                    <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                </template>
                                允许脚本发送通知消息
                            </n-tooltip>
                        </n-checkbox>
                        <n-checkbox v-model:checked="EditTaskModal.Data.EnableProxy">
                            代理
                            <n-tooltip trigger="hover" placement="top" style="max-width: 400px">
                                <template #trigger>
                                    <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                </template>
                                仅支持 .cs 源码任务：实现 Quantum.Plugins.IQuantumTask；平台数据走 ctx.Env/ctx.Notify 门面，外部请求走 ctx.Http
                            </n-tooltip>
                        </n-checkbox>
                    </n-space>
                </n-form-item>
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
<script>  /*  eslint-disable  */
  import { h } from 'vue'
  import { renderOpActions, OpColor } from '@/utils/op-actions'
  import {
    GetTask,
    PostTask,
    DeleteTask,
    DisableTask,
    EnableTask
  } from "@/api/open-trigger-task.js";
  import config from "@/config";
  import { AllEnums } from "@/api/enum.js";

  export default {
    name: 'OpenTriggerTaskIndex',
    data() {
      return {
        HttpMethods: [{
          Key: "GET",
          Value: "GET"
        }, {
          Key: "POST",
          Value: "POST"
        }],
        ScriptSubDir: "open-trigger-task",
        BaseUrl: "",
        CheckedRowKeys: [],
        TaskColumns: [{
          type: 'selection'
        },
        {
          title: "名称",
          key: "Name",
          width: 160,
          ellipsis: { tooltip: true },
          render: (row) => this.tdTip(h, { row }, 'Name')
        },
        {
          title: "密钥",
          key: "Secret",
          ellipsis: { tooltip: true },
          minWidth: 240,
          className: 'mono',
          render: (row) => this.tdTip(h, { row }, 'Secret')
        },
        {
          title: "请求方法",
          key: "HttpMethod",
          ellipsis: { tooltip: true },
          width: 90
        },
        {
          title: "执行脚本",
          key: "SrciptFile",
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
                row.SrciptFile
              )
            ]);
          }
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
          title: "代理",
          key: "EnableProxy",
          width: 80,
          align: "center",
          render: (row) => {
            return h(
              "span",
              {
                style: {
                  color: row.EnableProxy ? "var(--ok)" : "var(--danger)"
                }
              },
              row.EnableProxy ? "启用" : "禁用"
            );
          }
        },
        {
          title: "操作",
          key: "action",
          width: 80,
          align: "center",
          fixed: "right",
          render: (row, index) =>
            renderOpActions(h, [
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
        AllEnums: {},
        CommadnData: [],
        EditTaskModal: {
          Show: false,
          Title: "添加外触内执",
          Data: {
            Enable: true,
            EnablePush: false,
            EnableProxy: false,
            SrciptFile: ""
          }
        },
        ruleValidate: {
          Name: [
            {
              required: true,
              message: "名称不能为空",
              trigger: "blur"
            }
          ],
          HttpMethod: [
            {
              required: true,
              message: "触发方式不能为空",
              trigger: "blur"
            }
          ],
          Secret: [
            {
              required: true,
              message: "密钥不能为空",
              trigger: "blur"
            },
            {
              min: 12,
              message: "密钥长度不能少于12个字符",
              trigger: "blur"
            },
            {
              max: 30,
              message: "密钥长度不能超过30个字符",
              trigger: "blur"
            }
          ],
          SrciptFile: [
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
      HttpMethodOptions() {
        return this.HttpMethods.map((t) => ({ label: t.Key, value: t.Value }));
      },
      EnableOptions() {
        return [
          { label: "启用", value: "true" },
          { label: "禁用", value: "false" }
        ];
      },
      getTriggerUrl() {
        return window.location.origin + '/API/OpenTriggerTask/' + (this.EditTaskModal.Data.Secret || "")
      }
    },
    methods: {
      onCheckedRowKeys(keys) {
        this.CheckedRowKeys = keys;
      },
      // n-upload 自定义上传：走原接口，成功后回填脚本路径
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
            // 后端 BusinessException 走 HTTP 200 + {Code:500, Message}，需按业务码判失败
            let res = {};
            try { res = JSON.parse(xhr.responseText); } catch (e) { }
            if (res.Code === 200) {
              that.EditTaskModal.Data.SrciptFile = `${that.ScriptSubDir}/${file.name}`;
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
      disableSelection() {
        const ids = [...this.CheckedRowKeys];
        if (ids.length === 0) {
          this.$message.warning("请勾选要禁用的外触内执！");
          return;
        }
        DisableTask(ids).then(() => {
          this.LoadTask();
        });
      },
      enableSelection() {
        var ids = [...this.CheckedRowKeys];
        if (ids.length === 0) {
          this.$message.warning("请勾选要启用的外触内执！");
          return;
        }
        EnableTask(ids).then(() => {
          this.LoadTask();
        });
      },
      //批量删除外触内执
      deleteSelection() {
        var ids = [...this.CheckedRowKeys];
        if (ids.length === 0) {
          this.$message.warning("请勾选要删除的外触内执！");
          return;
        }
        this.delete(ids);
      },
      // 编辑脚本改为整页打开（弹窗内嵌 monaco 存在稳定性问题，且与原全屏弹窗体验一致）；
      // 文件名经 sessionStorage 传递（query 传参会让 keep-alive 缓存编辑器实例，稳定性差）
      eidtScripts(row) {
        sessionStorage.setItem('openScriptFile', row.SrciptFile);
        this.$router.push({ path: '/task/task-scripts' });
      },
      handleSubmit(name) {
        this.$refs[name].validate((errors) => {
          if (!errors) {
            var data = JSON.parse(JSON.stringify(this.EditTaskModal.Data));
            if (data.CommunicationTypeList && data.CommunicationTypeList.length > 0) {
              data.CommunicationTypes = data.CommunicationTypeList.join(",")
            }
            PostTask(data).then((res) => {
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
      editTask(i) {
        if (i > -1) {
          this.EditTaskModal.Title = "修改外触内执";
          this.EditTaskModal.Data = JSON.parse(
            JSON.stringify(this.CommadnData[i])
          );
          if (this.CommadnData[i].CommunicationTypes) {
            this.EditTaskModal.Data.CommunicationTypeList = [];
            for (let index = 0; index < this.CommadnData[i].CommunicationTypes.split(",").length; index++) {
              const element = this.CommadnData[i].CommunicationTypes.split(",")[index];
              this.EditTaskModal.Data.CommunicationTypeList.push(parseInt(element));
            }
          }
        } else {
          this.initEditTaskModal();
          this.EditTaskModal.Title = "添加外触内执";
        }
        this.EditTaskModal.Show = true;
      },
      initEditTaskModal() {
        this.EditTaskModal.Data = {
          Enable: true,
          EnablePush: false,
          EnableProxy: false,
          SrciptFile: ""
        };
      },
      // 删除数据
      delete(ids) {
        var c = this;
        this.$dialog.warning({
          title: "删除确认",
          content: `确定删除选择的[${ids.length}]个外触内执吗？`,
          positiveText: "确认",
          negativeText: "取消",
          onPositiveClick: function () {
            DeleteTask(ids).then((res) => {
              c.LoadTask(1);
            });
          }
        });
      },
      loadAllEnums() {
        AllEnums().then((res) => {
          this.AllEnums = res;
        });
      },
      pageSizeChange(size) {
        this.QueryForm.PageSize = size;
        this.LoadTask(1);
      },
      LoadTask(pageIndex) {
        this.QueryForm.PageIndex = pageIndex || 1;
        GetTask(this.QueryForm).then((res) => {
          this.CommadnData = res.Data;
          this.PageInfo.Total = res.TotalCount;
        });
      },
      resetQuery() {
        this.QueryForm.Key = "";
        this.QueryForm.Enable = undefined;
        this.LoadTask(1);
      }
    },
    mounted() {
      this.UploadHeaders.Authorization = localStorage.getItem("accessToken");
      this.BaseUrl = config.baseUrl.dev;
      this.LoadTask(1);
      this.loadAllEnums();
    }
  };
</script>
