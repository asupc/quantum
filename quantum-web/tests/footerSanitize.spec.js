/**
 * 登录页页脚净化回归（对抗性审计 F1：匿名 footer 端点 + 登录页 v-html = 未认证页面上的存储型 XSS）。
 *
 * 背景：`GET /api/SystemConfig/footer` 是 [AllowAnonymous] 端点，回吐管理端可写的 Setting.Footer
 * （后台文案明确「支持html标签」）；登录页此前直接 `v-html="Footer"`，配置一旦被污染（或未来任何
 * 导入/恢复路径带进脏值）即在**未认证页面**执行脚本——登录令牌存于 localStorage，等同账号接管，
 * 且项目没有任何 CSP 兜底。
 *
 * 口径：白名单重建（允许的行内标签/属性之外一律丢弃），不是黑名单过滤；属性值先解码实体、
 * 再按各属性严格正则校验、最后重新转义输出，绝不回抄源码片段。本文件同时锁定
 * 「登录页只绑净化值」这一渲染路径契约（computed + 模板源码两处证据）。
 */
import { beforeAll, describe, expect, it, vi } from 'vitest'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { sanitizeFooterHtml } from '../src/utils/footerHtml.js'

// 登录页 import 链（api/stores）在 node 环境只需可导入，不触发真实请求
vi.mock('@/api/setting.js', () => ({ GetFooter: vi.fn() }))
vi.mock('@/api/user.js', () => ({ QrCreate: vi.fn(), QrStatus: vi.fn() }))
vi.mock('@/api/setup.js', () => ({ GetSetupStatus: vi.fn() }))
vi.mock('@/stores/user', () => ({ useUserStore: () => ({ handleLogin: vi.fn() }) }))

/** 去掉全部白名单标签后不得残留 '<'：残留即可能被浏览器解析成新标签（净化输出必须满足的不变量） */
function assertInert(html) {
    const rest = String(html).replace(/<\/?(?:b|strong|i|em|u|br|p|a|span|font)(?:\s[^<>]*)?\/?>/gi, '')
    expect(rest).not.toContain('<')
}

describe('页脚白名单净化：保留有限格式', () => {
    it('保留粗体/强调/下划线/换行等安全标签', () => {
        const out = sanitizeFooterHtml(
            '<b>粗体</b><strong>强调</strong><i>斜体</i><em>弱强调</em><u>下划线</u>第一行<br>第二行'
        )
        expect(out).toContain('<b>粗体</b>')
        expect(out).toContain('<strong>强调</strong>')
        expect(out).toContain('<i>斜体</i>')
        expect(out).toContain('<em>弱强调</em>')
        expect(out).toContain('<u>下划线</u>')
        expect(out).toContain('<br>')
        assertInert(out)
    })

    it('http(s) 链接保留并强制 rel=noopener noreferrer', () => {
        const out = sanitizeFooterHtml('<a href="https://example.com/a?x=1&y=2">量子官网</a>')
        expect(out).toContain('href="https://example.com/a?x=1&amp;y=2"')
        expect(out).toContain('rel="noopener noreferrer"')
        expect(out).toContain('量子官网')
        expect(sanitizeFooterHtml('<a href="http://ok.example">x</a>')).toContain('href="http://ok.example"')
        // 协议大小写不敏感
        expect(sanitizeFooterHtml('<a HREF="HTTPS://OK.EXAMPLE">x</a>')).toContain('href="HTTPS://OK.EXAMPLE"')
        assertInert(out)
    })

    it('span 白名单内样式保留，白名单外/危险值丢弃', () => {
        expect(sanitizeFooterHtml('<span style="color:#10b981">绿</span>')).toContain('style="color:#10b981"')
        expect(sanitizeFooterHtml('<font color="red">红</font>')).toContain('color="red"')
    })

    it('标签白名单外的标签只丢标签、保留可见文本', () => {
        expect(sanitizeFooterHtml('<div>段落文本</div>')).toBe('段落文本')
        expect(sanitizeFooterHtml('<h1>标题</h1>')).toBe('标题')
    })
})

describe('页脚白名单净化：脚本/事件/危险协议一律失效', () => {
    const payloads = [
        '<script>alert(1)</script>',
        '<ScRiPt>alert(1)</sCrIpT>',
        '<img src=x onerror=alert(1)>',
        '<img src=x ONERROR=alert(1)>',
        '<img src="x" onerror="alert(1)">',
        '<svg onload=alert(1)>',
        '<iframe src=//evil.example></iframe>',
        '<object data="data:text/html,<script>1</script>"></object>',
        '<embed src="//evil.example">',
        '<form action="//evil"><input onfocus="alert(1)" autofocus></form>',
        '<meta http-equiv="refresh" content="0;url=javascript:alert(1)">',
        '<base href="//evil.example/">',
        '<style>body{background:url(javascript:1)}</style>',
        '<a href="javascript:alert(1)">x</a>',
        '<a href="JaVaScRiPt:alert(1)">x</a>',
        '<a href="data:text/html,<script>1</script>">x</a>',
        '<a href="&#106;avascript:alert(1)">x</a>',
        '<a href="https://ok.example" onclick="alert(1)">x</a>',
        '<div style="background:url(javascript:1)">x</div>',
        '<span style="background:url(javascript:1)">x</span>',
        '<span style="color:expression(alert(1))">x</span>',
        '<span style="width:expression(alert(1))">x</span>',
        '<b style="background:url(javascript:1)">x</b>',
        '<font color="javascript:alert(1)">x</font>',
        // 原型链陷阱：白名单查表必须按自有属性判定，否则 <constructor> 会命中原型方法而抛 TypeError
        '<constructor>x</constructor>',
        '<!-- <script>alert(1)</script> -->',
        '<!DOCTYPE html><script>alert(1)</script>'
    ]

    it.each(payloads)('%s 被中和为惰性输出', (payload) => {
        const out = sanitizeFooterHtml(payload)
        assertInert(out)
        expect(out).not.toMatch(/alert/i)
        expect(out).not.toMatch(/<script|<img|<svg|<iframe|<object|<embed|<form|<style|onerror|onload|onfocus|onclick/i)
        expect(out).not.toMatch(/javascript:|expression\(|url\(/i)
    })
})

describe('页脚白名单净化：畸形输入与实体编码不得复活成标签', () => {
    it('裸标签注入（前面带引号/尖括号）按文本转义', () => {
        const out = sanitizeFooterHtml('"><script>1</script>')
        expect(out).not.toMatch(/<script/i)
        expect(out).toContain('"')
        assertInert(out)
    })

    it('实体编码的 <script> 保持文本惰性（实体解码不会在 innerHTML 里重新成标签）', () => {
        const out = sanitizeFooterHtml('&lt;script&gt;alert(1)&lt;/script&gt;')
        expect(out).toBe('&lt;script&gt;alert(1)&lt;/script&gt;')
        const numeric = sanitizeFooterHtml('&#60;img src=x onerror=alert(1)&#62;')
        expect(numeric).not.toContain('<')
    })

    it('未闭合/畸形标签按文本处理，不吞后续内容也不复活', () => {
        const out = sanitizeFooterHtml('<b>未闭合 <a href="https://ok">链接')
        expect(out).toContain('未闭合')
        expect(out).toContain('链接')
        expect(out).toContain('href="https://ok"')
        assertInert(out)
        assertInert(sanitizeFooterHtml('a <3 b < script > c'))
        assertInert(sanitizeFooterHtml('<a href="https://ok.example" <b>x</b>'))
    })

    it('拖尾未闭合的引号不得吞掉后续标签', () => {
        const out = sanitizeFooterHtml('<a title="未闭合><b>后面</b>')
        expect(out).toContain('后面')
        assertInert(out)
    })

    it('注释与声明整体丢弃（条件注释是历史 XSS 载体）', () => {
        expect(sanitizeFooterHtml('<!--[if IE]><script>alert(1)</script><![endif]-->可见文本')).toBe('可见文本')
    })

    it('非字符串/空值输入收敛为空串（脏配置不炸、不显示 [object Object]）', () => {
        expect(sanitizeFooterHtml(null)).toBe('')
        expect(sanitizeFooterHtml(undefined)).toBe('')
        expect(sanitizeFooterHtml('')).toBe('')
        expect(sanitizeFooterHtml(123)).toBe('')
        expect(sanitizeFooterHtml({ evil: 1 })).toBe('')
    })
})

describe('登录页渲染路径：只绑净化值', () => {
    let LoginPage

    beforeAll(async () => {
        LoginPage = (await import('@/view/login/login.vue')).default
    })

    it('SafeFooter 计算属性净化原始 Footer（危险载荷输出惰性）', () => {
        expect(typeof LoginPage.computed.SafeFooter).toBe('function')
        const hostile = '<b>量子助手</b><img src=x onerror=alert(1)><a href="javascript:alert(1)">x</a>'
        const rendered = LoginPage.computed.SafeFooter.call({ Footer: hostile })
        expect(rendered).toContain('<b>量子助手</b>')
        expect(rendered).not.toMatch(/<img|javascript:|onerror/i)
        assertInert(rendered)
    })

    it('login.vue 源码只对净化值 v-html（原始 Footer 不再直接进 v-html）', () => {
        const source = readFileSync(fileURLToPath(new URL('../src/view/login/login.vue', import.meta.url)), 'utf8')
        expect(source).not.toMatch(/v-html="Footer"/)
        expect(source).toMatch(/v-html="SafeFooter"/)
    })
})
