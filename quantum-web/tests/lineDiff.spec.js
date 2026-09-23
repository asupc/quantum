import { describe, expect, it } from 'vitest'
import { diffLines, splitLines } from '../src/utils/lineDiff'

describe('splitLines', () => {
    it('统一换行符并忽略末尾空行', () => {
        expect(splitLines('a\r\nb\r\n')).toEqual(['a', 'b'])
        expect(splitLines('a\rb')).toEqual(['a', 'b'])
        expect(splitLines('a\nb')).toEqual(['a', 'b'])
    })

    it('空输入的边界', () => {
        expect(splitLines('')).toEqual([])
        expect(splitLines(null)).toEqual([])
        // 只有一个换行的文件 = 一行空行（与「a\n」= 一行内容 + 末尾换行 同口径）
        expect(splitLines('\n')).toEqual([''])
    })

    it('保留中间空行', () => {
        expect(splitLines('a\n\nb')).toEqual(['a', '', 'b'])
    })
})

describe('diffLines', () => {
    it('完全相同时无差异', () => {
        const result = diffLines('a\nb\nc', 'a\nb\nc')
        expect(result.added).toBe(0)
        expect(result.removed).toBe(0)
        expect(result.rows.every((n) => n.kind === 'same')).toBe(true)
    })

    it('尾部换行差异不算差异', () => {
        const result = diffLines('a\nb', 'a\nb\n')
        expect(result.added).toBe(0)
        expect(result.removed).toBe(0)
    })

    it('新增一行', () => {
        const result = diffLines('a\nc', 'a\nb\nc')
        expect(result.added).toBe(1)
        expect(result.removed).toBe(0)
        const added = result.rows.find((n) => n.kind === 'add')
        expect(added.text).toBe('b')
        expect(added.oldLine).toBeNull()
        expect(added.newLine).toBe(2)
    })

    it('删除一行', () => {
        const result = diffLines('a\nb\nc', 'a\nc')
        expect(result.added).toBe(0)
        expect(result.removed).toBe(1)
        const removed = result.rows.find((n) => n.kind === 'del')
        expect(removed.text).toBe('b')
        expect(removed.oldLine).toBe(2)
        expect(removed.newLine).toBeNull()
    })

    it('替换一行（至少 1 删 1 增）', () => {
        const result = diffLines('a\nb\nc', 'a\nB\nc')
        expect(result.added).toBe(1)
        expect(result.removed).toBe(1)
        // 行号分别跟踪新旧两侧：替换点之后 same 行两侧行号一致
        const tail = result.rows[result.rows.length - 1]
        expect(tail.kind).toBe('same')
        expect(tail.oldLine).toBe(3)
        expect(tail.newLine).toBe(3)
    })

    it('插入使两侧行号错位', () => {
        const result = diffLines('l1\nl2\nl3', 'l1\ninsert\nl2\nl3')
        const sameAfter = result.rows.filter((n) => n.kind === 'same')
        expect(sameAfter[0].oldLine).toBe(1)
        expect(sameAfter[0].newLine).toBe(1)
        expect(sameAfter[1].oldLine).toBe(2)
        expect(sameAfter[1].newLine).toBe(3)
    })

    it('空文件到有内容', () => {
        const result = diffLines('', 'a\nb')
        expect(result.added).toBe(2)
        expect(result.removed).toBe(0)
    })

    it('有内容到空文件', () => {
        const result = diffLines('a\nb', '')
        expect(result.added).toBe(0)
        expect(result.removed).toBe(2)
    })

    it('超大差异退化为整块替换且不抛出', () => {
        const oldText = Array.from({ length: 1600 }, (_, i) => `old ${i}`).join('\n')
        const newText = Array.from({ length: 1600 }, (_, i) => `new ${i}`).join('\n')
        const result = diffLines(oldText, newText)
        expect(result.truncated).toBe(true)
        expect(result.removed).toBe(1600)
        expect(result.added).toBe(1600)
    })
})
