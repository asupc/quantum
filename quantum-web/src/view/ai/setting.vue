<template>
    <div>
        <!-- 全局 AI 设置（自 providers.vue 拆出：本页只管设置，供应商/模型在「AI供应商」页） -->
        <div class="filter-card">
            <div class="card-title">
                <i class="fa-solid fa-sliders"></i> 全局设置
                <span class="card-hint">未配置可用供应商与模型时，AI 助手页显示引导态、功能不激活</span>
            </div>
            <n-form :show-feedback="false" label-placement="left" label-width="auto">
                <n-grid cols="1 s:2 m:3 l:4" responsive="screen" :x-gap="12" :y-gap="12">
                    <n-gi>
                        <n-form-item label="启用 AI">
                            <n-switch v-model:value="Setting.Enable" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="最大轮数">
                            <n-input-number v-model:value="Setting.MaxRounds" :min="1" :max="100" style="width: 100%" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="单次时限(秒)">
                            <n-input-number v-model:value="Setting.RunTimeoutSeconds" :min="60" :max="3000"
                                style="width: 100%" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="日志尾部行数">
                            <n-input-number v-model:value="Setting.LogTailLines" :min="20" :max="2000"
                                style="width: 100%" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="失败自动分析">
                            <n-switch v-model:value="Setting.AutoAnalyzeOnFailure" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="试运行通知">
                            <n-select v-model:value="Setting.TestRunNotifyMode" :options="NotifyModeOptions" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="试运行需确认">
                            <n-switch v-model:value="Setting.TestRunRequireConfirm" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <n-form-item label="版本保留数">
                            <n-input-number v-model:value="Setting.KeepVersionsPerFile" :min="1" :max="500"
                                style="width: 100%" />
                        </n-form-item>
                    </n-gi>
                    <n-gi :span="2">
                        <n-form-item label="保存完整提示词">
                            <n-switch v-model:value="Setting.SaveFullPrompt" />
                            <span class="card-hint">打开后每次运行的请求/响应对落盘 logs/ai（排查用）</span>
                        </n-form-item>
                    </n-gi>
                    <n-gi :span="2">
                        <n-form-item label="追加提示">
                            <n-input v-model:value="Setting.SystemPromptExtra" placeholder="如：本平台脚本注释用中文"
                                clearable />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <div class="filter-actions">
                            <n-button type="primary" @click="saveSetting">
                                <template #icon>
                                    <i class="fa-solid fa-check"></i>
                                </template>
                                保存设置
                            </n-button>
                        </div>
                    </n-gi>
                </n-grid>
            </n-form>
        </div>

        <!-- AI 写权限（高危）：默认全关；开启后 AI 在对话中直接执行对应写操作（无逐条确认） -->
        <div class="filter-card danger-card">
            <div class="card-title danger-title">
                <i class="fa-solid fa-triangle-exclamation"></i> AI 写权限（高危）
            </div>
            <div class="danger-desc">开启后 AI 将在对话中直接执行对应写操作（无逐条确认），请仅在与可信模型/可信内容交互时开启；全部操作可在系统日志（AI助手类）中追溯；写权限仅对你在对话中主动发起的运行生效，任务失败自动分析的运行恒只读</div>
            <div class="perm-grid">
                <div v-for="p in WritePerms" :key="p.key" class="perm-item">
                    <n-switch v-model:value="Setting[p.key]" class="perm-switch" />
                    <div class="perm-text">
                        <div class="perm-name">{{ p.name }}</div>
                        <div class="perm-desc">{{ p.desc }}</div>
                    </div>
                </div>
            </div>
            <div class="filter-actions perm-actions">
                <n-button type="error" @click="saveSetting">
                    <template #icon>
                        <i class="fa-solid fa-check"></i>
                    </template>
                    保存写权限设置
                </n-button>
            </div>
        </div>
    </div>
</template>
<script>
  import {
    GetAiSetting,
    SaveAiSetting
  } from '@/api/ai'

  export default {
    name: 'AiSetting',
    data() {
      return {
        Setting: {
          Enable: true,
          MaxRounds: 8,
          RunTimeoutSeconds: 600,
          LogTailLines: 200,
          AutoAnalyzeOnFailure: false,
          TestRunNotifyMode: 'task',
          TestRunRequireConfirm: false,
          KeepVersionsPerFile: 30,
          SaveFullPrompt: false,
          SystemPromptExtra: null,
          // AI 写权限四开关（默认全关，防止保存时 undefined 绑定把开关置空）
          AllowScriptDelete: false,
          AllowTaskManage: false,
          AllowEnvManage: false,
          AllowCustomDataManage: false
        },
        WritePerms: [
          { key: 'AllowScriptDelete', name: '删除脚本文件', desc: '允许 AI 直接删除任务脚本文件（删除前自动版本留底，可从脚本版本页恢复）' },
          { key: 'AllowTaskManage', name: '任务管理', desc: '允许 AI 添加/修改/删除任务（编辑按字段合并不清空未传字段，删除前自动备份）' },
          { key: 'AllowEnvManage', name: '环境变量管理', desc: '允许 AI 添加/编辑/删除环境变量（写入的值会流经模型与供应商，对不可信内容分析时勿开）' },
          { key: 'AllowCustomDataManage', name: '自定义数据管理', desc: '允许 AI 增删改自定义数据类型与数据（行删除有备份，类型删除与整类清空无备份）' }
        ]
      }
    },
    computed: {
      NotifyModeOptions() {
        return [
          { label: '按任务配置', value: 'task' },
          { label: '强制开启', value: 'force' },
          { label: '静默（不发通知）', value: 'silent' }
        ]
      }
    },
    methods: {
      loadSetting() {
        GetAiSetting().then((res) => {
          this.Setting = res
        })
      },
      saveSetting() {
        SaveAiSetting(JSON.parse(JSON.stringify(this.Setting))).then((res) => {
          this.Setting = res
          this.$message.success('AI 设置已保存')
        })
      }
    },
    mounted() {
      this.loadSetting()
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

/* AI 写权限（高危）卡 */
.danger-card {
    margin-top: 12px;
    border-color: rgba(255, 107, 107, 0.35);
}

.danger-title {
    margin-bottom: 4px;
    color: #ff6b6b;
}

.danger-title i {
    color: #ff6b6b;
}

.danger-desc {
    margin-bottom: 14px;
    color: var(--text-3);
    font-size: 12px;
    line-height: 1.6;
}

.perm-grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 12px;
}

@media (max-width: 760px) {
    .perm-grid {
        grid-template-columns: 1fr;
    }
}

.perm-item {
    display: flex;
    align-items: flex-start;
    gap: 10px;
    padding: 12px 14px;
    background: rgba(255, 107, 107, 0.04);
    border: 1px solid rgba(255, 107, 107, 0.18);
    border-radius: 8px;
    height: 100%;
    box-sizing: border-box;
}

.perm-switch {
    flex: none;
    margin-top: 2px;
}

.perm-text {
    min-width: 0;
}

.perm-name {
    color: var(--text);
    font-size: 13px;
    font-weight: 500;
    line-height: 20px;
}

.perm-desc {
    margin-top: 4px;
    color: var(--text-3);
    font-size: 12px;
    line-height: 1.6;
}

.perm-actions {
    margin-top: 14px;
    height: auto;
}
</style>
