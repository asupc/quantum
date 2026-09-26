import { describe, it, expect } from 'vitest'
import { sessionTitle } from '../src/utils/chatFormat.js'

// G-Push 会话标题优先级：外部会话展示名 > 任务名推导 > 原始会话键
describe('sessionTitle 外部会话标题映射', () => {
    it('外部会话优先用服务端下发的展示标题', () => {
        expect(sessionTitle('external:c1:ab12', {}, '机房监控')).toBe('机房监控')
    })

    it('空白标题回落既有规则，不显示成空', () => {
        expect(sessionTitle('T1', { T1: '晨报任务' }, '   ')).toBe('晨报任务')
        expect(sessionTitle('T1', {}, null)).toBe('T1')
    })

    it('默认会话语义不变', () => {
        expect(sessionTitle('', {}, '机房监控')).toBe('默认会话')
        expect(sessionTitle(null)).toBe('默认会话')
    })
})
