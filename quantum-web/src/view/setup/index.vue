<template>
    <div class="setup">
        <div class="setup-card glass-card">
            <img class="logo-mark" src="@/assets/images/logo-min.png" alt="Quantum Logo" />
            <h2>首次部署初始化</h2>
            <div class="sub">QUANTUM SETUP WIZARD</div>

            <n-steps :current="step" size="small" class="steps">
                <n-step title="创建账号" />
                <n-step title="选择数据库" />
                <n-step title="初始化任务" />
            </n-steps>

            <!-- 第 1 步：创建登录账号 -->
            <div v-if="step === 1" class="step-body">
                <n-alert type="info" show-icon class="tip">
                    为你的量子平台设置唯一的登录账号（Web 管理端与 App 共用）。
                </n-alert>
                <n-form label-placement="top">
                    <n-form-item label="登录用户名">
                        <n-input
                            v-model:value="form.userName"
                            placeholder="如 admin"
                            maxlength="64"
                        />
                    </n-form-item>
                    <n-form-item label="登录密码">
                        <n-input
                            v-model:value="form.passWord"
                            type="password"
                            show-password-on="click"
                            placeholder="至少 6 位"
                        />
                    </n-form-item>
                    <n-form-item label="确认密码">
                        <n-input
                            v-model:value="form.confirmPassWord"
                            type="password"
                            show-password-on="click"
                            placeholder="再输入一次"
                            @keyup.enter="next"
                        />
                    </n-form-item>
                </n-form>
            </div>

            <!-- 第 2 步：选择数据库 -->
            <div v-if="step === 2" class="step-body">
                <n-alert type="info" show-icon class="tip">
                    选择平台数据的存储方式。当前生效：{{ boot.DBType || 'SQLite' }}
                    <template v-if="boot.DBAddress">（{{ boot.DBAddress }}）</template>
                </n-alert>
                <n-radio-group v-model:value="form.dbType" class="db-types">
                    <div v-for="item in dbTypes" :key="item.value" class="db-type-item">
                        <n-radio :value="item.value">{{ item.label }}</n-radio>
                        <div class="db-type-hint">{{ item.hint }}</div>
                    </div>
                </n-radio-group>
                <n-form label-placement="top">
                    <n-form-item v-if="form.dbType !== 'MySql'" label="SQLite 数据库文件名">
                        <n-input
                            v-model:value="form.dbAddress"
                            placeholder="如 Quantum.db（存放于 db/ 目录）"
                            @keyup.enter="next"
                        />
                    </n-form-item>
                    <template v-else>
                        <n-form-item label="填写方式">
                            <n-radio-group v-model:value="mysqlMode">
                                <n-radio value="form">引导填写（推荐）</n-radio>
                                <n-radio value="string">连接串直填</n-radio>
                            </n-radio-group>
                        </n-form-item>
                        <template v-if="mysqlMode === 'form'">
                            <n-form-item label="数据库主机（IP 或域名）">
                                <n-input
                                    v-model:value="mysqlForm.host"
                                    placeholder="如 127.0.0.1"
                                />
                            </n-form-item>
                            <div class="mysql-row">
                                <n-form-item label="端口" class="mysql-port">
                                    <n-input v-model:value="mysqlForm.port" placeholder="3306" />
                                </n-form-item>
                                <n-form-item label="数据库名" class="mysql-db">
                                    <n-input v-model:value="mysqlForm.database" placeholder="quantum" />
                                </n-form-item>
                            </div>
                            <div class="mysql-row">
                                <n-form-item label="账号" class="mysql-db">
                                    <n-input v-model:value="mysqlForm.user" placeholder="root" />
                                </n-form-item>
                                <n-form-item label="密码" class="mysql-db">
                                    <n-input
                                        v-model:value="mysqlForm.password"
                                        type="password"
                                        show-password-on="click"
                                        placeholder="数据库账号密码"
                                    />
                                </n-form-item>
                            </div>
                            <div class="conn-preview">连接串：{{ effectiveDbAddress }}</div>
                        </template>
                        <n-form-item v-else label="MySQL 连接串">
                            <n-input
                                v-model:value="form.dbAddress"
                                type="textarea"
                                :rows="2"
                                placeholder="Server=127.0.0.1;Port=3306;Database=quantum;Uid=root;Pwd=xxx;CharSet=utf8;"
                            />
                        </n-form-item>
                    </template>
                </n-form>
                <div class="db-actions">
                    <n-button size="small" :loading="testingDb" @click="testDatabase">
                        测试连接
                    </n-button>
                    <n-tag v-if="dbTestResult" :type="dbTestResult.Success ? 'success' : 'error'" size="small">
                        {{ dbTestResult.Message }}
                    </n-tag>
                </div>
                <n-alert v-if="databaseChanged" type="warning" show-icon class="tip">
                    数据库发生切换：需重启服务后生效，勾选的任务将在重启后自动创建。
                </n-alert>
            </div>

            <!-- 第 3 步：选择自带脚本建任务 -->
            <div v-if="step === 3" class="step-body">
                <n-alert type="info" show-icon class="tip">
                    平台自带脚本已整理成任务模板，勾选后自动创建任务（停用状态，配置好环境变量即可启用）。
                </n-alert>
                <div class="script-toolbar">
                    <span class="script-summary">{{ selectionSummary }}</span>
                    <n-button size="tiny" quaternary @click="selectAll">全选</n-button>
                    <n-button size="tiny" quaternary @click="selectedScripts = []">清空</n-button>
                </div>
                <n-checkbox-group v-model:value="selectedScripts" class="script-list">
                    <div v-for="script in scripts" :key="script.FileName" class="script-item">
                        <n-checkbox :value="script.FileName" :label="script.Name" />
                        <div class="script-desc">{{ script.Description || '（无说明）' }}</div>
                    </div>
                </n-checkbox-group>
            </div>

            <!-- 完成页 -->
            <div v-if="step === 4" class="step-body">
                <n-alert :type="result && result.RestartRequired ? 'warning' : 'success'" show-icon class="tip">
                    {{ result ? result.Message : '初始化完成' }}
                </n-alert>
                <n-alert
                    v-if="result && result.RestartRequired"
                    type="info"
                    show-icon
                    class="tip"
                >
                    重启方法（Docker）：<span class="mono">docker restart quantum</span>
                    ；重启完成后用你创建的账号登录，勾选的任务会自动创建。
                </n-alert>
                <div class="finish-actions">
                    <n-button
                        v-if="result && !result.RestartRequired"
                        type="primary"
                        :loading="autoLogging"
                        @click="autoLogin"
                    >
                        进入平台
                    </n-button>
                    <n-button v-else type="primary" @click="goLogin">去登录</n-button>
                </div>
            </div>

            <div v-if="step < 4" class="actions">
                <n-button v-if="step > 1" quaternary @click="prev">上一步</n-button>
                <n-button v-if="step < 3" type="primary" @click="next">下一步</n-button>
                <n-button v-else type="primary" :loading="submitting" @click="submit">
                    完成初始化
                </n-button>
            </div>
        </div>
    </div>
</template>

<script>
import { GetSetupStatus, TestSetupDatabase, CompleteSetup } from '@/api/setup'
import { useUserStore } from '@/stores/user'
import {
    DB_TYPES,
    MYSQL_FORM_DEFAULTS,
    allScriptFileNames,
    buildMySqlConnection,
    databaseUnchanged,
    scriptSelectionSummary,
    validateAccount,
    validateDatabase,
    validateMySqlForm
} from '@/utils/setupWizard'

export default {
    name: 'SetupWizard',
    setup() {
        const userStore = useUserStore()
        return { userStore }
    },
    data() {
        return {
            step: 1,
            ready: false,
            dbTypes: DB_TYPES,
            boot: { DBType: '', DBAddress: '' },
            scripts: [],
            selectedScripts: [],
            form: {
                userName: '',
                passWord: '',
                confirmPassWord: '',
                dbType: 'SQLite',
                dbAddress: ''
            },
            // MySQL 填写方式：form = 引导填 IP/端口/账号密码（拼成连接串）；string = 连接串直填
            mysqlMode: 'form',
            mysqlForm: { ...MYSQL_FORM_DEFAULTS },
            testingDb: false,
            dbTestResult: null,
            submitting: false,
            autoLogging: false,
            result: null
        }
    },
    computed: {
        // 实际参与校验/试连/提交的库地址：引导模式下由 IP/端口/账号密码拼成连接串
        effectiveDbAddress() {
            if (this.form.dbType === 'MySql' && this.mysqlMode === 'form') {
                return buildMySqlConnection(this.mysqlForm)
            }
            return this.form.dbAddress
        },
        databaseChanged() {
            return !databaseUnchanged(this.boot, {
                DBType: this.form.dbType,
                DBAddress: this.effectiveDbAddress
            })
        },
        selectionSummary() {
            return scriptSelectionSummary(this.scripts, this.selectedScripts)
        }
    },
    methods: {
        // 第 2 步校验：引导模式逐项校验 IP/端口/账号密码，直填模式校验连接串
        validateCurrentDatabase() {
            if (this.form.dbType === 'MySql' && this.mysqlMode === 'form') {
                return validateMySqlForm(this.mysqlForm)
            }
            return validateDatabase({
                dbType: this.form.dbType,
                dbAddress: this.effectiveDbAddress
            })
        },
        async loadStatus() {
            try {
                const status = await GetSetupStatus()
                if (!status || !status.NeedSetup) {
                    // 已初始化过：向导入口关闭，回登录页
                    this.$router.replace({ name: 'login' }).catch(() => {})
                    return
                }
                this.boot = {
                    DBType: status.DBType || 'SQLite',
                    DBAddress: status.DBAddress || ''
                }
                this.scripts = status.Scripts || []
                this.selectedScripts = allScriptFileNames(this.scripts)
                this.form.dbType = this.boot.DBType
                this.form.dbAddress = this.boot.DBAddress
                this.ready = true
            } catch (error) {
                this.$message?.error(String(error))
            }
        },
        next() {
            const error = this.step === 1 ? validateAccount(this.form) : this.validateCurrentDatabase()
            if (error) {
                this.$message?.warning(error)
                return
            }
            this.step = Math.min(3, this.step + 1)
        },
        prev() {
            this.step = Math.max(1, this.step - 1)
        },
        selectAll() {
            this.selectedScripts = allScriptFileNames(this.scripts)
        },
        async testDatabase() {
            const error = this.validateCurrentDatabase()
            if (error) {
                this.$message?.warning(error)
                return
            }
            this.testingDb = true
            this.dbTestResult = null
            try {
                this.dbTestResult = await TestSetupDatabase({
                    DBType: this.form.dbType,
                    DBAddress: this.effectiveDbAddress
                })
            } catch (error) {
                // 拦截器已把业务错误消息 toast 出来，这里留空结果即可
                this.dbTestResult = null
            } finally {
                this.testingDb = false
            }
        },
        async submit() {
            const accountError = validateAccount(this.form)
            if (accountError) {
                this.$message?.warning(accountError)
                this.step = 1
                return
            }
            const dbError = this.validateCurrentDatabase()
            if (dbError) {
                this.$message?.warning(dbError)
                this.step = 2
                return
            }
            this.submitting = true
            try {
                this.result = await CompleteSetup({
                    UserName: this.form.userName.trim(),
                    PassWord: this.form.passWord,
                    DBType: this.form.dbType,
                    DBAddress: this.effectiveDbAddress.trim(),
                    Scripts: this.selectedScripts
                })
                this.step = 4
            } catch (error) {
                this.$message?.error(String(error))
            } finally {
                this.submitting = false
            }
        },
        // 完成且无需重启：直接用新账号登录进平台（落点由主框架按「默认首页」解析）
        async autoLogin() {
            this.autoLogging = true
            try {
                const token = await this.userStore.handleLogin({
                    userName: this.form.userName.trim(),
                    password: this.form.passWord
                })
                if (!token) return
                localStorage.setItem('accessToken', 'Bearer ' + token)
                this.$router.push({ path: '/' }).catch(() => {})
            } catch (error) {
                this.$message?.error(String(error))
            } finally {
                this.autoLogging = false
            }
        },
        goLogin() {
            this.$router.replace({ name: 'login' }).catch(() => {})
        }
    },
    mounted() {
        this.loadStatus()
    }
}
</script>

<style scoped>
/* 风格 B：与登录页同款网格底纹 + 玻璃卡片 */
.setup {
    height: 100%;
    position: relative;
    display: flex;
    align-items: center;
    justify-content: center;
    background:
        radial-gradient(1200px 600px at 20% -10%, #1e293b 0%, transparent 60%),
        radial-gradient(900px 500px at 110% 110%, #134e4a 0%, transparent 55%),
        var(--bg);
    overflow: auto;
    padding: 32px 16px;
}

.setup::before {
    content: '';
    position: absolute;
    inset: 0;
    background-image:
        linear-gradient(var(--line) 1px, transparent 1px),
        linear-gradient(90deg, var(--line) 1px, transparent 1px);
    background-size: 44px 44px;
    opacity: .35;
    mask-image: radial-gradient(600px 400px at 50% 45%, #000 30%, transparent 100%);
    -webkit-mask-image: radial-gradient(600px 400px at 50% 45%, #000 30%, transparent 100%);
}

.setup-card {
    position: relative;
    width: 560px;
    max-width: 100%;
    background: rgba(17, 26, 46, .85);
    backdrop-filter: blur(14px);
    border-color: var(--line-2);
    border-radius: 16px;
    padding: 32px 34px;
    box-shadow: 0 24px 80px rgba(0, 0, 0, .6), var(--glow);
    z-index: 1;
}

.logo-mark {
    width: 52px;
    height: 52px;
    margin: 0 auto 16px;
    border-radius: 13px;
    display: block;
    object-fit: cover;
    box-shadow: 0 8px 24px rgba(0, 0, 0, .5);
}

.setup-card h2 {
    text-align: center;
    font-size: 22px;
    margin: 0 0 4px;
    color: var(--text);
}

.setup-card .sub {
    text-align: center;
    color: var(--text-3);
    font-size: 12px;
    margin-bottom: 20px;
    letter-spacing: 1px;
}

.steps {
    margin-bottom: 18px;
}

.step-body {
    min-height: 220px;
}

.tip {
    margin-bottom: 14px;
}

.db-types {
    display: block;
    margin-bottom: 8px;
}

.db-type-item {
    padding: 10px 12px;
    border: 1px solid var(--line-2);
    border-radius: 10px;
    margin-bottom: 10px;
}

.db-type-hint {
    margin-top: 4px;
    margin-left: 24px;
    color: var(--text-3);
    font-size: 12px;
}

.db-actions {
    display: flex;
    align-items: center;
    gap: 10px;
    flex-wrap: wrap;
    margin-bottom: 12px;
}

/* MySQL 引导表单：端口与库名/账号密码两列排布 */
.mysql-row {
    display: flex;
    gap: 12px;
}

.mysql-port {
    width: 130px;
    flex: none;
}

.mysql-db {
    flex: 1;
}

.conn-preview {
    margin: -6px 0 12px;
    padding: 8px 10px;
    border: 1px dashed var(--line-2);
    border-radius: 8px;
    color: var(--text-3);
    font-size: 12px;
    word-break: break-all;
    line-height: 1.6;
}

.script-toolbar {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 10px;
}

.script-summary {
    flex: 1;
    color: var(--text-3);
    font-size: 12px;
}

.script-list {
    display: block;
    max-height: 300px;
    overflow: auto;
    border: 1px solid var(--line-2);
    border-radius: 10px;
    padding: 6px 12px;
}

.script-item {
    padding: 8px 0;
    border-bottom: 1px dashed var(--line);
}

.script-item:last-child {
    border-bottom: none;
}

.script-desc {
    margin-left: 24px;
    margin-top: 2px;
    color: var(--text-3);
    font-size: 12px;
    line-height: 1.6;
}

.actions,
.finish-actions {
    display: flex;
    justify-content: flex-end;
    gap: 10px;
    margin-top: 18px;
}

.finish-actions {
    justify-content: center;
}

.mono {
    font-family: monospace;
}
</style>
