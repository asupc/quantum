/**
 * axios 拦截器 200 信封契约加固（对抗性审计 F4）：
 *  - responseType:'blob' 的下载响应同样可能是 JSON 错误信封（服务端 401/业务异常时
 *    Content-Type 为 application/json）——必须嗅探后再放行，否则用户下载到一个装着
 *    {"Code":401} 的「文件」且永远不会被登出；
 *  - 非 200 HTTP 状态在成功回调里不得静默原样返回（应提示并 reject）。
 *
 * 测试不从真实网络走：直接取出拦截器注册的响应处理器调用（HttpRequest.interceptors 注册）。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'

// vi.mock 工厂被提升到文件顶部：共享引用必须经 vi.hoisted 提前建立
const { RouterPush, localStorageRemove } = vi.hoisted(() => ({
    RouterPush: vi.fn(),
    localStorageRemove: vi.fn()
}))
vi.mock('@/router/index.js', () => ({ default: { push: RouterPush } }))
vi.mock('@/libs/naive-discrete', () => ({
    message: { success: vi.fn(), warning: vi.fn(), error: vi.fn() },
    loadingBar: { start: vi.fn(), finish: vi.fn() }
}))
vi.mock('@/stores/chat', () => ({ useChatStore: () => ({ disconnect: vi.fn() }) }))

import HttpRequest from '@/libs/axios'
import { message } from '@/libs/naive-discrete'

vi.stubGlobal('localStorage', { getItem: vi.fn(() => null), setItem: vi.fn(), removeItem: localStorageRemove })

/** 取出拦截器注册的响应处理器（成功/失败两支） */
function interceptors() {
    const handlers = {}
    const instance = {
        interceptors: {
            request: { use: (ok) => { handlers.request = ok } },
            response: { use: (ok, err) => { handlers.response = ok; handlers.responseError = err } }
        }
    }
    new HttpRequest('').interceptors(instance, '/api/test')
    return handlers
}

function blobResponse(body, type) {
    return {
        config: { responseType: 'blob', noLoading: true },
        status: 200,
        data: new Blob([body], type ? { type } : undefined)
    }
}

// 处理器返回可能是同步对象（修复前）或 Promise（修复后）：统一包成 Promise，
// 断言「是否 reject / 是否原样放行」这一行为契约本身，而不是返回类型
const settle = (value) => Promise.resolve(value)

beforeEach(() => {
    vi.clearAllMocks()
})

describe('F4 blob 响应中的错误信封', () => {
    it('blob 体是 401 信封：清 token + 跳登录 + reject（不当作下载文件放行）', async () => {
        const response = blobResponse('{"Code":401,"Message":"Token验证失败"}', 'application/json')
        await expect(interceptors().response(response)).rejects.toBeTruthy()
        expect(localStorageRemove).toHaveBeenCalledWith('accessToken')
        expect(RouterPush).toHaveBeenCalledWith({ name: 'login' })
    })

    it('blob 体是业务错误信封（非 200 Code）：提示并 reject', async () => {
        const response = blobResponse('{"Code":500,"Message":"导出失败"}', 'application/json')
        await expect(settle(interceptors().response(response))).rejects.toBeTruthy()
        expect(message.error).toHaveBeenCalledWith('导出失败')
        expect(RouterPush).not.toHaveBeenCalled()
    })

    it('真实文件流（CSV/txt）原样放行——不误伤下载 happy path', async () => {
        const csv = blobResponse('a,b,c\n1,2,3', 'text/csv')
        await expect(settle(interceptors().response(csv))).resolves.toBe(csv)
        const txt = blobResponse('A=1\nB=2', 'text/plain')
        await expect(settle(interceptors().response(txt))).resolves.toBe(txt)
        expect(message.error).not.toHaveBeenCalled()
        expect(localStorageRemove).not.toHaveBeenCalled()
    })

    it('无 Content-Type 的小 blob 若体可解析为信封同样被拦（首字节兜底）', async () => {
        const response = blobResponse('{"Code":401,"Message":"未授权"}')
        await expect(settle(interceptors().response(response))).rejects.toBeTruthy()
        expect(RouterPush).toHaveBeenCalledWith({ name: 'login' })
    })
})

describe('F4b blob 信封嗅探按正文字节（声明 Content-Type 不可信）', () => {
    it('application/octet-stream + 401 信封体：按信封处置（清 token + 跳登录 + reject）', async () => {
        const response = blobResponse('{"Code":401,"Message":"Token验证失败"}', 'application/octet-stream')
        await expect(settle(interceptors().response(response))).rejects.toBeTruthy()
        expect(localStorageRemove).toHaveBeenCalledWith('accessToken')
        expect(RouterPush).toHaveBeenCalledWith({ name: 'login' })
    })

    it('text/xml 声明 + 业务错误信封体（任务导出 ExportAsync 的真实形态）：提示并 reject', async () => {
        const response = blobResponse('{"Code":500,"Message":"导出失败"}', 'text/xml')
        await expect(settle(interceptors().response(response))).rejects.toBeTruthy()
        expect(message.error).toHaveBeenCalledWith('导出失败')
        expect(RouterPush).not.toHaveBeenCalled()
    })

    it('信封体带 UTF-8 BOM / 前导空白时同样被识别', async () => {
        const response = blobResponse('\uFEFF\n  {"Code":401,"Message":"未授权"}  ', 'application/octet-stream')
        await expect(settle(interceptors().response(response))).rejects.toBeTruthy()
        expect(RouterPush).toHaveBeenCalledWith({ name: 'login' })
    })

    it('真实 text/xml 文档（首字节 <，非信封）原样放行', async () => {
        const xml = blobResponse('<?xml version="1.0" encoding="utf-8"?><tasks><task id="1"/></tasks>', 'text/xml')
        await expect(settle(interceptors().response(xml))).resolves.toBe(xml)
        expect(message.error).not.toHaveBeenCalled()
        expect(localStorageRemove).not.toHaveBeenCalled()
    })

    it('CSV 首格以 { 开头但整体非 JSON：原样放行（不得误当信封）', async () => {
        const csv = blobResponse('{name},age\n{Alice},30\n', 'text/csv')
        await expect(settle(interceptors().response(csv))).resolves.toBe(csv)
        expect(message.error).not.toHaveBeenCalled()
        expect(localStorageRemove).not.toHaveBeenCalled()
    })

    it('二进制 blob（zip/png 魔数）原样放行', async () => {
        const zip = blobResponse(new Uint8Array([0x50, 0x4b, 0x03, 0x04, 0x00, 0x00, 0x7b, 0x22, 0xff]), 'application/octet-stream')
        await expect(settle(interceptors().response(zip))).resolves.toBe(zip)
        const png = blobResponse(new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x7b]), 'image/png')
        await expect(settle(interceptors().response(png))).resolves.toBe(png)
    })

    it('超过 512KB 上限的大 blob 不嗅探（尺寸闸门保留）', async () => {
        const big = blobResponse('{"Code":401,"Message":"x"}' + ' '.repeat(600 * 1024), 'application/json')
        await expect(settle(interceptors().response(big))).resolves.toBe(big)
        expect(RouterPush).not.toHaveBeenCalled()
    })
})

describe('F4 非 blob 分支的既有契约（回归基线）', () => {
    it('Code=200 解出 Data；Code=401 登出；Code=500 提示并 reject', async () => {
        const handlers = interceptors()
        await expect(settle(handlers.response({ config: { noLoading: true }, status: 200, data: { Code: 200, Data: { ok: 1 } } })))
            .resolves.toEqual({ ok: 1 })
        await expect(settle(handlers.response({ config: { noLoading: true }, status: 200, data: { Code: 401, Message: '未授权' } })))
            .rejects.toBeTruthy()
        expect(RouterPush).toHaveBeenCalledWith({ name: 'login' })
        await expect(settle(handlers.response({ config: { noLoading: true }, status: 200, data: { Code: 500, Message: '业务失败' } })))
            .rejects.toBeTruthy()
        expect(message.error).toHaveBeenCalledWith('业务失败')
    })

    it('非 200 HTTP 状态在成功回调里提示并 reject（不得静默原样返回）', async () => {
        const response = { config: { noLoading: true }, status: 500, data: { Code: 500 } }
        await expect(settle(interceptors().response(response))).rejects.toBeTruthy()
        expect(message.error).toHaveBeenCalled()
    })
})
