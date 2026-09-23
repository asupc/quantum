import { describe, expect, it } from 'vitest'
import { optionColor, parsePayload } from '@/utils/messagePayload'

describe('消息载荷解析（对齐 App MessagePayloadParser）', () => {
    it('完整载荷解析', () => {
        const payload = parsePayload(JSON.stringify({
            poster: ' https://x/y.jpg ',
            options: [
                { key: 'a', label: '选项一', reply: '回一', color: 'red', desc: '说明' }
            ]
        }))
        expect(payload.poster).toBe('https://x/y.jpg')
        expect(payload.options).toHaveLength(1)
        expect(payload.options[0]).toEqual({ key: 'a', label: '选项一', reply: '回一', color: 'red', desc: '说明' })
    })

    it('reply 缺省回落 key', () => {
        const payload = parsePayload(JSON.stringify({ options: [{ key: 'k1', label: '标签' }] }))
        expect(payload.options[0].reply).toBe('k1')
    })

    it('key 缺省回落 label', () => {
        const payload = parsePayload(JSON.stringify({ options: [{ label: '标签' }] }))
        expect(payload.options[0].key).toBe('标签')
    })

    it('未知颜色归一为空（渲染层按灰处理）', () => {
        const payload = parsePayload(JSON.stringify({ options: [{ label: 'x', color: 'pink' }] }))
        expect(payload.options[0].color).toBe('')
        expect(optionColor(payload.options[0].color)).toBe('#94A3B8')
        expect(optionColor('green')).toBe('#10B981')
    })

    it('缺 label 的选项被丢弃', () => {
        const payload = parsePayload(JSON.stringify({ options: [{ key: 'a' }, { label: 'ok' }] }))
        expect(payload.options).toHaveLength(1)
        expect(payload.options[0].label).toBe('ok')
    })

    it('损坏/空载荷返回 null（调用方降级渲染，绝不抛异常）', () => {
        expect(parsePayload('')).toBeNull()
        expect(parsePayload(null)).toBeNull()
        expect(parsePayload('{oops')).toBeNull()
        expect(parsePayload('[]')).toBeNull()
        expect(parsePayload(JSON.stringify({ other: 1 }))).toBeNull()
    })

    it('载荷根部 taskId 提取（服务端注入的来源任务 Id，点选代发透传用）', () => {
        const payload = parsePayload(JSON.stringify({
            taskId: ' task-1 ',
            poster: 'https://x/y.jpg',
            options: [{ key: 'a', label: '选项一' }]
        }))
        expect(payload.taskId).toBe('task-1')
    })

    it('仅 taskId（无海报无选项）也返回非 null，供点选取值', () => {
        expect(parsePayload(JSON.stringify({ taskId: 'task-2' })))
            .toEqual({ taskId: 'task-2', poster: '', options: [] })
        // taskId 非字符串/空白视为缺失
        expect(parsePayload(JSON.stringify({ taskId: '   ' }))).toBeNull()
        expect(parsePayload(JSON.stringify({ taskId: 123 }))).toBeNull()
    })

    it('旧载荷（无 taskId）taskId 为空串，兼容现状', () => {
        const payload = parsePayload(JSON.stringify({ poster: 'https://x/y.jpg' }))
        expect(payload.taskId).toBe('')
    })

    it('选项数量上限 20 条', () => {
        const options = Array.from({ length: 30 }, (_, i) => ({ label: `选项${i}` }))
        expect(parsePayload(JSON.stringify({ options })).options).toHaveLength(20)
    })
})
