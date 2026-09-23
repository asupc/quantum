import { describe, expect, it } from 'vitest'
import { hasMarkup, MAX_MARKS, needFold, parseRich, SPAN_PLAIN, SPAN_STYLED, SPAN_LINK } from '@/utils/richText'

/**
 * 用例逐条移植自 App 端 quantum-app/core/common/src/test/.../RichTextTest.kt：
 * 两端解析规则必须完全一致，任何一侧改动都要同步这份用例。
 */
describe('富文本标记解析（与 App RichTextParser 对齐）', () => {
    it('纯文本无标记原样返回', () => {
        expect(parseRich('hello 量子')).toEqual([{ type: SPAN_PLAIN, text: 'hello 量子' }])
    })

    it('彩色文字标记', () => {
        expect(parseRich('{{red|警告}}')).toEqual([
            { type: SPAN_STYLED, text: '警告', color: 'red', pill: false }
        ])
    })

    it('胶囊标签标记', () => {
        expect(parseRich('{{tag:green|已监听}}')).toEqual([
            { type: SPAN_STYLED, text: '已监听', color: 'green', pill: true }
        ])
    })

    it('六色全部可识别', () => {
        const names = ['red', 'green', 'orange', 'blue', 'purple', 'gray']
        names.forEach((name) => {
            expect(parseRich(`{{${name}|x}}`)).toEqual([
                { type: SPAN_STYLED, text: 'x', color: name, pill: false }
            ])
        })
    })

    it('混合文本切分正确', () => {
        expect(parseRich('结果 {{red|失败}}，详见日志')).toEqual([
            { type: SPAN_PLAIN, text: '结果 ' },
            { type: SPAN_STYLED, text: '失败', color: 'red', pill: false },
            { type: SPAN_PLAIN, text: '，详见日志' }
        ])
    })

    it('未知颜色按字面显示', () => {
        expect(parseRich('{{pink|文字}}')).toEqual([{ type: SPAN_PLAIN, text: '{{pink|文字}}' }])
    })

    it('颜色名大小写敏感', () => {
        expect(parseRich('{{RED|x}}')).toEqual([{ type: SPAN_PLAIN, text: '{{RED|x}}' }])
    })

    it('未闭合按字面显示', () => {
        expect(parseRich('{{red|文字')).toEqual([{ type: SPAN_PLAIN, text: '{{red|文字' }])
    })

    it('空文本按字面显示', () => {
        expect(parseRich('{{red|}}')).toEqual([{ type: SPAN_PLAIN, text: '{{red|}}' }])
    })

    it('跨行标记不匹配按字面显示', () => {
        expect(parseRich('{{red|多行\n文字}}')).toEqual([{ type: SPAN_PLAIN, text: '{{red|多行\n文字}}' }])
    })

    it('嵌套花括号外层不匹配内层合法标记仍生效', () => {
        // 外层 {{red| 后紧跟 { 被文本段规则拒绝；内层 {{blue|x}} 是合法标记
        expect(parseRich('{{red|{{blue|x}}}}')).toEqual([
            { type: SPAN_PLAIN, text: '{{red|' },
            { type: SPAN_STYLED, text: 'x', color: 'blue', pill: false },
            { type: SPAN_PLAIN, text: '}}' }
        ])
    })

    it('超过上限的标记按字面显示', () => {
        const raw = Array.from({ length: 51 }, () => '{{red|t}}').join('')
        const spans = parseRich(raw)
        const styled = spans.filter((span) => span.type === SPAN_STYLED)
        expect(styled).toHaveLength(MAX_MARKS)
        // 第 51 个标记落回普通文本
        expect(spans[spans.length - 1].type).toBe(SPAN_PLAIN)
    })

    it('命名链接标记', () => {
        expect(parseRich('{{link:详情|https://www.zhihu.com/question/123}}')).toEqual([
            { type: SPAN_LINK, text: '详情', url: 'https://www.zhihu.com/question/123' }
        ])
    })

    it('命名链接与彩色文字混排', () => {
        expect(parseRich('1. 标题 {{link:详情|https://a.b/c}} {{orange|新}}')).toEqual([
            { type: SPAN_PLAIN, text: '1. 标题 ' },
            { type: SPAN_LINK, text: '详情', url: 'https://a.b/c' },
            { type: SPAN_PLAIN, text: ' ' },
            { type: SPAN_STYLED, text: '新', color: 'orange', pill: false }
        ])
    })

    it('命名链接 http 大小写不敏感', () => {
        expect(parseRich('{{link:x|HTTP://a.b}}')).toEqual([{ type: SPAN_LINK, text: 'x', url: 'HTTP://a.b' }])
    })

    it('命名链接非 http 协议按字面显示', () => {
        const raw = '{{link:点我|javascript:alert(1)}}'
        expect(parseRich(raw)).toEqual([{ type: SPAN_PLAIN, text: raw }])
    })

    it('命名链接未闭合按字面显示', () => {
        const raw = '{{link:详情|https://a.b'
        expect(parseRich(raw)).toEqual([{ type: SPAN_PLAIN, text: raw }])
    })

    it('link 不带冒号按颜色名解析为未知按字面显示', () => {
        const raw = '{{link|详情}}'
        expect(parseRich(raw)).toEqual([{ type: SPAN_PLAIN, text: raw }])
    })

    it('hasMarkup 快速判定', () => {
        expect(hasMarkup('a {{red|b}} c')).toBe(true)
        expect(hasMarkup('{{tag:orange|进行中}}')).toBe(true)
        expect(hasMarkup('{{link:详情|https://a.b}}')).toBe(true)
        expect(hasMarkup('纯文本')).toBe(false)
        expect(hasMarkup('{{unknown|x}}')).toBe(false)
        expect(hasMarkup('普通 { 花括号')).toBe(false)
    })

    it('hasMarkup 连续调用不受正则状态影响（g 标志陷阱）', () => {
        const raw = '{{red|a}}'
        expect(hasMarkup(raw)).toBe(true)
        expect(hasMarkup(raw)).toBe(true)
        expect(hasMarkup(raw)).toBe(true)
    })

    it('needFold 按换行数判定（阈值：换行 > 10 即 12 行起折叠）', () => {
        // 与 App 一致：统计换行符数量，> FOLD_LINES(10) 才折叠
        expect(needFold(Array.from({ length: 11 }, (_, i) => `line${i}`).join('\n'))).toBe(false)
        expect(needFold(Array.from({ length: 12 }, (_, i) => `line${i}`).join('\n'))).toBe(true)
        expect(needFold('单行')).toBe(false)
    })
})
