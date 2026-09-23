<template>
    <div>
        <!-- 供应商与模型管理（全局设置/AI 写权限已拆到「AI设置」页） -->
        <div class="filter-card">
            <div class="card-title">
                <i class="fa-solid fa-microchip"></i> AI 供应商
                <span class="card-hint">OpenAI 兼容 / Anthropic / Responses 三协议；未配置可用供应商与模型时，AI 助手页显示引导态、功能不激活</span>
            </div>
            <n-space>
                <n-button type="primary" @click="editProvider(-1)">
                    <template #icon>
                        <i class="fa-solid fa-plus"></i>
                    </template>
                    添加供应商
                </n-button>
                <n-button type="error" @click="deleteSelection">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    删除选中
                </n-button>
                <span style="color: var(--text-3)">
                    密钥只回掩码；「测试」会真实调用一次模型（拉模型清单 → 最小对话 → 工具能力探测）。
                </span>
            </n-space>
            <n-data-table style="margin-top: 12px" :columns="ProviderColumns" :data="Providers" :row-key="(row) => row.Id"
                :checked-row-keys="CheckedRowKeys" @update:checked-row-keys="onCheckedRowKeys" :bordered="true"
                size="small" :scroll-x="1200" />
        </div>


        <!-- 供应商编辑 -->
        <n-modal preset="card" v-model:show="EditModal.Show" :title="EditModal.Title" style="width: 640px"
            :mask-closable="false">
            <n-form ref="ProviderForm" :model="EditModal.Data" :rules="ProviderRules" label-placement="left"
                label-width="110">
                <n-form-item label="名称" path="Name">
                    <n-input v-model:value="EditModal.Data.Name" placeholder="如 DeepSeek" />
                </n-form-item>
                <n-form-item label="BaseUrl" path="BaseUrl">
                    <n-input v-model:value="EditModal.Data.BaseUrl" placeholder="https://api.deepseek.com（裸域名自动补 /v1）" />
                </n-form-item>
                <n-form-item label="协议" path="Protocol">
                    <n-select v-model:value="EditModal.Data.Protocol" :options="ProtocolOptions" />
                    <span class="card-hint">{{ ProtocolHint }}</span>
                </n-form-item>
                <n-form-item label="API 密钥" path="ApiKey">
                    <n-input v-model:value="EditModal.Data.ApiKey" type="password" show-password-on="click"
                        :placeholder="EditModal.Data.Id ? `留空表示不改（当前 ${EditModal.Masked}）` : 'sk-...'" />
                </n-form-item>
                <n-form-item label="工具能力">
                    <n-select v-model:value="EditModal.Data.SupportsTools" :options="ToolSupportOptions" />
                </n-form-item>
                <n-form-item label="超时/重试">
                    <n-space>
                        <n-input-number v-model:value="EditModal.Data.TimeoutSeconds" :min="10" :max="600"
                            style="width: 140px">
                            <template #suffix>秒</template>
                        </n-input-number>
                        <n-input-number v-model:value="EditModal.Data.MaxRetries" :min="0" :max="5" style="width: 120px">
                            <template #prefix>重试</template>
                        </n-input-number>
                    </n-space>
                </n-form-item>
                <n-form-item label="选项">
                    <n-space>
                        <n-checkbox v-model:checked="EditModal.Data.UsePlatformProxy">走平台代理</n-checkbox>
                        <n-checkbox v-model:checked="EditModal.Data.Enable">启用</n-checkbox>
                        <span class="card-hint">排序</span>
                        <n-input-number v-model:value="EditModal.Data.Sort" :min="0" style="width: 100px" />
                    </n-space>
                </n-form-item>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="EditModal.Show = false" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="submitProvider">提交</n-button>
                </div>
            </template>
        </n-modal>

        <!-- 模型管理 -->
        <n-modal preset="card" v-model:show="ModelsModal.Show" :title="ModelsModal.Title" style="width: 1000px"
            :mask-closable="false">
            <n-space style="margin-bottom: 12px">
                <n-button size="small" type="primary" @click="openModelEdit(-1)">
                    <template #icon>
                        <i class="fa-solid fa-plus"></i>
                    </template>
                    添加模型
                </n-button>
                <n-button size="small" @click="syncModels">
                    <template #icon>
                        <i class="fa-solid fa-cloud-arrow-down"></i>
                    </template>
                    拉取模型
                </n-button>
                <n-button size="small" type="error" @click="deleteModelSelection">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    删除选中
                </n-button>
                <span class="card-hint">上下文大小用于上下文预算裁剪；「默认」模型是 Agent 未指定时的选用模型</span>
            </n-space>

            <div v-if="SyncResult.length" class="sync-panel">
                <div class="card-title">供应商返回 {{ SyncResult.length }} 个模型（勾选后导入，已存在的会跳过）</div>
                <n-checkbox-group v-model:value="SyncChecked">
                    <n-space>
                        <n-checkbox v-for="item in SyncResult" :key="item" :value="item" :label="item" />
                    </n-space>
                </n-checkbox-group>
                <div style="margin-top: 8px">
                    <n-button size="small" type="primary" @click="importModels">导入勾选</n-button>
                    <n-button size="small" style="margin-left: 8px" @click="SyncResult = []">收起</n-button>
                </div>
            </div>

            <n-data-table :columns="ModelColumns" :data="ModelsModal.Models" :row-key="(row) => row.Id"
                :checked-row-keys="ModelCheckedKeys" @update:checked-row-keys="onModelCheckedRowKeys" :bordered="true"
                size="small" :scroll-x="900" />
        </n-modal>

        <!-- 模型编辑 -->
        <n-modal preset="card" v-model:show="ModelEdit.Show" :title="ModelEdit.Title" style="width: 560px"
            :mask-closable="false">
            <n-form ref="ModelForm" :model="ModelEdit.Data" :rules="ModelRules" label-placement="left"
                label-width="110">
                <n-form-item label="模型标识" path="ModelId">
                    <n-input v-model:value="ModelEdit.Data.ModelId" placeholder="如 deepseek-chat" />
                </n-form-item>
                <n-form-item label="显示名">
                    <n-input v-model:value="ModelEdit.Data.DisplayName" placeholder="留空则用模型标识" />
                </n-form-item>
                <n-form-item label="上下文大小" path="ContextWindow">
                    <n-input-number v-model:value="ModelEdit.Data.ContextWindow" :min="1024" :max="2000000"
                        style="width: 200px">
                        <template #suffix>token</template>
                    </n-input-number>
                </n-form-item>
                <n-form-item label="输出上限">
                    <n-input-number v-model:value="ModelEdit.Data.MaxOutputTokens" :min="64" :max="200000"
                        style="width: 200px">
                        <template #suffix>token</template>
                    </n-input-number>
                </n-form-item>
                <n-form-item label="温度">
                    <n-input-number v-model:value="ModelEdit.Data.Temperature" :min="0" :max="2" :step="0.1"
                        style="width: 200px" />
                </n-form-item>
                <n-form-item label="选项">
                    <n-space>
                        <n-checkbox v-model:checked="ModelEdit.Data.Enable">启用</n-checkbox>
                        <n-checkbox v-model:checked="ModelEdit.Data.IsDefault">设为默认</n-checkbox>
                        <span class="card-hint">排序</span>
                        <n-input-number v-model:value="ModelEdit.Data.Sort" :min="0" style="width: 100px" />
                    </n-space>
                </n-form-item>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="ModelEdit.Show = false" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="submitModel">提交</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>
<script>
  import { h } from 'vue'
  import { renderOpActions, OpColor } from '@/utils/op-actions'
  import {
    GetAiProviders,
    SaveAiProvider,
    DeleteAiProviders,
    RevealAiKey,
    TestAiProvider,
    SyncAiModels,
    ImportAiModels,
    SaveAiModel,
    SetDefaultAiModel,
    DeleteAiModels
  } from '@/api/ai'

  const TOOL_SUPPORT_LABELS = {
    0: { text: '未探测', cls: 'off' },
    1: { text: '支持工具', cls: 'ok' },
    2: { text: '不支持工具', cls: 'warn' }
  }

  export default {
    name: 'AiProviders',
    data() {
      return {
        Providers: [],
        CheckedRowKeys: [],
        EditingProvider: null,
        EditModal: { Show: false, Title: '添加供应商', Data: {}, Masked: '' },
        ModelsModal: { Show: false, Title: '', Provider: null, Models: [] },
        SyncResult: [],
        SyncChecked: [],
        ModelCheckedKeys: [],
        ModelEdit: { Show: false, Title: '添加模型', Data: {} },
        ProviderRules: {
          Name: [{ required: true, message: '供应商名称不能为空', trigger: 'blur' }],
          BaseUrl: [{ required: true, message: 'BaseUrl 不能为空', trigger: 'blur' }]
        },
        ModelRules: {
          ModelId: [{ required: true, message: '模型标识不能为空', trigger: 'blur' }],
          ContextWindow: [{ required: true, type: 'number', message: '请填写上下文大小', trigger: 'blur' }]
        },
        ProviderColumns: [
          { type: 'selection' },
          {
            title: '名称',
            key: 'Name',
            width: 150,
            render: (row) => this.tdTip(h, { row }, 'Name')
          },
          {
            title: 'BaseUrl',
            key: 'BaseUrl',
            minWidth: 240,
            className: 'mono',
            ellipsis: { tooltip: true }
          },
          {
            title: '协议',
            key: 'Protocol',
            width: 110,
            align: 'center',
            render: (row) => {
              const label = { openai: 'OpenAI', anthropic: 'Anthropic', responses: 'Responses' }[row.Protocol || 'openai']
              return h('span', { class: 'pill ' + ((row.Protocol || 'openai') === 'openai' ? 'off' : 'warn') },
                [h('span', { class: 'dot' }), label])
            }
          },
          {
            title: '密钥',
            key: 'ApiKeyMasked',
            width: 170,
            className: 'mono',
            render: (row) => {
              if (!row.HasApiKey) {
                return h('span', { class: 'pill warn' }, [h('span', { class: 'dot' }), '未配置'])
              }
              return h('div', { class: 'op-actions' }, [
                h('span', { class: 'mono' }, row.ApiKeyMasked),
                h('i', {
                  class: 'op-icon fa-solid fa-eye',
                  title: '显示明文',
                  style: { color: OpColor.Info },
                  onClick: () => this.revealKey(row)
                })
              ])
            }
          },
          {
            title: '工具能力',
            key: 'SupportsTools',
            width: 110,
            align: 'center',
            render: (row) => {
              const label = TOOL_SUPPORT_LABELS[row.SupportsTools] || TOOL_SUPPORT_LABELS[0]
              return h('span', { class: `pill ${label.cls}` }, [h('span', { class: 'dot' }), label.text])
            }
          },
          {
            title: '模型',
            key: 'Models',
            width: 70,
            align: 'center',
            className: 'mono',
            render: (row) => (row.Models || []).length
          },
          {
            title: '最近测试',
            key: 'LastTestTime',
            width: 230,
            render: (row) => {
              if (!row.LastTestTime) {
                return '-'
              }
              return h('span', {
                style: { color: row.LastTestOk ? 'var(--ok)' : 'var(--danger)' },
                title: row.LastTestMessage || ''
              }, `${row.LastTestOk ? '通过' : '失败'} · ${this.formatTime(row.LastTestTime)}`)
            }
          },
          {
            title: '状态',
            key: 'Enable',
            width: 80,
            align: 'center',
            render: (row) => h('span', { class: row.Enable ? 'pill ok' : 'pill danger' }, [
              h('span', { class: 'dot' }),
              row.Enable ? '启用' : '禁用'
            ])
          },
          {
            title: '操作',
            key: 'action',
            width: 150,
            align: 'center',
            fixed: 'right',
            render: (row) =>
              renderOpActions(h, [
                { icon: 'fa-plug-circle-bolt', title: '测试', color: OpColor.Run, onClick: () => this.testProvider(row) },
                { icon: 'fa-pencil', title: '编辑', color: OpColor.Edit, onClick: () => this.editProvider(row) },
                { icon: 'fa-layer-group', title: '模型', color: OpColor.Info, onClick: () => this.openModels(row) },
                { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.deleteProviders([row.Id]) }
              ])
          }
        ],
        ModelColumns: [
          { type: 'selection' },
          { title: '模型标识', key: 'ModelId', minWidth: 200, className: 'mono', ellipsis: { tooltip: true } },
          { title: '显示名', key: 'DisplayName', width: 160, ellipsis: { tooltip: true } },
          {
            title: '上下文',
            key: 'ContextWindow',
            width: 110,
            align: 'right',
            className: 'mono',
            render: (row) => this.formatTokens(row.ContextWindow)
          },
          {
            title: '输出上限',
            key: 'MaxOutputTokens',
            width: 100,
            align: 'right',
            className: 'mono',
            render: (row) => this.formatTokens(row.MaxOutputTokens)
          },
          { title: '温度', key: 'Temperature', width: 70, align: 'right', className: 'mono' },
          {
            title: '默认',
            key: 'IsDefault',
            width: 70,
            align: 'center',
            render: (row) => (row.IsDefault ? h('i', { class: 'fa-solid fa-star', style: 'color: var(--warn)' }) : '')
          },
          {
            title: '状态',
            key: 'Enable',
            width: 80,
            align: 'center',
            render: (row) => h('span', { class: row.Enable ? 'pill ok' : 'pill danger' }, [
              h('span', { class: 'dot' }),
              row.Enable ? '启用' : '禁用'
            ])
          },
          {
            title: '操作',
            key: 'action',
            width: 110,
            align: 'center',
            fixed: 'right',
            render: (row) =>
              renderOpActions(h, [
                {
                  icon: 'fa-star',
                  title: '设为默认',
                  color: OpColor.Run,
                  onClick: () => this.setDefault(row),
                },
                { icon: 'fa-pencil', title: '编辑', color: OpColor.Edit, onClick: () => this.openModelEdit(row) },
                { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.deleteModels([row.Id]) }
              ])
          }
        ]
      }
    },
    computed: {
      ProtocolOptions() {
        return [
          { label: 'OpenAI 兼容（chat/completions）', value: 'openai' },
          { label: 'Anthropic（Messages API）', value: 'anthropic' },
          { label: 'OpenAI Responses（/responses）', value: 'responses' }
        ]
      },
      ProtocolHint() {
        const map = {
          openai: 'POST {BaseUrl}/chat/completions（DeepSeek/通义/智谱/Kimi/OpenRouter/Ollama 与绝大多数中转）',
          anthropic: 'POST {BaseUrl}/messages，鉴权用 x-api-key + anthropic-version（Claude 官方与支持 Anthropic 的网关）',
          responses: 'POST {BaseUrl}/responses，input/output 项式（OpenAI 官方与支持的网关）'
        }
        return map[this.EditModal.Data.Protocol] || map.openai
      },
      ToolSupportOptions() {
        return [
          { label: '未探测（自动降级）', value: 0 },
          { label: '支持工具调用', value: 1 },
          { label: '不支持（走 JSON 协议）', value: 2 }
        ]
      }
    },
    methods: {
      formatTime(value) {
        return value ? String(value).replace('T', ' ').slice(0, 19) : ''
      },
      formatTokens(value) {
        const size = Number(value || 0)
        return size >= 1000 ? (size / 1000).toFixed(size % 1000 === 0 ? 0 : 1) + 'K' : String(size)
      },
      onCheckedRowKeys(keys) {
        this.CheckedRowKeys = keys
      },
      onModelCheckedRowKeys(keys) {
        this.ModelCheckedKeys = keys
      },
      loadProviders() {
        GetAiProviders().then((res) => {
          this.Providers = res || []
          if (this.ModelsModal.Show && this.ModelsModal.Provider) {
            const current = this.Providers.find((n) => n.Id === this.ModelsModal.Provider.Id)
            if (current) {
              this.ModelsModal.Provider = current
              this.ModelsModal.Models = current.Models || []
            }
          }
        })
      },
      revealKey(row) {
        RevealAiKey(row.Id).then((key) => {
          this.$dialog.info({
            title: `密钥（${row.Name}）`,
            content: key,
            positiveText: '知道了'
          })
        })
      },
      testProvider(row) {
        const c = this
        this.$message.loading('正在测试连通性（会真实调用一次模型）…', { duration: 1500 })
        TestAiProvider(row.Id).then((res) => {
          c.loadProviders()
          const support = (TOOL_SUPPORT_LABELS[res.SupportsTools] || TOOL_SUPPORT_LABELS[0]).text
          c.$dialog[res.Ok ? 'success' : 'error']({
            title: res.Ok ? '测试通过' : '测试失败',
            content: `${res.Message}\n工具能力：${support}（耗时 ${res.LatencyMs} ms）`,
            positiveText: '知道了'
          })
        }).catch((error) => {
          c.$message.error(String((error && error.message) || error || '测试失败'))
        })
      },
      // 行内「编辑」传的是整行对象，工具栏「添加」传的是 -1：两种入参都要认
      //（只认索引时对象 > -1 恒为 false，会静默走到「添加」分支导致表单空白）
      editProvider(target) {
        const row = typeof target === 'object' && target !== null ? target : this.Providers[target]
        if (row) {
          this.EditModal.Title = `修改供应商（${row.Name}）`
          this.EditModal.Masked = row.ApiKeyMasked
          this.EditModal.Data = {
            Id: row.Id,
            Name: row.Name,
            BaseUrl: row.BaseUrl,
            Protocol: row.Protocol || 'openai',
            ApiKey: '',
            UsePlatformProxy: row.UsePlatformProxy,
            TimeoutSeconds: row.TimeoutSeconds,
            MaxRetries: row.MaxRetries,
            Enable: row.Enable,
            Sort: row.Sort,
            SupportsTools: row.SupportsTools
          }
        } else {
          this.EditModal.Title = '添加供应商'
          this.EditModal.Masked = ''
          this.EditModal.Data = {
            Name: '',
            BaseUrl: '',
            Protocol: 'openai',
            ApiKey: '',
            UsePlatformProxy: false,
            TimeoutSeconds: 120,
            MaxRetries: 1,
            Enable: true,
            Sort: 0,
            SupportsTools: 0
          }
        }
        this.EditModal.Show = true
      },
      submitProvider() {
        this.$refs.ProviderForm.validate((errors) => {
          if (errors) {
            return
          }
          SaveAiProvider(JSON.parse(JSON.stringify(this.EditModal.Data))).then(() => {
            this.$message.success('供应商已保存')
            this.EditModal.Show = false
            this.loadProviders()
          })
        })
      },
      deleteSelection() {
        if (!this.CheckedRowKeys.length) {
          this.$message.warning('请选中供应商后再操作！')
          return
        }
        this.deleteProviders(this.CheckedRowKeys)
      },
      deleteProviders(ids) {
        const c = this
        this.$dialog.warning({
          title: '删除供应商',
          content: '确定删除选中的供应商？其下模型一并删除。',
          positiveText: '确认删除',
          negativeText: '取消',
          onPositiveClick: function () {
            DeleteAiProviders(ids).then(() => {
              c.CheckedRowKeys = []
              c.loadProviders()
            })
          }
        })
      },
      openModels(row) {
        this.ModelsModal.Provider = row
        this.ModelsModal.Models = row.Models || []
        this.ModelsModal.Title = `模型管理（${row.Name}）`
        this.SyncResult = []
        this.SyncChecked = []
        this.ModelCheckedKeys = []
        this.ModelsModal.Show = true
      },
      syncModels() {
        const provider = this.ModelsModal.Provider
        if (!provider) {
          return
        }
        SyncAiModels(provider.Id).then((list) => {
          this.SyncResult = list || []
          this.SyncChecked = []
          if (!this.SyncResult.length) {
            this.$message.warning('该供应商未返回模型清单（可手动添加模型）')
          }
        }).catch((error) => {
          this.$message.error(String((error && error.message) || error || '拉取模型失败'))
        })
      },
      importModels() {
        const provider = this.ModelsModal.Provider
        if (!provider || !this.SyncChecked.length) {
          this.$message.warning('请先勾选要导入的模型')
          return
        }
        ImportAiModels({ ProviderId: provider.Id, ModelIds: this.SyncChecked }).then((count) => {
          this.$message.success(`已导入 ${count} 个模型（已存在的跳过）`)
          this.SyncResult = []
          this.SyncChecked = []
          this.loadProviders()
        })
      },
      openModelEdit(index) {
        const provider = this.ModelsModal.Provider
        if (!provider) {
          return
        }
        if (typeof index === 'object' && index !== null) {
          this.ModelEdit.Title = `修改模型（${index.ModelId}）`
          this.ModelEdit.Data = JSON.parse(JSON.stringify(index))
        } else {
          this.ModelEdit.Title = '添加模型'
          this.ModelEdit.Data = {
            ProviderId: provider.Id,
            ModelId: '',
            DisplayName: '',
            ContextWindow: 32768,
            MaxOutputTokens: 4096,
            Temperature: 0.2,
            Enable: true,
            IsDefault: !(provider.Models || []).length,
            Sort: 0
          }
        }
        this.ModelEdit.Show = true
      },
      submitModel() {
        this.$refs.ModelForm.validate((errors) => {
          if (errors) {
            return
          }
          SaveAiModel(JSON.parse(JSON.stringify(this.ModelEdit.Data))).then(() => {
            this.$message.success('模型已保存')
            this.ModelEdit.Show = false
            this.loadProviders()
          })
        })
      },
      setDefault(row) {
        SetDefaultAiModel(row.Id).then(() => {
          this.$message.success(`已将 ${row.ModelId} 设为默认模型`)
          this.loadProviders()
        })
      },
      deleteModelSelection() {
        if (!this.ModelCheckedKeys.length) {
          this.$message.warning('请选中模型后再操作！')
          return
        }
        this.deleteModels(this.ModelCheckedKeys)
      },
      deleteModels(ids) {
        const c = this
        this.$dialog.warning({
          title: '删除模型',
          content: '确定删除选中的模型？默认模型被删时会自动指定一个可用模型。',
          positiveText: '确认删除',
          negativeText: '取消',
          onPositiveClick: function () {
            DeleteAiModels(ids).then(() => {
              c.ModelCheckedKeys = []
              c.loadProviders()
            })
          }
        })
      }
    },
    mounted() {
      this.loadProviders()
    },
    activated() {
      this.loadProviders()
    }
  }
</script>
<style scoped>
.card-title {
    margin-bottom: 12px;
    color: var(--text);
    font-weight: 500;
}

.card-title i {
    color: var(--accent);
    margin-right: 6px;
}

.card-hint {
    margin-left: 10px;
    color: var(--text-3);
    font-size: 12px;
}

:deep(.pill) {
    white-space: nowrap;
}

.sync-panel {
    margin-bottom: 12px;
    padding: 12px;
    background: var(--card-2);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    max-height: 240px;
    overflow: auto;
}
</style>
