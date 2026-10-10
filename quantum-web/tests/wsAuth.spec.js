/**
 * /ws/app 握手鉴权与重连策略回归（对抗性审计 F5）：
 *  - 令牌不再进 URL query（此前 `?token=<jwt>` 会被代理/访问日志留存），改走服务端已支持的
 *    首帧 `{"type":"auth","token":"<jwt>"}`（无 token query 时服务端升级后等首帧，见
 *    Quantum.Web/Middleware/AppWebSocketMiddleware.cs：token 留空允许升级、30s 内未鉴权即断）；
 *  - 鉴权被拒（服务端先发 error「token 无效或已过期，请重新登录。」再以 1008 PolicyViolation 关闭，
 *    或 1008 在升级前到达）：必须清令牌 + 断会话 + 回登录页（与 REST 401 同一份登出实现），
 *    且**不再重连**——旧实现是无限退避重试同一个死令牌，登出完全依赖 REST 拦截器；
 *  - 传输层失败（从未 open 的 1006，如后端重启/网络中断）不是凭据故障：保持退避重连、不得登出；
 *  - 无 token query 时服务端固定先下发一条「请首帧鉴权」提示帧：属协议内噪声，不得外抛到视图层。
 *
 * 环境为 node：window / localStorage / WebSocket 全部打桩，router 与会话 store 以 spy 替身接入
 * （登出逻辑收敛在 @/libs/authSession，axios 拦截器与 WS 共用同一实现）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const h = vi.hoisted(() => ({
    push: vi.fn(),
    disconnect: vi.fn(),
    removeItem: vi.fn(),
    store: { accessToken: 'Bearer test-jwt' }
}))

vi.mock('@/router/index.js', () => ({ default: { push: h.push } }))
vi.mock('@/stores/chat', () => ({ useChatStore: () => ({ disconnect: h.disconnect }) }))

// 打桩 WebSocket：测试自行驱动 open/message/close 时序，并记录 URL 与上行帧
class FakeWebSocket {
    static CONNECTING = 0
    static OPEN = 1
    static CLOSING = 2
    static CLOSED = 3
    static instances = []

    constructor(url) {
        this.url = url
        this.readyState = FakeWebSocket.CONNECTING
        this.sent = []
        FakeWebSocket.instances.push(this)
    }

    send(data) {
        this.sent.push(data)
    }

    close() {
        this.readyState = FakeWebSocket.CLOSED
    }

    // —— 测试侧驱动 ——
    open() {
        this.readyState = FakeWebSocket.OPEN
        this.onopen?.()
    }

    message(frame) {
        this.onmessage?.({ data: typeof frame === 'string' ? frame : JSON.stringify(frame) })
    }

    fail(code = 1006) {
        this.readyState = FakeWebSocket.CLOSED
        this.onclose?.({ code })
    }
}

vi.stubGlobal('WebSocket', FakeWebSocket)
vi.stubGlobal('localStorage', {
    getItem: (key) => (key === 'accessToken' ? h.store.accessToken : null),
    setItem: (key, value) => {
        if (key === 'accessToken') h.store.accessToken = value
    },
    removeItem: (key) => {
        h.removeItem(key)
        if (key === 'accessToken') h.store.accessToken = null
    }
})

import { AppSocket, WS_STATE } from '@/libs/ws'

/** 服务端首帧鉴权失败的两条信号（与 AppWebSocketMiddleware.cs 文案/关闭码逐字对齐） */
const TOKEN_FAILURE_FRAME = { type: 'error', content: 'token 无效或已过期，请重新登录。' }
const PRE_AUTH_PROMPT = { type: 'error', content: '未鉴权：请发送 {"type":"auth","token":"<jwt>"} 完成登录。' }
const AUTH_FAILURE_CLOSE_CODE = 1008 // WebSocketCloseStatus.PolicyViolation

const last = () => FakeWebSocket.instances[FakeWebSocket.instances.length - 1]

/** 让 clearLoginSession 内部 bustChatSession 的动态 import 微任务落地（会话 store 断连是异步委托）。
 *  假定时器下模块加载由运行时的宏任务调度驱动，纯 Promise 微任务冲刷不足以让 import 结算。 */
const flush = async () => {
    await vi.advanceTimersByTimeAsync(0)
}

beforeEach(() => {
    vi.useFakeTimers()
    vi.clearAllMocks()
    FakeWebSocket.instances = []
    h.store.accessToken = 'Bearer test-jwt'
    vi.stubGlobal('window', { location: { protocol: 'http:', host: 'test.local' } })
})

afterEach(() => {
    vi.useRealTimers()
})

describe('F5 WS 握手：令牌不进 URL，首帧完成鉴权', () => {
    it('构造的 URL 不含 token（裸 /ws/app），https 下用 wss', () => {
        const client = new AppSocket()
        client.connect('jwt-abc')
        expect(last().url).toBe('ws://test.local/ws/app')
        expect(last().url).not.toContain('token=')

        vi.stubGlobal('window', { location: { protocol: 'https:', host: 'test.local' } })
        const secure = new AppSocket()
        secure.connect('jwt-abc')
        expect(last().url).toBe('wss://test.local/ws/app')
    })

    it('onopen 后第一帧即 auth（去掉 Bearer 前缀），早于任何心跳帧', () => {
        const client = new AppSocket()
        client.connect('Bearer jwt-abc')
        const ws = last()
        ws.open()
        expect(ws.sent.map((raw) => JSON.parse(raw))).toEqual([{ type: 'auth', token: 'jwt-abc' }])

        vi.advanceTimersByTime(25000)
        expect(ws.sent.map((raw) => JSON.parse(raw))).toEqual([
            { type: 'auth', token: 'jwt-abc' },
            { type: 'ping' }
        ])
    })

    it('未登录（无令牌）不就绪连接', () => {
        const client = new AppSocket()
        client.connect('')
        expect(FakeWebSocket.instances).toHaveLength(0)
        expect(client.state).toBe(WS_STATE.CLOSED)
    })

    it('服务端「请首帧鉴权」提示帧不外抛视图层（协议内噪声）', () => {
        const onFrame = vi.fn()
        const client = new AppSocket({ onFrame })
        client.connect('jwt-abc')
        last().open()
        last().message(PRE_AUTH_PROMPT)
        expect(onFrame).not.toHaveBeenCalled()
        expect(h.push).not.toHaveBeenCalled()
    })
})

describe('F5 鉴权被拒：登出一次并停止重连', () => {
    it('error「token 无效或已过期」+ 1008 关闭：清令牌/断会话/回登录，且不再调度重连', async () => {
        const client = new AppSocket()
        const scheduleReconnect = vi.spyOn(client, 'scheduleReconnect')
        client.connect('jwt-expired')
        const ws = last()
        ws.open()
        ws.message(TOKEN_FAILURE_FRAME)
        // 鉴权失败帧本身不外抛（不再重复告警：处置已由登出承接）
        expect(scheduleReconnect).not.toHaveBeenCalled()
        ws.fail(AUTH_FAILURE_CLOSE_CODE)
        await flush()

        expect(h.removeItem).toHaveBeenCalledWith('accessToken')
        expect(h.store.accessToken).toBeNull()
        expect(h.disconnect).toHaveBeenCalledTimes(1)
        expect(h.push).toHaveBeenCalledTimes(1)
        expect(h.push).toHaveBeenCalledWith({ name: 'login' })

        // 不再重连：无待执行定时器，推进到超过最大退避也无新连接；重复的关闭回调也幂等
        expect(scheduleReconnect).not.toHaveBeenCalled()
        expect(vi.getTimerCount()).toBe(0)
        ws.fail(AUTH_FAILURE_CLOSE_CODE)
        vi.advanceTimersByTime(120000)
        await flush()
        expect(FakeWebSocket.instances).toHaveLength(1)
        expect(h.push).toHaveBeenCalledTimes(1)
        expect(h.disconnect).toHaveBeenCalledTimes(1)
    })

    it('升级前即被拒（从未 open 的 1008）：同样登出并停重连', async () => {
        const client = new AppSocket()
        const scheduleReconnect = vi.spyOn(client, 'scheduleReconnect')
        client.connect('jwt-rejected')
        last().fail(AUTH_FAILURE_CLOSE_CODE)
        await flush()

        expect(h.push).toHaveBeenCalledTimes(1)
        expect(h.disconnect).toHaveBeenCalledTimes(1)
        expect(scheduleReconnect).not.toHaveBeenCalled()
        vi.advanceTimersByTime(120000)
        expect(FakeWebSocket.instances).toHaveLength(1)
    })
})

describe('F5 传输层失败仍保留重连', () => {
    it('已连接会话正常断开（1006）不登出，按退避重连', () => {
        const client = new AppSocket()
        client.connect('jwt-ok')
        const ws = last()
        ws.open()
        ws.fail(1006)

        expect(h.push).not.toHaveBeenCalled()
        expect(h.removeItem).not.toHaveBeenCalled()
        expect(FakeWebSocket.instances).toHaveLength(1)

        vi.advanceTimersByTime(1000)
        expect(FakeWebSocket.instances).toHaveLength(2)
        // 重连仍走首帧鉴权（令牌从 localStorage 现取）
        last().open()
        expect(JSON.parse(last().sent[0])).toEqual({ type: 'auth', token: 'test-jwt' })
    })

    it('从未 open 的 1006（后端重启/网络中断）不登出——凭据可能仍然有效，继续退避重连', () => {
        const client = new AppSocket()
        client.connect('jwt-ok')
        last().fail(1006)

        expect(h.push).not.toHaveBeenCalled()
        expect(h.removeItem).not.toHaveBeenCalled()
        vi.advanceTimersByTime(1000)
        expect(FakeWebSocket.instances).toHaveLength(2)
    })

    it('主动 close() 后不再重连（manualClose 语义保持）', () => {
        const client = new AppSocket()
        client.connect('jwt-ok')
        const ws = last()
        ws.open()
        client.close()
        ws.fail(1006)

        expect(h.push).not.toHaveBeenCalled()
        vi.advanceTimersByTime(120000)
        expect(FakeWebSocket.instances).toHaveLength(1)
    })
})
