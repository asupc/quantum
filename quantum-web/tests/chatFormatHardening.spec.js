/**
 * 会话展示格式化加固（对抗性审计 F2 + 媒体地址协议白名单）：
 *  - sessionPreview：Content 非字符串（数字/对象/数组/null）不得抛异常，一律按字符串口径取首行；
 *  - chatMedia.resolveMediaUrl / needsAuth：危险协议（javascript:/data:/file:）、协议相对地址（//evil）、
 *    blob:、空白填充、非字符串输入都不得产出可导航/可播放的地址。
 */
import { describe, expect, it, vi } from 'vitest'
import { sessionPreview } from '@/utils/chatFormat'

// chatMedia 经 @/api/app 拉到 axios/router/tools（node 环境无 document），
// 只取 DownloadUrl 的纯函数实现，切断整条 HTTP 依赖链（与 chatStore.spec.js 同做法）
vi.mock('@/api/app', () => ({ DownloadUrl: (fileId) => `/api/AppUpload/${fileId}` }))

import { needsAuth, resolveMediaUrl, isPlayable, isPlayableAudio, isPlayableVideo } from '@/utils/chatMedia'

// chatMedia 的 isOwnServerUrl 读 window.location，node 环境下打桩
vi.stubGlobal('window', { location: { origin: 'http://test.local' } })

describe('sessionPreview 脏 Content 兜底（F2）', () => {
    it('数字/对象/数组/null 一律不抛，按字符串口径取首行', () => {
        expect(() => sessionPreview({ ContentType: 'text', Content: { evil: 1 } })).not.toThrow()
        expect(sessionPreview({ ContentType: 'text', Content: 42 })).toBe('42')
        expect(sessionPreview({ ContentType: 'text', Content: ['一行', '二行'] })).toBe('一行,二行')
        expect(sessionPreview({ ContentType: 'text', Content: null })).toBe('')
        expect(sessionPreview({ ContentType: 'text', Content: undefined })).toBe('')
        // 含换行的对象会被 String() 压成 [object Object]，不得抛
        expect(sessionPreview({ ContentType: 'notify', Content: { t: 1 } })).toBe('[object Object]')
        // 非字符串首行同样要剥离富文本标记（与字符串路径同口径）
        expect(sessionPreview({ ContentType: 'text', Content: 42 })).not.toContain('{{')
    })
})

describe('chatMedia 协议白名单（危险地址不得成为可导航/可播地址）', () => {
    it('javascript:/data:/file:/blob: 一律不产出任何可用地址', () => {
        // 危险协议不落任何分支：既不得原样透传（<img src> 可执行/可导航），也不得拼成下载地址
        expect(resolveMediaUrl('javascript:alert(1)', 'image')).toBe('')
        expect(resolveMediaUrl('data:text/html,<script>1</script>', 'image')).toBe('')
        expect(resolveMediaUrl('data:image/png;base64,AAAA', 'image')).toBe('')
        expect(resolveMediaUrl('file:///etc/passwd', 'file')).toBe('')
        expect(resolveMediaUrl('blob:http://test.local/abc', 'image')).toBe('')
        expect(resolveMediaUrl('vbscript:msgbox(1)', 'image')).toBe('')
    })

    it('协议相对地址（//evil）不产出可用地址（不得绕开协议白名单）', () => {
        expect(resolveMediaUrl('//evil.example/x.png', 'image')).toBe('')
        expect(resolveMediaUrl('//evil.example/x.mp3', 'audio')).toBe('')
    })

    it('空白填充地址剥离后按实际内容判定（不得产出纯空白地址）', () => {
        expect(resolveMediaUrl('   ', 'image')).toBe('')
        expect(resolveMediaUrl('\n\t', 'text')).toBe('')
        expect(resolveMediaUrl('  https://ok.example/a.png  ', 'image')).toBe('https://ok.example/a.png')
        // 前导空白后跟危险协议同样被拦（白名单判定先 trim）
        expect(resolveMediaUrl('  javascript:alert(1)', 'image')).toBe('')
    })

    it('非字符串输入不得抛且不得产出可导航地址', () => {
        expect(() => resolveMediaUrl(42, 'image')).not.toThrow()
        expect(() => resolveMediaUrl({ evil: 1 }, 'image')).not.toThrow()
        expect(resolveMediaUrl(42, 'image')).toBe('')
        expect(resolveMediaUrl({ evil: 1 }, 'text')).toBe('')
        expect(resolveMediaUrl(null, 'image')).toBe('')
        expect(needsAuth(42)).toBe(false)
        expect(needsAuth({ evil: 1 })).toBe(false)
    })

    it('正常地址口径不变（外链/站内相对/裸 FileId）', () => {
        expect(resolveMediaUrl('https://ok.example/a.png', 'image')).toBe('https://ok.example/a.png')
        expect(resolveMediaUrl('HTTP://ok.example/a.png', 'image')).toBe('HTTP://ok.example/a.png')
        expect(resolveMediaUrl('api/AppUpload/x', 'image')).toBe('/api/AppUpload/x')
        expect(resolveMediaUrl('/api/AppMedia/y', 'image')).toBe('/api/AppMedia/y')
        expect(resolveMediaUrl('abc123def', 'image')).toBe('/api/AppUpload/abc123def')
        expect(resolveMediaUrl('abc123def', 'text')).toBe('')
        expect(needsAuth('/api/AppUpload/x')).toBe(true)
        expect(needsAuth('/api/AppMedia/y')).toBe(true)
        expect(needsAuth('  /api/AppUpload/x  ')).toBe(true)
        expect(needsAuth('https://ok.example/a.png')).toBe(false)
    })

    it('可播放判定只认白名单扩展名，危险/脏地址不进入播放分支', () => {
        expect(isPlayable('javascript:alert(1).mp3')).toBe(false)
        expect(isPlayableAudio('data:audio/mp3;base64,AAAA')).toBe(false)
        expect(isPlayableVideo('file:///x/y.mp4')).toBe(false)
        expect(isPlayable('https://ok.example/a.mp3')).toBe(true)
        expect(isPlayableVideo('https://ok.example/a.mp4')).toBe(true)
        expect(isPlayableAudio('/api/AppUpload/x.mp3?a=1#b')).toBe(true)
        expect(isPlayable('')).toBe(false)
    })
})
