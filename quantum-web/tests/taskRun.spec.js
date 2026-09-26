import { describe, it, expect } from 'vitest'
import {
    runStatusMeta,
    triggerSourceLabel,
    formatElapsed,
    formatUtcToLocal,
    summaryOr,
    clampPolicy,
    backoffSecondsFor,
    PolicyLimits
} from '../src/utils/taskRun.js'

// 一期 G2 Web 侧执行记录展示口径：颜色只代表执行终态，不代表 HTTP 请求成功
describe('taskRun 执行记录展示口径', () => {
    it('每个终态都有中文标签与色调', () => {
        expect(runStatusMeta('Succeeded').label).toBe('成功')
        expect(runStatusMeta('Rejected').tone).toBe('danger')
        expect(runStatusMeta('Canceled').tone).toBe('warn')
        // 中断/未知不得被画成成功色（结果不可知 ≠ 跑成功）
        expect(runStatusMeta('Interrupted').tone).not.toBe('ok')
    })

    it('未知状态原样回显而不假装成功', () => {
        const meta = runStatusMeta('Weird')
        expect(meta.label).toBe('Weird')
        expect(meta.tone).toBe('muted')
        expect(runStatusMeta(null).label).toBe('未知')
    })

    it('触发源标签覆盖六类入口', () => {
        expect(triggerSourceLabel('Shadow')).toBe('AI 试运行')
        expect(triggerSourceLabel('Retry')).toBe('自动重试')
        expect(triggerSourceLabel('Cron')).toBe('定时')
        expect(triggerSourceLabel('')).toBe('-')
    })

    it('耗时缺失显示为占位而不是 0 秒', () => {
        expect(formatElapsed(null)).toBe('-')
        expect(formatElapsed(undefined)).toBe('-')
        expect(formatElapsed(-5)).toBe('-')
        expect(formatElapsed(850)).toBe('850 毫秒')
        expect(formatElapsed(65_000)).toBe('1 分 5 秒')
    })

    it('UTC 串按本地时区渲染', () => {
        const text = formatUtcToLocal('2026-09-26T00:00:00Z')
        expect(text).toMatch(/^2026\/09\/2[56] /)
        expect(formatUtcToLocal('2026-09-26T00:00:00')).toMatch(/2026/)
        expect(formatUtcToLocal('')).toBe('-')
        expect(formatUtcToLocal('not-a-date')).toBe('not-a-date')
    })

    it('无摘要时回落到终态名而不是空白', () => {
        expect(summaryOr('Failed', '  脚本抛异常  ')).toBe('脚本抛异常')
        expect(summaryOr('Failed', null)).toBe('执行异常')
        expect(summaryOr('Running', '')).toBe('执行中')
    })

    it('策略输入按服务端口径 clamp', () => {
        const clamped = clampPolicy({
            RetryCount: 99,
            BackoffSeconds: 5,
            AlertAfterConsecutiveFailures: 0,
            CooldownMinutes: 99999,
            Enabled: 'yes',
            SendRecovery: 0
        })
        expect(clamped.RetryCount).toBe(PolicyLimits.RetryCount[1])
        expect(clamped.BackoffSeconds).toBe(PolicyLimits.BackoffSeconds[0])
        expect(clamped.AlertAfterConsecutiveFailures).toBe(1)
        expect(clamped.CooldownMinutes).toBe(PolicyLimits.CooldownMinutes[1])
        expect(clamped.Enabled).toBe(true)
        expect(clamped.SendRecovery).toBe(false)
        expect(clampPolicy(null).RetryCount).toBe(0)
    })

    it('指数退避封顶 3600 秒', () => {
        expect(backoffSecondsFor(60, 1)).toBe(60)
        expect(backoffSecondsFor(60, 2)).toBe(120)
        expect(backoffSecondsFor(3600, 4)).toBe(3600)
        expect(backoffSecondsFor(0, 1)).toBe(30)
    })
})
