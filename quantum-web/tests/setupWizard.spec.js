import { describe, it, expect } from 'vitest'
import {
    MIN_PASSWORD_LENGTH,
    MYSQL_FORM_DEFAULTS,
    allScriptFileNames,
    buildMySqlConnection,
    databaseUnchanged,
    isPlainFileName,
    scriptSelectionSummary,
    validateAccount,
    validateDatabase,
    validateMySqlForm,
    validateSetupForm
} from '../src/utils/setupWizard.js'

const validForm = {
    userName: ' admin ',
    passWord: 'secret-123',
    confirmPassWord: 'secret-123',
    dbType: 'SQLite',
    dbAddress: 'Quantum.db'
}

describe('初始化向导表单校验', () => {
    it('合法表单通过（用户名两侧空白忽略）', () => {
        expect(validateSetupForm(validForm)).toBe('')
    })

    it('用户名/密码逐项校验', () => {
        expect(validateAccount({ ...validForm, userName: '  ' })).toContain('登录用户名')
        expect(validateAccount({ ...validForm, userName: 'a'.repeat(65) })).toContain('过长')
        expect(validateAccount({ ...validForm, passWord: '12345' })).toContain(`至少 ${MIN_PASSWORD_LENGTH} 位`)
        expect(validateAccount({ ...validForm, confirmPassWord: 'other-pass' })).toContain('不一致')
    })

    it('数据库类型与地址校验：SQLite 禁路径，MySQL 必填连接串', () => {
        expect(validateDatabase({ ...validForm, dbType: 'oracle' })).toContain('数据库类型')
        expect(validateDatabase({ ...validForm, dbType: 'MySql', dbAddress: '' })).toContain('连接串')
        expect(validateDatabase({ ...validForm, dbAddress: '../evil.db' })).toContain('不能包含路径')
        expect(validateDatabase({ ...validForm, dbType: 'MySql', dbAddress: 'Server=1.1.1.1;' })).toBe('')
    })

    it('全量校验短路于第一条错误', () => {
        expect(validateSetupForm({ ...validForm, userName: '', dbAddress: '' })).toContain('登录用户名')
    })
})

describe('MySQL 引导表单（IP/端口/账号密码 → 连接串）', () => {
    it('默认值与完整填写校验', () => {
        expect(validateMySqlForm(MYSQL_FORM_DEFAULTS)).toContain('数据库主机')
        expect(
            validateMySqlForm({
                host: '127.0.0.1',
                port: '3306',
                database: 'quantum',
                user: 'root',
                password: 'secret-123'
            })
        ).toBe('')
    })

    it('端口/库名/账号/密码逐项校验', () => {
        const base = { host: '127.0.0.1', port: '3306', database: 'q', user: 'root', password: 'p' }
        expect(validateMySqlForm({ ...base, port: 'abc' })).toContain('端口')
        expect(validateMySqlForm({ ...base, port: '70000' })).toContain('端口')
        expect(validateMySqlForm({ ...base, database: ' ' })).toContain('数据库名')
        expect(validateMySqlForm({ ...base, user: '' })).toContain('数据库账号')
        expect(validateMySqlForm({ ...base, password: '' })).toContain('数据库密码')
    })

    it('拼接连接串：常规取值不加引号', () => {
        expect(
            buildMySqlConnection({
                host: '127.0.0.1',
                port: '3306',
                database: 'quantum',
                user: 'root',
                password: 'secret-123'
            })
        ).toBe('Server=127.0.0.1;Port=3306;Database=quantum;Uid=root;Pwd=secret-123;CharSet=utf8;')
    })

    it('拼接连接串：特殊字符取值加引号（引号加倍，分号不截断）', () => {
        const conn = buildMySqlConnection({
            host: '10.0.0.1',
            port: '3307',
            database: 'q db',
            user: 'ro"ot',
            password: 'a;b"c'
        })
        expect(conn).toContain('Database="q db"')
        expect(conn).toContain('Uid="ro""ot"')
        expect(conn).toContain('Pwd="a;b""c"')
    })
})

describe('数据库切换判定', () => {
    it('类型或地址一致 = 无需重启', () => {
        expect(
            databaseUnchanged(
                { DBType: 'SQLite', DBAddress: 'Quantum.db' },
                { DBType: 'sqlite', DBAddress: 'quantum.db' }
            )
        ).toBe(true)
    })

    it('SQLite→MySQL 切换需重启（大小写/空白不敏感比较）', () => {
        expect(
            databaseUnchanged(
                { DBType: 'SQLite', DBAddress: 'Quantum.db' },
                { DBType: 'MySql', DBAddress: ' Server=1.1.1.1; ' }
            )
        ).toBe(false)
        expect(
            databaseUnchanged(
                { DBType: 'SQLite', DBAddress: 'a.db' },
                { DBType: 'SQLite', DBAddress: 'b.db' }
            )
        ).toBe(false)
    })

    it('兼容后端 PascalCase 与小写两种字段名', () => {
        expect(
            databaseUnchanged({ dbType: 'sqlite', dbAddress: 'x.db' }, { DBType: 'SQLite', DBAddress: 'x.db' })
        ).toBe(true)
    })
})

describe('自带脚本选择', () => {
    const scripts = [{ FileName: 'a.cs' }, { FileName: 'b.cs' }, { FileName: 'c.cs' }]

    it('全选清单与选择摘要', () => {
        expect(allScriptFileNames(scripts)).toEqual(['a.cs', 'b.cs', 'c.cs'])
        expect(scriptSelectionSummary(scripts, ['a.cs', 'c.cs'])).toBe('已选 2/3 个脚本')
    })

    it('清单外文件名不计入摘要', () => {
        expect(scriptSelectionSummary(scripts, ['a.cs', 'evil.cs'])).toBe('已选 1/3 个脚本')
    })

    it('纯文件名判定', () => {
        expect(isPlainFileName('Quantum.db')).toBe(true)
        expect(isPlainFileName('sub/dir.db')).toBe(false)
        expect(isPlainFileName('..')).toBe(false)
        expect(isPlainFileName('')).toBe(false)
    })
})
