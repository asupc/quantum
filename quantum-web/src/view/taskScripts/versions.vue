<template>
    <div>
        <div class="filter-card">
            <n-form :show-feedback="false" label-placement="left" label-width="auto">
                <n-grid cols="1 s:2 m:2 l:3 xl:4" responsive="screen" :x-gap="12" :y-gap="12">
                    <n-gi>
                        <n-form-item label="脚本文件">
                            <n-select v-model:value="QueryForm.FileName" :options="FileOptions" clearable filterable
                                placeholder="全部脚本（按最近变更排序）" @update:value="loadVersions(1)" />
                        </n-form-item>
                    </n-gi>
                    <n-gi>
                        <div class="filter-actions">
                            <n-button type="primary" @click="loadVersions(1)">
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
                <n-button type="primary" size="small" @click="loadAll(true)">
                    <template #icon>
                        <i class="fa-solid fa-rotate"></i>
                    </template>
                    刷新
                </n-button>
                <n-button type="error" size="small" @click="deleteSelection">
                    <template #icon>
                        <i class="fa-solid fa-trash-can"></i>
                    </template>
                    删除选中
                </n-button>
            </n-space>
            <span style="margin-left: 12px; color: var(--text-3)">
                版本历史每文件保留最近 30 版（锁定的版本不清理）；当前版本以磁盘文件为准。
            </span>
        </div>

        <n-data-table ref="VersionTable" :columns="VersionColumns" :data="VersionData" :row-key="(row) => row.Id"
            :checked-row-keys="CheckedRowKeys" @update:checked-row-keys="onCheckedRowKeys" :scroll-x="1200"
            :bordered="true" size="small" />

        <div style="margin: 10px; overflow: hidden">
            <div style="float: right">
                <n-pagination :item-count="PageInfo.Total" :page="QueryForm.PageIndex" :page-size="QueryForm.PageSize"
                    :page-sizes="PageInfo.PageSizeOpts" show-size-picker @update:page-size="pageSizeChange"
                    @update:page="loadVersions">
                    <template #prefix>
                        <span class="mono">共 {{ PageInfo.Total }} 条</span>
                    </template>
                </n-pagination>
            </div>
        </div>

        <!-- 版本正文 -->
        <n-modal preset="card" v-model:show="ViewModal.Show" :title="ViewModal.Title" style="width: 1000px"
            :mask-closable="false">
            <div class="version-code">
                <pre class="mono">{{ ViewModal.Content }}</pre>
            </div>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="ViewModal.Show = false">关闭</n-button>
                </div>
            </template>
        </n-modal>

        <!-- 版本对比 -->
        <n-modal preset="card" v-model:show="DiffModal.Show" :title="DiffModal.Title" style="width: 1200px"
            :mask-closable="false">
            <n-form :show-feedback="false" label-placement="left" label-width="90">
                <n-form-item label="对比目标">
                    <n-select v-model:value="DiffModal.Target" :options="DiffTargetOptions" style="width: 420px"
                        @update:value="renderDiff" />
                </n-form-item>
            </n-form>
            <div style="margin-bottom: 8px">
                <span class="pill ok"><span class="dot"></span>新增 {{ DiffModal.Added }}</span>
                <span class="pill danger" style="margin-left: 8px"><span class="dot"></span>删除 {{ DiffModal.Removed }}</span>
                <span v-if="DiffModal.Truncated" style="margin-left: 12px; color: var(--warn)">
                    差异过大，已按整块替换展示
                </span>
            </div>
            <div class="version-diff">
                <div v-for="(row, index) in RenderedRows" :key="index" :class="['diff-row', 'diff-' + row.kind]">
                    <span class="diff-no">{{ row.oldLine === null ? '' : row.oldLine }}</span>
                    <span class="diff-no">{{ row.newLine === null ? '' : row.newLine }}</span>
                    <span class="diff-sign">{{ row.kind === 'add' ? '+' : row.kind === 'del' ? '-' : ' ' }}</span>
                    <span class="diff-text mono">{{ row.text }}</span>
                </div>
                <div v-if="DiffModal.Rows.length > MaxDiffRows" class="diff-more">
                    仅展示前 {{ MaxDiffRows }} 行差异（共 {{ DiffModal.Rows.length }} 行）
                </div>
            </div>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="DiffModal.Show = false">关闭</n-button>
                </div>
            </template>
        </n-modal>

        <!-- 回滚被拒时的诊断清单 -->
        <n-modal preset="card" v-model:show="Diagnostics.Show" title="回滚被拒绝" style="width: 760px">
            <n-alert type="error" :show-icon="true" style="margin-bottom: 12px">
                历史版本未通过安全门禁/编译检查，内容未落盘（与在线保存同口径）。
            </n-alert>
            <div v-for="group in Diagnostics.Groups" :key="group.Title" style="margin-bottom: 12px">
                <div style="margin-bottom: 6px">{{ group.Title }}（{{ group.Items.length }}）</div>
                <div class="diag-list">
                    <div v-for="(item, index) in group.Items" :key="index" class="diag-item mono">
                        [第{{ item.Line }}行] {{ item.Message }}
                    </div>
                </div>
            </div>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="Diagnostics.Show = false">关闭</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>
<script>
  import { h } from 'vue'
  import { renderOpActions, OpColor } from '@/utils/op-actions'
  import { diffLines } from '@/utils/lineDiff'
  import { GetScripts } from '@/api/task'
  import {
    GetScriptVersions,
    GetVersionFiles,
    GetScriptVersion,
    RollbackVersion,
    PinVersion,
    DeleteVersions
  } from '@/api/scriptVersion'

  // 来源 → 展示标签与语义色
  const SOURCE_LABELS = {
    ManualEdit: { text: '在线保存', cls: 'ok' },
    Upload: { text: '上传', cls: 'ok' },
    AgentApply: { text: 'AI 应用', cls: 'warn' },
    Rollback: { text: '回滚', cls: 'warn' },
    Import: { text: '导入', cls: 'off' }
  }

  export default {
    name: 'ScriptVersions',
    data() {
      return {
        listSeq: 0,
        MaxDiffRows: 3000,
        CheckedRowKeys: [],
        VersionColumns: [
          { type: 'selection' },
          {
            title: '序号',
            width: 60,
            align: 'center',
            render: (row, index) => index + 1
          },
          {
            title: '脚本文件',
            key: 'FileName',
            width: 200,
            className: 'mono',
            ellipsis: { tooltip: true },
            render: (row) => {
              return this.tdTip(h, { row }, 'FileName')
            }
          },
          {
            title: '时间',
            key: 'CreateTime',
            width: 160,
            render: (row) => this.formatTime(row.CreateTime)
          },
          {
            title: '来源',
            key: 'Source',
            width: 110,
            align: 'center',
            render: (row) => {
              const label = SOURCE_LABELS[row.Source] || { text: row.Source || '-', cls: 'off' }
              return h('span', { class: `pill ${label.cls}` }, [h('span', { class: 'dot' }), label.text])
            }
          },
          {
            title: '大小',
            key: 'Size',
            width: 90,
            align: 'right',
            className: 'mono',
            render: (row) => this.formatSize(row.Size)
          },
          {
            title: '行数',
            key: 'LineCount',
            width: 70,
            align: 'right',
            className: 'mono'
          },
          {
            title: '备注',
            key: 'Remark',
            minWidth: 200,
            render: (row) => {
              return this.tdTip(h, { row }, 'Remark')
            }
          },
          {
            title: '操作人',
            key: 'Creator',
            width: 90,
            render: (row) => row.Creator || '-'
          },
          {
            title: '锁定',
            key: 'IsPinned',
            width: 70,
            align: 'center',
            render: (row) => (row.IsPinned ? h('i', { class: 'fa-solid fa-lock', style: 'color: var(--warn)' }) : '')
          },
          {
            title: '操作',
            key: 'action',
            width: 150,
            align: 'center',
            fixed: 'right',
            render: (row) =>
              renderOpActions(h, [
                { icon: 'fa-eye', title: '查看', color: OpColor.Info, onClick: () => this.viewVersion(row) },
                { icon: 'fa-code-compare', title: '对比', color: OpColor.Info, onClick: () => this.openDiff(row) },
                { icon: 'fa-clock-rotate-left', title: '回滚', color: OpColor.Run, onClick: () => this.rollback(row) },
                {
                  icon: row.IsPinned ? 'fa-lock-open' : 'fa-lock',
                  title: row.IsPinned ? '解锁' : '锁定',
                  color: OpColor.Edit,
                  onClick: () => this.togglePin(row)
                },
                { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.deleteVersions([row.Id]) }
              ])
          }
        ],
        QueryForm: {
          FileName: null,
          PageIndex: 1,
          PageSize: 20
        },
        PageInfo: {
          Total: 0,
          PageSizeOpts: [15, 20, 50, 100, 200, 500, 1000]
        },
        VersionData: [],
        FileOptions: [],
        ViewModal: {
          Show: false,
          Title: '版本内容',
          Content: ''
        },
        DiffModal: {
          Show: false,
          Title: '版本对比',
          Left: null,
          Target: 'current',
          OtherId: null,
          Rows: [],
          Added: 0,
          Removed: 0,
          Truncated: false
        },
        Diagnostics: {
          Show: false,
          Groups: []
        }
      }
    },
    computed: {
      RenderedRows() {
        return this.DiffModal.Rows.slice(0, this.MaxDiffRows)
      },
      DiffTargetOptions() {
        // 「当前文件」+ 本页其余版本（同一脚本的历史版本可直接互相对比）
        const options = [{ label: '当前脚本文件（磁盘最新）', value: 'current' }]
        for (const item of this.VersionData) {
          if (this.DiffModal.Left && item.Id === this.DiffModal.Left.Id) continue
          options.push({
            label: `${this.formatTime(item.CreateTime)} · ${(SOURCE_LABELS[item.Source] || {}).text || item.Source}`,
            value: item.Id
          })
        }
        return options
      }
    },
    methods: {
      onCheckedRowKeys(keys) {
        this.CheckedRowKeys = keys
      },
      pageSizeChange(size) {
        this.QueryForm.PageSize = size
        this.loadVersions(1)
      },
      resetQuery() {
        this.QueryForm.FileName = null
        this.loadVersions(1)
      },
      formatSize(size) {
        const bytes = Number(size || 0)
        if (bytes < 1024) return bytes + ' B'
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
        return (bytes / 1024 / 1024).toFixed(2) + ' MB'
      },
      formatTime(value) {
        return value ? String(value).replace('T', ' ').slice(0, 19) : ''
      },
      showDiagnostics(result) {
        const groups = []
        if ((result.Blocked || []).length) groups.push({ Title: '安全门禁拦截', Items: result.Blocked })
        if ((result.Errors || []).length) groups.push({ Title: '编译错误', Items: result.Errors })
        if ((result.Warnings || []).length) groups.push({ Title: '警告', Items: result.Warnings })
        this.Diagnostics.Groups = groups
        this.Diagnostics.Show = true
      },
      loadFiles() {
        GetVersionFiles().then((res) => {
          this.FileOptions = (res || []).map((n) => ({
            label: `${n.FileName}（${n.Count} 版）`,
            value: n.FileName
          }))
        }).catch(() => {})
      },
      loadAll(manual) {
        this.loadFiles()
        this.loadVersions(manual ? 1 : undefined)
      },
      loadVersions(pageIndex) {
        this.QueryForm.PageIndex = pageIndex || this.QueryForm.PageIndex || 1
        const seq = ++this.listSeq
        GetScriptVersions(this.QueryForm).then((res) => {
          if (seq !== this.listSeq) return
          this.VersionData = res.Data
          this.PageInfo.Total = res.TotalCount
          this.loadFiles()
        }).catch((error) => {
          if (seq !== this.listSeq) return
          this.$message.error(String((error && error.message) || error || '版本列表加载失败'))
        })
      },
      viewVersion(row) {
        GetScriptVersion(row.Id).then((res) => {
          this.ViewModal.Title = `${res.FileName} · ${this.formatTime(res.CreateTime)}（${(SOURCE_LABELS[res.Source] || {}).text || res.Source}）`
          this.ViewModal.Content = res.Content || ''
          this.ViewModal.Show = true
        })
      },
      openDiff(row) {
        this.DiffModal.Left = row
        this.DiffModal.Target = 'current'
        this.DiffModal.Title = `版本对比：${row.FileName}`
        this.DiffModal.Show = true
        this.renderDiff()
      },
      renderDiff() {
        const left = this.DiffModal.Left
        if (!left) return
        const leftContent = GetScriptVersion(left.Id).then((res) => res.Content || '')
        const rightContent = this.DiffModal.Target === 'current'
          ? GetScripts({ fileName: left.FileName })
          : GetScriptVersion(this.DiffModal.Target).then((res) => res.Content || '')
        Promise.all([leftContent, rightContent]).then(([oldText, newText]) => {
          const result = diffLines(oldText, newText)
          this.DiffModal.Rows = result.rows
          this.DiffModal.Added = result.added
          this.DiffModal.Removed = result.removed
          this.DiffModal.Truncated = result.truncated
        }).catch((error) => {
          this.$message.error(String((error && error.message) || error || '版本内容读取失败'))
        })
      },
      rollback(row) {
        const c = this
        this.$dialog.warning({
          title: '回滚脚本版本',
          content: `确定将「${row.FileName}」回滚到 ${this.formatTime(row.CreateTime)} 的版本？回滚同样要过安全门禁与编译检查。`,
          positiveText: '确认回滚',
          negativeText: '取消',
          onPositiveClick: function () {
            return RollbackVersion(row.Id).then((res) => {
              if (res && res.Success) {
                c.$message.success('回滚成功，已记入版本历史')
                c.loadAll(true)
                return
              }
              c.showDiagnostics(res || {})
            }).catch((error) => {
              c.$message.error(String((error && error.message) || error || '回滚失败'))
            })
          }
        })
      },
      togglePin(row) {
        const pinned = !row.IsPinned
        PinVersion(row.Id, pinned).then(() => {
          this.$message.success(pinned ? '已锁定（不会被保留策略清理）' : '已解锁')
          this.loadVersions()
        })
      },
      deleteSelection() {
        const ids = this.CheckedRowKeys
        if (!ids || ids.length === 0) {
          this.$message.warning('请选中版本后再操作！')
          return
        }
        this.deleteVersions(ids)
      },
      deleteVersions(ids) {
        const c = this
        this.$dialog.warning({
          title: '删除版本记录',
          content: '确定删除选中的版本记录？只删历史快照，不影响磁盘上的当前脚本。',
          positiveText: '确认删除',
          negativeText: '取消',
          onPositiveClick: function () {
            DeleteVersions(ids).then(() => {
              c.CheckedRowKeys = []
              c.loadAll(true)
            })
          }
        })
      },
      // 从脚本编辑页「历史版本」跳转过来时带 file 查询参数，直接定位该脚本
      applyRouteQuery() {
        const file = this.$route.query.file
        if (file && file !== this.QueryForm.FileName) {
          this.QueryForm.FileName = file
          this.loadVersions(1)
        }
      }
    },
    mounted() {
      this.applyRouteQuery()
      this.loadAll(true)
    },
    activated() {
      this.applyRouteQuery()
    }
  }
</script>
<style scoped>
/* 来源胶囊不折行（列宽已按最长标签「在线保存」预留） */
:deep(.pill) {
    white-space: nowrap;
}

.version-code {
    max-height: 60vh;
    overflow: auto;
    background: var(--card-2);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    padding: 12px;
}

.version-code pre {
    margin: 0;
    white-space: pre-wrap;
    word-break: break-all;
    font-size: 12px;
    line-height: 1.6;
}

.version-diff {
    max-height: 60vh;
    overflow: auto;
    background: var(--card-2);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    padding: 4px 0;
}

.diff-row {
    display: flex;
    align-items: flex-start;
    font-size: 12px;
    line-height: 1.6;
    white-space: pre-wrap;
    word-break: break-all;
}

.diff-no {
    flex: 0 0 52px;
    text-align: right;
    padding-right: 8px;
    color: var(--text-3);
    user-select: none;
    font-family: var(--mono, monospace);
}

.diff-sign {
    flex: 0 0 14px;
    text-align: center;
    user-select: none;
}

.diff-text {
    flex: 1;
    padding-right: 12px;
}

.diff-add {
    background: rgba(14, 165, 233, 0.12);
}

.diff-add .diff-sign {
    color: var(--ok);
}

.diff-del {
    background: rgba(239, 68, 68, 0.12);
}

.diff-del .diff-sign {
    color: var(--danger);
}

.diff-more,
.diag-item {
    padding: 4px 12px;
    color: var(--text-3);
    font-size: 12px;
}

.diag-list {
    max-height: 40vh;
    overflow: auto;
    background: var(--card-2);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    padding: 6px 0;
}
</style>
