/**
 * 会话 store 抗污染加固回归（对抗性审计 F2/F3/F6/F7/F12）：
 *  - F2：非字符串 Content 不得崩溃，也不得中断补拉页 / 污染游标（脏行跳过、其余行照常生效）；
 *  - F3：同一 MsgId 重复送达（WS 帧 + 补拉页重复）只计一次 Total/Unread；
 *  - F6：openSession 的旧响应在用户切走会话后不得推进水位 / 上报已读；
 *  - F7：脏 seq（字符串 / 对象）不得把 syncCursor 污染成 NaN；
 *  - F12：列表载荷非数组时收敛为空列表而不是崩溃。
 *
 * 测试基建与 chatStore.spec.js 一致（node 环境，模块级 mock；window/localStorage 打桩）。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

vi.mock('@/libs/naive-discrete', () => ({
    message: { success: vi.fn(), warning: vi.fn(), error: vi.fn() }
}))
vi.mock('@/libs/ws', () => ({
    AppSocket: class {
        static rawToken() {
            return 'test-token'
        }
        constructor(opts) {
            this.opts = opts
        }
        connect() { }
        close() { }
        send(frame) {
            ;(globalThis.__wsFrames = globalThis.__wsFrames || []).push(frame)
            return true
        }
    },
    WS_STATE: { CLOSED: 'closed', CONNECTING: 'connecting', OPEN: 'open' }
}))
vi.mock('@/utils/chatMedia', () => ({ clearMediaCache: vi.fn() }))
vi.mock('@/api/task', () => ({ GetTask: vi.fn() }))
vi.mock('@/api/app', () => ({
    SyncMessages: vi.fn(),
    SessionsOverview: vi.fn(),
    SessionsRead: vi.fn(),
    SessionMessages: vi.fn(),
    MarkRead: vi.fn(),
    DeleteSession: vi.fn(),
    ClearSession: vi.fn(),
    SubmitCommand: vi.fn(),
    NotificationList: vi.fn()
}))

import { useChatStore } from '@/stores/chat'
import { DeleteSession, SessionMessages, SessionsOverview, SessionsRead, SubmitCommand, SyncMessages } from '@/api/app'

vi.stubGlobal('window', { location: { origin: 'http://test.local' } })
vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn() })

/** 造一条「会话列表已加载 + 空消息窗口」的可计数据话形态 */
function seedSession(store, sessionKey = 'grp', currentKey = 'other') {
    store.currentKey = currentKey
    store.sessions = [{ SessionKey: sessionKey, Total: 0, Unread: 0, Last: { Seq: 0 } }]
    store.messagesBySession = { [sessionKey]: [] }
}

beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
    globalThis.__wsFrames = []
})

afterEach(() => {
    vi.useRealTimers()
    useChatStore().disconnect()
})

describe('F2 非字符串 Content（脏帧）', () => {
    it('applyMessage 收到数字/对象 Content 不抛异常，仍落行并推进游标', () => {
        const store = useChatStore()
        seedSession(store)
        expect(() =>
            store.applyMessage({ Seq: 42, MsgId: 'm42', Direction: 1, Content: 42, ContentType: 'text', SessionKey: 'grp' })
        ).not.toThrow()
        expect(() =>
            store.applyMessage({ Seq: 43, MsgId: 'm43', Direction: 1, Content: { hostile: true }, ContentType: 'text', SessionKey: 'grp' })
        ).not.toThrow()
        expect(store.messagesBySession.grp).toHaveLength(2)
        expect(store.syncCursor).toBe(43)
        // 预览（withPreview → sessionPreview）同样不得被脏 Content 带崩：
        // 最后一条脏行（对象）落为字符串预览，全程无异常
        expect(store.sessions[0].previewText).toBe('[object Object]')
    })

    it('补拉页含一条毒行时整页仍生效、游标推进到页尾（不进入无限重拉）', async () => {
        SyncMessages.mockResolvedValue({
            Data: [
                { Seq: 7, MsgId: 'm7', Direction: 1, Content: '正常', ContentType: 'text', SessionKey: 'grp' },
                { Seq: 8, MsgId: 'm8', Direction: 1, Content: { evil: 1 }, ContentType: 'text', SessionKey: 'grp' },
                { Seq: 9, MsgId: 'm9', Direction: 1, Content: 9, ContentType: 'text', SessionKey: 'grp' }
            ],
            MaxSeq: 9
        })
        const store = useChatStore()
        seedSession(store)
        store.syncCursor = 6

        await expect(store.catchUp()).resolves.toBeUndefined()

        expect(store.messagesBySession.grp.map((r) => r.Seq)).toEqual([7, 8, 9])
        expect(store.syncCursor).toBe(9)
        expect(SyncMessages).toHaveBeenCalledTimes(1)
    })

    it('某行在应用期意外抛错时该行跳过、其余行照常生效且游标推进（循环级防御）', async () => {
        const hostile = { Seq: 8, MsgId: 'm8', Direction: 1, Content: '毒', ContentType: 'text', SessionKey: 'grp' }
        // 取值即抛脏行（模拟任何未预期的行形态在 decorate/插入期炸掉）
        Object.defineProperty(hostile, 'Payload', {
            get() {
                throw new Error('hostile row')
            }
        })
        SyncMessages.mockResolvedValue({
            Data: [
                { Seq: 7, MsgId: 'm7', Direction: 1, Content: '正常', ContentType: 'text', SessionKey: 'grp' },
                hostile,
                { Seq: 9, MsgId: 'm9', Direction: 1, Content: '正常2', ContentType: 'text', SessionKey: 'grp' }
            ],
            MaxSeq: 9
        })
        const store = useChatStore()
        seedSession(store)
        store.syncCursor = 6

        await store.catchUp()

        expect(store.messagesBySession.grp.map((r) => r.MsgId)).toEqual(['m7', 'm9'])
        expect(store.syncCursor).toBe(9)
    })
})

describe('F3 重复送达（WS 帧 + 补拉页）只计一次', () => {
    it('同一 MsgId 两次送达：Total/Unread 各计一次、列表只有一行', () => {
        const store = useChatStore()
        seedSession(store)
        const row = { Seq: 7, MsgId: 'm7', Direction: 1, Content: '你好', ContentType: 'text', SessionKey: 'grp' }

        store.applyMessage({ ...row })
        store.applyMessage({ ...row })

        const entry = () => store.sessions.find((s) => s.SessionKey === 'grp')
        expect(store.messagesBySession.grp).toHaveLength(1)
        expect(entry().Total).toBe(1)
        expect(entry().Unread).toBe(1)
    })

    it('乐观气泡被服务端回显替换只计一次（既不多计也不漏计）', async () => {
        SubmitCommand.mockResolvedValue({ Accepted: true })
        const store = useChatStore()
        store.currentKey = 'grp'
        store.sessions = [{ SessionKey: 'grp', Total: 0, Unread: 0, Last: { Seq: 0 } }]
        store.messagesBySession = { grp: [] }

        await store.send('你好')
        const entry = () => store.sessions.find((s) => s.SessionKey === 'grp')
        expect(entry().Total).toBe(0) // pending 乐观行不计入

        const echo = { Seq: 8, MsgId: 'm8', Direction: 2, Content: '你好', ContentType: 'text', SessionKey: 'grp' }
        store.applyMessage({ ...echo })
        expect(entry().Total).toBe(1)
        store.applyMessage({ ...echo })
        expect(entry().Total).toBe(1)
    })
})

describe('F3b 重复送达去重与窗口加载状态无关（后台会话）', () => {
    /** 造「会话列表已加载、但该会话窗口从未在本端打开」的形态（messagesBySession 无该键） */
    function seedUnloadedSession(store, sessionKey = 'grp') {
        store.currentKey = 'other'
        store.sessions = [{ SessionKey: sessionKey, Total: 0, Unread: 0, Last: { Seq: 0 } }]
        store.messagesBySession = {}
    }

    it('窗口未加载（messagesBySession 无该键）时同一 MsgId 两次送达仍只计一次', () => {
        const store = useChatStore()
        seedUnloadedSession(store)
        const row = { Seq: 7, MsgId: 'm7', Direction: 1, Content: 'x', ContentType: 'text', SessionKey: 'grp' }

        store.applyMessage({ ...row })
        store.applyMessage({ ...row })

        const entry = () => store.sessions.find((s) => s.SessionKey === 'grp')
        expect(entry().Total).toBe(1)
        expect(entry().Unread).toBe(1)
    })

    it('WS 帧 + 补拉页携带同一 msgId（窗口未加载）只计一次 Total/Unread（端到端）', async () => {
        const row = { Seq: 7, MsgId: 'm7', Direction: 1, Content: 'x', ContentType: 'text', SessionKey: 'grp' }
        SyncMessages.mockResolvedValue({ Data: [{ ...row }], MaxSeq: 7 })
        const store = useChatStore()
        seedUnloadedSession(store)

        store.handleFrame({ type: 'message', msgId: 'm7', seq: 7, content: 'x', contentType: 'text', direction: 1, session: 'grp' })
        await store.catchUp()

        const entry = () => store.sessions.find((s) => s.SessionKey === 'grp')
        expect(entry().Total).toBe(1)
        expect(entry().Unread).toBe(1)
    })

    it('窗口已加载时去重口径不变（回归基线）；乐观气泡回显照旧只计一次', () => {
        const store = useChatStore()
        seedSession(store)
        const row = { Seq: 7, MsgId: 'm7', Direction: 1, Content: 'x', ContentType: 'text', SessionKey: 'grp' }
        store.applyMessage({ ...row })
        store.applyMessage({ ...row })
        const entry = () => store.sessions.find((s) => s.SessionKey === 'grp')
        expect(store.messagesBySession.grp).toHaveLength(1)
        expect(entry().Total).toBe(1)
    })

    it('同一 MsgId 在窗口后加载（openSession 首屏）时也不重复计数', async () => {
        const row = { Seq: 7, MsgId: 'm7', Direction: 1, Content: 'x', ContentType: 'text', SessionKey: 'grp' }
        SessionMessages.mockResolvedValue({ Messages: [{ ...row }], HasMore: false })
        const store = useChatStore()
        seedUnloadedSession(store)
        store.applyMessage({ ...row })

        await store.openSession('grp')

        const entry = () => store.sessions.find((s) => s.SessionKey === 'grp')
        expect(store.messagesBySession.grp.filter((r) => r.MsgId === 'm7')).toHaveLength(1)
        expect(entry().Total).toBe(1)
    })

    it('账本有界（每会话 500）：淘汰只影响最旧的行，近端重复仍被拦', () => {
        const store = useChatStore()
        seedUnloadedSession(store)
        for (let i = 1; i <= 700; i++) {
            store.applyMessage({ Seq: i, MsgId: `m${i}`, Direction: 1, Content: 'x', ContentType: 'text', SessionKey: 'grp' })
        }

        expect(store.seenMsgIds.grp.size).toBe(500)
        expect(store.seenMsgIds.grp.has('m1')).toBe(false) // FIFO 淘汰最旧
        expect(store.seenMsgIds.grp.has('m700')).toBe(true)

        const entry = () => store.sessions.find((s) => s.SessionKey === 'grp')
        const before = entry().Total
        store.applyMessage({ Seq: 700, MsgId: 'm700', Direction: 1, Content: 'x', ContentType: 'text', SessionKey: 'grp' })
        expect(entry().Total).toBe(before)
    })

    it('窗口未加载时乐观气泡的服务端回显照旧计入一次（不得被账本误当成重复）', async () => {
        SubmitCommand.mockResolvedValue({ Accepted: true })
        const store = useChatStore()
        store.currentKey = 'grp'
        store.sessions = [{ SessionKey: 'grp', Total: 0, Unread: 0, Last: { Seq: 0 } }]
        store.messagesBySession = { grp: [] }

        await store.send('hello')
        const entry = () => store.sessions.find((s) => s.SessionKey === 'grp')
        expect(entry().Total).toBe(0) // pending 乐观行不计入

        // 模拟切走会话后窗口被清（后台态）：回显到达时须以实体化行计入一次
        store.messagesBySession = {}
        store.currentKey = 'other'
        store.applyMessage({ Seq: 8, MsgId: 'm8', Direction: 2, Content: 'hello', ContentType: 'text', SessionKey: 'grp' })
        expect(entry().Total).toBe(1)
        // 同一回显重复送达仍只有一次
        store.applyMessage({ Seq: 8, MsgId: 'm8', Direction: 2, Content: 'hello', ContentType: 'text', SessionKey: 'grp' })
        expect(entry().Total).toBe(1)
    })
})

describe('F7b deleteSession 的 MaxSeq 归一（NaN 防污染）', () => {
    it('MaxSeq 为对象/字符串/布尔时 syncCursor 保持有限数（不被毒化成 NaN）', async () => {
        const store = useChatStore()
        for (const maxSeq of [{}, 'abc', null, undefined, NaN]) {
            DeleteSession.mockResolvedValue({ Deleted: true, MaxSeq: maxSeq })
            store.sessions = [{ SessionKey: 'grp', Total: 1, Unread: 0, Last: { Seq: 8 } }]
            store.messagesBySession = { grp: [] }
            store.syncCursor = 20

            await store.deleteSession('grp')

            expect(Number.isFinite(store.syncCursor)).toBe(true)
            // 域外 MaxSeq 归 0（= 无语义）→ 不调整游标（按「无序号不推进」口径，而非清零重拉）
            expect(store.syncCursor).toBe(20)
        }
    })

    it('MaxSeq 为真值时按数字语义回拨（true → 1），且仍为有限数', async () => {
        DeleteSession.mockResolvedValue({ Deleted: true, MaxSeq: true })
        const store = useChatStore()
        store.sessions = [{ SessionKey: 'grp', Total: 1, Unread: 0, Last: { Seq: 8 } }]
        store.messagesBySession = { grp: [] }
        store.syncCursor = 5

        await store.deleteSession('grp')

        expect(store.syncCursor).toBe(1)
    })

    it('MaxSeq 为正常数时游标回拨仍按 min 口径（不误伤兼容语义）', async () => {
        const store = useChatStore()
        store.sessions = [{ SessionKey: 'grp', Total: 1, Unread: 0, Last: { Seq: 8 } }]
        store.messagesBySession = { grp: [] }

        DeleteSession.mockResolvedValue({ Deleted: true, MaxSeq: 5 })
        store.syncCursor = 20
        await store.deleteSession('grp')
        expect(store.syncCursor).toBe(5)

        // 超出当前游标的正常值同样按 min 处理，游标不前进
        DeleteSession.mockResolvedValue({ Deleted: true, MaxSeq: 1e21 })
        store.syncCursor = 20
        await store.deleteSession('grp')
        expect(Number.isFinite(store.syncCursor)).toBe(true)
        expect(store.syncCursor).toBe(20)
    })
})

describe('F6 openSession 请求代际防御（切会话竞态）', () => {
    it('切走后旧会话响应不得推进 A 的水位、不得上报已读（数据仍缓存）', async () => {
        vi.useFakeTimers()
        SessionsRead.mockResolvedValue({ Advanced: [] })
        let resolveA
        SessionMessages.mockImplementation((key) =>
            key === 'A'
                ? new Promise((resolve) => {
                    resolveA = resolve
                })
                : Promise.resolve({ Messages: [], HasMore: false })
        )
        const store = useChatStore()

        const openA = store.openSession('A')
        await store.openSession('B')
        resolveA({
            Messages: [{ Seq: 99, MsgId: 'a99', Direction: 1, Content: 'hi', ContentType: 'text', SessionKey: 'A' }],
            HasMore: false
        })
        await openA

        expect(store.currentKey).toBe('B')
        expect(store.messagesBySession.A).toHaveLength(1) // 数据照常缓存，供切回时使用
        expect(store.readSeqs.A).toBeUndefined() // 水位不得被旧响应推进
        await vi.advanceTimersByTimeAsync(3500)
        expect(SessionsRead).not.toHaveBeenCalled() // 不得上报「读过从未看到的 A 会话」
    })

    it('会话未切走时照常推进水位并上报已读（不误伤正常路径）', async () => {
        vi.useFakeTimers()
        SessionsRead.mockResolvedValue({ Advanced: [] })
        SessionMessages.mockResolvedValue({
            Messages: [{ Seq: 12, MsgId: 'a12', Direction: 1, Content: 'hi', ContentType: 'text', SessionKey: 'A' }],
            HasMore: false
        })
        const store = useChatStore()

        await store.openSession('A')

        expect(store.readSeqs.A).toBe(12)
        await vi.advanceTimersByTimeAsync(3500)
        expect(SessionsRead).toHaveBeenCalledTimes(1)
        expect(SessionsRead.mock.calls[0][0]).toEqual({ A: 12 })
    })

    it('disconnect（登出）后旧响应同样不得推进水位 / 上报已读', async () => {
        vi.useFakeTimers()
        SessionsRead.mockResolvedValue({ Advanced: [] })
        let resolveA
        SessionMessages.mockImplementation(() => new Promise((resolve) => (resolveA = resolve)))
        const store = useChatStore()
        store.connect() // 造出模块级 socket，disconnect 才有对象可关

        const openA = store.openSession('A')
        store.disconnect()
        resolveA({
            Messages: [{ Seq: 55, MsgId: 'a55', Direction: 1, Content: 'hi', ContentType: 'text', SessionKey: 'A' }],
            HasMore: false
        })
        await openA

        expect(store.readSeqs.A).toBeUndefined()
        await vi.advanceTimersByTimeAsync(3500)
        expect(SessionsRead).not.toHaveBeenCalled()
    })
})

describe('F7 脏 seq 不污染 syncCursor', () => {
    it('WS 帧 seq 为字符串/对象时游标保持有限数，真实行序不乱', () => {
        const store = useChatStore()
        seedSession(store)

        store.handleFrame({ type: 'message', msgId: 'bad', seq: 'abc', content: '毒', contentType: 'text', direction: 1, session: 'grp' })
        expect(Number.isFinite(store.syncCursor)).toBe(true)

        store.handleFrame({ type: 'message', msgId: 'm5', seq: 5, content: '五', contentType: 'text', direction: 1, session: 'grp' })
        store.handleFrame({ type: 'message', msgId: 'm3', seq: 3, content: '三', contentType: 'text', direction: 1, session: 'grp' })

        expect(store.syncCursor).toBe(5)
        // 脏行 seq 归零沉底，真实行仍按 Seq 升序
        expect(store.messagesBySession.grp.map((r) => r.Seq)).toEqual([3, 5, 0])
    })

    it('REST 行 / 补拉页序为脏值时同样不污染游标', async () => {
        const store = useChatStore()
        seedSession(store)
        store.applyMessage({
            Seq: { weird: true },
            MsgId: 'mx',
            Direction: 1,
            Content: '毒',
            ContentType: 'text',
            SessionKey: 'grp'
        })
        expect(Number.isFinite(store.syncCursor)).toBe(true)
        expect(store.syncCursor).toBe(0)

        SyncMessages.mockResolvedValue({ Data: [], MaxSeq: 'abc' })
        await store.catchUp()
        expect(Number.isFinite(store.syncCursor)).toBe(true)
    })
})

describe('F12 列表载荷兜底（非数组不崩）', () => {
    it('sessions 载荷为非数组对象时收敛为空列表且不抛', async () => {
        SessionsOverview.mockResolvedValue({ Sessions: { oops: 1 }, MaxSeq: 5 })
        const store = useChatStore()
        await expect(store.refreshSessions()).resolves.toBeUndefined()
        expect(store.sessions).toEqual([])
        expect(store.syncCursor).toBe(5)
    })

    it('补拉 Data 为非数组时不抛、不误推进游标', async () => {
        SyncMessages.mockResolvedValue({ Data: { oops: 1 }, MaxSeq: 9 })
        const store = useChatStore()
        seedSession(store)
        store.syncCursor = 4
        await expect(store.catchUp()).resolves.toBeUndefined()
        expect(store.syncCursor).toBe(4)
    })
})
