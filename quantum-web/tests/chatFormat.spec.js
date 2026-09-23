import dayjs from 'dayjs'
import { describe, expect, it } from 'vitest'
import {
    formatBytes,
    formatDuration,
    formatTimeLabel,
    sessionPreview,
    sessionTitle,
    shouldInsertTimeSeparator,
    stripMarkup
} from '@/utils/chatFormat'

describe('会话时间分隔（对齐 App 规则：间隔 > 5 分钟插入）', () => {
    const now = dayjs('2026-09-18 10:00:00', 'YYYY-MM-DD HH:mm:ss')

    it('时间分隔文案四档格式', () => {
        expect(formatTimeLabel('2026-09-18 09:05:00', now)).toBe('09:05')
        expect(formatTimeLabel('2026-09-17 21:47:00', now)).toBe('昨天 21:47')
        expect(formatTimeLabel('2026-06-01 08:30:00', now)).toBe('06-01 08:30')
        expect(formatTimeLabel('2025-12-31 23:59:00', now)).toBe('2025-12-31 23:59')
    })

    it('无法解析的时间不显示', () => {
        expect(formatTimeLabel('', now)).toBe('')
        expect(formatTimeLabel('not-a-time', now)).toBe('')
    })

    it('首条必插、间隔超 5 分钟才插', () => {
        expect(shouldInsertTimeSeparator(null, '2026-09-18 09:00:00')).toBe(true)
        expect(shouldInsertTimeSeparator('2026-09-18 09:00:00', '2026-09-18 09:05:00')).toBe(false)
        expect(shouldInsertTimeSeparator('2026-09-18 09:00:00', '2026-09-18 09:06:00')).toBe(true)
    })

    it('待发送气泡（无时间）不插分隔', () => {
        expect(shouldInsertTimeSeparator('2026-09-18 09:00:00', null)).toBe(false)
    })
})

describe('会话列表预览（对齐 App sessionPreview）', () => {
    it('媒体类型占位文案', () => {
        expect(sessionPreview({ ContentType: 'image', Content: 'xxx.jpg' })).toBe('[图片]')
        expect(sessionPreview({ ContentType: 'video', Content: 'xxx.mp4' })).toBe('[视频]')
        expect(sessionPreview({ ContentType: 'audio', Content: 'xxx.mp3' })).toBe('[音频]')
        expect(sessionPreview({ ContentType: 'file', Content: 'xxx.pdf' })).toBe('[文件]')
    })

    it('notify 取【标题】作为预览（与 App removePrefix/substringBefore 同规则）', () => {
        expect(sessionPreview({ ContentType: 'notify', Content: '【登录提醒】\n您的账号于 09:00 登录' }))
            .toBe('登录提醒')
    })

    it('notify 无【标题】时回退首行原文', () => {
        expect(sessionPreview({ ContentType: 'notify', Content: '纯文本通知\n第二行' })).toBe('纯文本通知')
    })

    it('文本取首行并剥离富文本标记（Web 增强）', () => {
        expect(sessionPreview({ ContentType: 'text', Content: '第一行 {{tag:green|已完成}}\n第二行' }))
            .toBe('第一行 已完成')
        expect(stripMarkup('{{red|红}}与{{blue|蓝}}')).toBe('红与蓝')
    })
})

describe('其他格式化', () => {
    it('时长 m:ss / h:mm:ss', () => {
        expect(formatDuration(64)).toBe('1:04')
        expect(formatDuration(3725)).toBe('1:02:05')
        expect(formatDuration(NaN)).toBe('--:--')
    })

    it('文件大小', () => {
        expect(formatBytes(512)).toBe('512 B')
        expect(formatBytes(2048)).toBe('2.0 KB')
        expect(formatBytes(10.3 * 1024 * 1024)).toBe('10.3 MB')
    })

    it('会话标题：任务名映射，映射不到回退会话键原文（自定义会话名本身可读）', () => {
        expect(sessionTitle('', {})).toBe('默认会话')
        expect(sessionTitle('abc', { abc: '每日晨报' })).toBe('每日晨报')
        // 会话键 = 任务 Id 且映射不到 → 显示原文（原「任务 前8位」回退已废弃）
        expect(sessionTitle('72ab92c3cd444851b21dd4ffde5a0998', {})).toBe('72ab92c3cd444851b21dd4ffde5a0998')
        // 会话键 = 任务配置的会话名 → 原文即可读
        expect(sessionTitle('影视聚合', {})).toBe('影视聚合')
    })
})
