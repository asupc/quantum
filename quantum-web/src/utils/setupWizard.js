// 首次部署初始化向导纯逻辑（表单校验/是否需重启/脚本选择摘要），配 tests/setupWizard.spec.js。
// 向导三步：① 创建登录账号 → ② 选择数据库 → ③ 选择自带脚本建任务。

export const MIN_PASSWORD_LENGTH = 6

export const DB_TYPES = [
    { value: 'SQLite', label: 'SQLite（推荐）', hint: '本地数据库文件，零配置开箱即用' },
    { value: 'MySql', label: 'MySQL / MariaDB', hint: '独立数据库服务，适合大数据量或已有 MySQL 的环境' }
]

/** 第 1 步「创建登录账号」校验：错误消息，空串 = 通过。 */
export function validateAccount(form) {
    const userName = (form.userName || '').trim()
    if (!userName) return '请设置登录用户名'
    if (userName.length > 64) return '登录用户名过长（最多 64 个字符）'
    if (!form.passWord || form.passWord.length < MIN_PASSWORD_LENGTH) {
        return `登录密码至少 ${MIN_PASSWORD_LENGTH} 位`
    }
    if (form.passWord !== form.confirmPassWord) return '两次输入的密码不一致'
    return ''
}

/** MySQL 引导表单默认值（端口/库名/账号给常用缺省，密码必填）。 */
export const MYSQL_FORM_DEFAULTS = { host: '', port: '3306', database: 'quantum', user: 'root', password: '' }

/**
 * MySQL 引导表单校验（IP/端口/库名/账号/密码逐项）：错误消息，空串 = 通过。
 */
export function validateMySqlForm(fields) {
    if (!(fields.host || '').trim()) return '请填写数据库主机（IP 或域名）'
    const port = String(fields.port ?? '').trim()
    if (!/^\d{1,5}$/.test(port) || Number(port) < 1 || Number(port) > 65535) {
        return '请填写正确的端口（1-65535）'
    }
    if (!(fields.database || '').trim()) return '请填写数据库名'
    if (!(fields.user || '').trim()) return '请填写数据库账号'
    if (!fields.password) return '请填写数据库密码'
    return ''
}

/**
 * 连接串取值转义：含 ; " ' { } 或空白等特殊字符时用双引号包裹（内部引号加倍），
 * 干净取值保持原样——拼出的连接串与手填格式一致，便于与当前生效库做「是否切换」比较。
 */
function quoteConnectionStringValue(value) {
    const text = String(value ?? '')
    return /[;"'{}\s]/.test(text) ? '"' + text.replace(/"/g, '""') + '"' : text
}

/**
 * 把引导表单拼成 MySQL 连接串（与手填连接串同一后端契约，后续校验/试连/提交共用）。
 */
export function buildMySqlConnection(fields) {
    const parts = [
        'Server=' + quoteConnectionStringValue((fields.host || '').trim()),
        'Port=' + quoteConnectionStringValue(String(fields.port ?? '').trim()),
        'Database=' + quoteConnectionStringValue((fields.database || '').trim()),
        'Uid=' + quoteConnectionStringValue((fields.user || '').trim()),
        'Pwd=' + quoteConnectionStringValue(fields.password ?? ''),
        'CharSet=utf8'
    ]
    return parts.join(';') + ';'
}

/** 第 2 步「选择数据库」校验：错误消息，空串 = 通过。 */
export function validateDatabase(form) {
    const dbType = (form.dbType || '').trim().toLowerCase()
    if (dbType !== 'sqlite' && dbType !== 'mysql') return '请选择数据库类型'
    const dbAddress = (form.dbAddress || '').trim()
    if (!dbAddress) return dbType === 'sqlite' ? '请填写 SQLite 数据库文件名' : '请填写 MySQL 连接串'
    if (dbType === 'sqlite' && !isPlainFileName(dbAddress)) {
        return 'SQLite 数据库文件名不能包含路径（只允许纯文件名，如 Quantum.db）'
    }
    return ''
}

/**
 * 校验向导表单：返回第一条错误消息，空串 = 校验通过。
 * 与后端 SetupService.Complete 的校验规则保持一致（密码长度、库类型、SQLite 文件名禁路径）。
 */
export function validateSetupForm(form) {
    return validateAccount(form) || validateDatabase(form)
}

/** 纯文件名：不含任何路径分隔符且不是 . / .. */
export function isPlainFileName(name) {
    const value = (name || '').trim()
    return !!value && !/[\\/]/.test(value) && value !== '.' && value !== '..'
}

/**
 * 数据库选择与本次启动生效的库是否一致：一致则初始化任务可立即创建，
 * 发生切换（如 SQLite→MySQL）必须重启服务，任务改为重启后自动创建。
 */
export function databaseUnchanged(bootDatabase, chosen) {
    const bootType = (bootDatabase?.DBType || bootDatabase?.dbType || '').trim().toLowerCase()
    const chosenType = (chosen?.DBType || chosen?.dbType || '').trim().toLowerCase()
    const bootAddress = (bootDatabase?.DBAddress || bootDatabase?.dbAddress || '').trim().toLowerCase()
    const chosenAddress = (chosen?.DBAddress || chosen?.dbAddress || '').trim().toLowerCase()
    return bootType === chosenType && bootAddress === chosenAddress
}

/** 脚本选择摘要（第 3 步标题栏展示），如「已选 3/5 个脚本」。 */
export function scriptSelectionSummary(scripts, selectedFileNames) {
    const total = (scripts || []).length
    const selected = (selectedFileNames || []).filter((name) =>
        (scripts || []).some((script) => script.FileName === name)
    ).length
    return `已选 ${selected}/${total} 个脚本`
}

/** 全选后的文件名清单（向导「全选」按钮）。 */
export function allScriptFileNames(scripts) {
    return (scripts || []).map((script) => script.FileName)
}
