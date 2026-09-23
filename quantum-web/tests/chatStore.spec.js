/**
 * 会话 store 行为测试（docs/会话分组归并方案.md §3 第 23 项）：
 *  - pickOption：点选从载荷根部取 taskId，WS 帧 targetTask / REST 回落 TargetTaskId 两路透传；手打不带
 *  - deleteSession：本地移除会话项 + 清窗口 + 水位归零 + 游标回拨 Math.min(当前, 返回 MaxSeq)
 *    （2026-09-21 日志删除逻辑调整：删除只删会话数据，服务端消息记录保留）
 *
 * 现有测试基建为 node 环境（无 DOM），store 的 UI/网络依赖全部模块级 mock：
 * naive-discrete（消息弹窗）、ws（长连接，send 记录到 globalThis.__wsFrames）、
 * api/app + api/task（axios 封装）、chatMedia（媒体缓存）；window/localStorage 以 stubGlobal 打桩。
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
    SubmitCommand: vi.fn(),
    NotificationList: vi.fn()
}))

import { useChatStore } from '@/stores/chat'
import { DeleteSession, SessionMessages, SessionsOverview, SessionsRead, SubmitCommand, SyncMessages } from '@/api/app'

vi.stubGlobal('window', { location: { origin: 'http://test.local' } })
vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn() })

const OPTION_PAYLOAD = JSON.stringify({
    taskId: 'task-9',
    options: [{ key: '1', label: '选项一', reply: '回复一' }]
})

/** 造一条带选项载荷的机器人消息（decorate 前的原始行形态：Payload 为 JSON 字符串） */
function seedOptionMessage(store, sessionKey = 'grp') {
    const msg = {
        Seq: 5,
        MsgId: 'm5',
        Direction: 1,
        Content: '选一个',
        ContentType: 'text',
        SessionKey: sessionKey,
        Payload: OPTION_PAYLOAD
    }
    store.currentKey = sessionKey
    store.messagesBySession = { [sessionKey]: [msg] }
    return msg
}

beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
    globalThis.__wsFrames = []
})

afterEach(() => {
    // 复位模块级 WS socket（disconnect 会把 socket 置 null，避免用例间串扰）
    useChatStore().disconnect()
})

describe('pickOption 点选透传来源任务（方案 §2.4）', () => {
    it('WS 通路上组帧带 targetTask（camelCase 帧字段）', async () => {
        const store = useChatStore()
        const msg = seedOptionMessage(store)
        store.connect() // 接入 mock AppSocket：sendFrame 可用
        const ok = await store.pickOption(msg, { key: '1', label: '选项一', reply: '回复一' })
        expect(ok).toBe(true)
        expect(globalThis.__wsFrames).toHaveLength(1)
        expect(globalThis.__wsFrames[0]).toMatchObject({
            type: 'command',
            content: '回复一',
            session: 'grp',
            targetTask: 'task-9'
        })
        expect(SubmitCommand).not.toHaveBeenCalled()
        // 点选成功写本地已选态
        expect(store.chosen[5]).toBe('1')
    })

    it('WS 未连接回落 REST：请求体带 TargetTaskId（PascalCase）', async () => {
        SubmitCommand.mockResolvedValue({ Accepted: true })
        const store = useChatStore()
        const msg = seedOptionMessage(store)
        // 不 connect()：socket 为 null，sendFrame 返回 false → REST 回落
        const ok = await store.pickOption(msg, { key: '1', label: '选项一', reply: '回复一' })
        expect(ok).toBe(true)
        expect(globalThis.__wsFrames).toHaveLength(0)
        expect(SubmitCommand).toHaveBeenCalledTimes(1)
        expect(SubmitCommand.mock.calls[0][0]).toMatchObject({
            Content: '回复一',
            Session: 'grp',
            TargetTaskId: 'task-9'
        })
    })

    it('手打不带 targetTask；载荷无 taskId 的点选也不带', async () => {
        SubmitCommand.mockResolvedValue({ Accepted: true })
        const store = useChatStore()
        store.currentKey = 'grp'
        store.lastSendAt = 0
        const ok = await store.send('手打指令')
        expect(ok).toBe(true)
        expect(SubmitCommand.mock.calls[0][0].TargetTaskId).toBeUndefined()

        // 旧载荷（无 taskId）的点选：send 被节流拦下前先重置节流时间戳
        store.lastSendAt = 0
        store.sending = false
        const legacyMsg = {
            Seq: 6,
            MsgId: 'm6',
            Direction: 1,
            Content: '选',
            ContentType: 'text',
            SessionKey: 'grp',
            Payload: JSON.stringify({ options: [{ key: '2', label: '旧', reply: '旧回复' }] })
        }
        store.messagesBySession = { grp: [legacyMsg] }
        const ok2 = await store.pickOption(legacyMsg, { key: '2', label: '旧', reply: '旧回复' })
        expect(ok2).toBe(true)
        expect(SubmitCommand.mock.calls[1][0].TargetTaskId).toBeUndefined()
    })
})

describe('deleteSession 删除会话（2026-09-21 日志删除逻辑调整：只删会话数据，消息记录保留）', () => {
    it('本地移除会话项 + 清窗口 + 水位归零 + 游标回拨 Math.min(当前, 返回 MaxSeq)', async () => {
        DeleteSession.mockResolvedValue({ Deleted: true, MaxSeq: 5 })
        const store = useChatStore()
        store.syncCursor = 20
        store.sessions = [
            { SessionKey: 'grp', Total: 2, Unread: 1, Last: { Seq: 12 } },
            { SessionKey: 'other', Total: 1, Unread: 2, Last: { Seq: 9 } }
        ]
        store.readSeqs = { grp: 12 }
        store.messagesBySession = { grp: [{ Seq: 12, MsgId: 'm12' }] }
        const res = await store.deleteSession('grp')
        expect(res.Deleted).toBe(true)
        expect(store.sessions.map((item) => item.SessionKey)).toEqual(['other'])
        expect(store.messagesBySession.grp).toEqual([])
        expect(store.readSeqs.grp).toBe(0)
        expect(store.unreadTotal).toBe(2)
        expect(store.syncCursor).toBe(5)
    })

    it('服务端未删除（Deleted=false）不动本地状态', async () => {
        DeleteSession.mockResolvedValue({ Deleted: false, MaxSeq: 20 })
        const store = useChatStore()
        store.syncCursor = 20
        store.sessions = [{ SessionKey: 'grp', Total: 2 }]
        await store.deleteSession('grp')
        expect(store.sessions).toHaveLength(1)
        expect(store.syncCursor).toBe(20)
    })
})

describe('applyMessageMove 跨会话触发消息迁移（2026-09-18 批次）', () => {
    beforeEach(() => {
        SessionsOverview.mockResolvedValue({ Sessions: [], MaxSeq: 0 })
        SessionMessages.mockResolvedValue({ Messages: [], HasMore: false })
    })

    it('handleFrame 分发 message_moved：搬移本地行 + 刷新列表快照 + 正停留在来源会话时自动切换', async () => {
        const store = useChatStore()
        store.currentKey = ''
        store.messagesBySession = {
            '': [{ Seq: 12, MsgId: 'm12', Direction: 2, Content: '随机密码-1', ContentType: 'text' }]
        }
        await store.handleFrame({ type: 'message_moved', msgId: 'm12', seq: 12, to: 'task-pwd' })
        // 默认会话（from 缺省）的行已搬到任务会话
        expect(store.messagesBySession['']).toHaveLength(0)
        expect(store.messagesBySession['task-pwd'][0]).toMatchObject({ MsgId: 'm12' })
        // 会话列表快照刷新（预览/排序以服务端为准）
        expect(SessionsOverview).toHaveBeenCalledTimes(1)
        // D4 自动切换：currentKey（默认会话 ''）=== from（缺省）→ 打开目标会话；
        // 行刚搬入 to 列表（已存在），openSession 跳过首屏拉取
        expect(store.currentKey).toBe('task-pwd')
        expect(SessionMessages).not.toHaveBeenCalled()
    })

    it('正停留在其他会话时只搬数据不打断（D4）', async () => {
        const store = useChatStore()
        store.currentKey = 'task-music'
        store.messagesBySession = {
            '': [{ Seq: 12, MsgId: 'm12', Direction: 2, Content: '随机密码-1', ContentType: 'text' }]
        }
        await store.applyMessageMove({ type: 'message_moved', msgId: 'm12', seq: 12, from: '', to: 'task-pwd' })
        expect(store.currentKey).toBe('task-music')
        expect(store.messagesBySession['task-pwd']).toHaveLength(1)
    })

    it('迁移帧先于 echo 到达（乱序防御）：先记 moves，echo 落行时改写会话键并清来源 pending 乐观气泡', async () => {
        const store = useChatStore()
        store.currentKey = ''
        store.messagesBySession = {
            '': [{ Seq: null, MsgId: 'local-1', Direction: 2, Content: '随机密码-1', ContentType: 'text', pending: true }]
        }
        await store.applyMessageMove({ type: 'message_moved', msgId: 'm12', seq: 12, from: '', to: 'task-pwd' })
        // 行不在本地（pending 气泡是 local- id）：只记映射；停留来源会话仍自动切换（openSession 建好目标列表）
        expect(store.moves.get('m12')).toEqual({ from: '', to: 'task-pwd' })
        expect(store.currentKey).toBe('task-pwd')
        // echo 后到（帧 session 仍是来源）：applyMessage 命中 moves → 行落目标会话 + 来源 pending 被清
        store.handleFrame({
            type: 'message',
            msgId: 'm12',
            seq: 12,
            content: '随机密码-1',
            contentType: 'text',
            direction: 2,
            createTime: '2026-09-18 12:00:00'
        })
        expect(store.messagesBySession['']).toHaveLength(0)
        expect(store.messagesBySession['task-pwd']).toHaveLength(1)
        expect(store.messagesBySession['task-pwd'][0].MsgId).toBe('m12')
        expect(store.messagesBySession['task-pwd'][0].pending).toBeFalsy()
    })

    it('from === to 忽略（防御）；缺 msgId 同样忽略', async () => {
        const store = useChatStore()
        store.currentKey = 'task-pwd'
        await store.applyMessageMove({ type: 'message_moved', msgId: 'm12', seq: 12, from: 'task-pwd', to: 'task-pwd' })
        await store.applyMessageMove({ type: 'message_moved', seq: 12, from: 'a', to: 'b' })
        expect(SessionsOverview).not.toHaveBeenCalled()
        expect(store.moves.size).toBe(0)
    })
})

describe('applyMessage 顺序维护（§3-4 二分插入）', () => {
    it('乱序到达的行插入后仍按 Seq 升序', () => {
        const store = useChatStore()
        store.currentKey = 'other' // 避开 advanceWatermark/上报
        store.sessions = [{ SessionKey: 'grp', Total: 0, Unread: 0, Last: { Seq: 0 } }]
        store.messagesBySession = { grp: [] }
        ;[5, 3, 8, 1].forEach((seq) =>
            store.applyMessage({
                Seq: seq,
                MsgId: `m${seq}`,
                Direction: 1,
                Content: `c${seq}`,
                ContentType: 'text',
                SessionKey: 'grp'
            })
        )
        expect(store.messagesBySession.grp.map((r) => r.Seq)).toEqual([1, 3, 5, 8])
    })
})

describe('disconnect 重置运行态（§3-7 WS 双死锁修复）', () => {
    it('清 ready / 内存会话态 / 游标 / moves / 阅读节流定时器', () => {
        const store = useChatStore()
        store.ready = true
        store.syncCursor = 42
        store.notifyMap = { a: { title: 'x' } }
        store.messagesBySession = { grp: [{ Seq: 1, MsgId: 'm1' }] }
        store.moves = new Map([['m1', { from: '', to: 'grp' }]])
        store._readTimer = setTimeout(() => {}, 100000)
        store.connect() // 造出模块级 socket（非空）
        store.disconnect()
        expect(store.ready).toBe(false) // 重登录后 initRealtime 才会重走
        expect(store.messagesBySession).toEqual({})
        expect(store.notifyMap).toEqual({})
        expect(store.syncCursor).toBe(0)
        expect(store.moves.size).toBe(0)
        expect(store._readTimer).toBeFalsy()
    })
})

describe('并发合流（§3-8 in-flight）', () => {
    it('refreshSessions 并发只打一次服务端', async () => {
        let resolveFirst
        SessionsOverview.mockImplementation(() => new Promise((r) => (resolveFirst = r)))
        const store = useChatStore()
        const p1 = store.refreshSessions()
        const p2 = store.refreshSessions()
        expect(SessionsOverview).toHaveBeenCalledTimes(1)
        resolveFirst({ Sessions: [], MaxSeq: 3 })
        await Promise.all([p1, p2])
        expect(store.syncCursor).toBe(3)
    })

    it('catchUp 并发锁：第二份复用同一 in-flight', async () => {
        SyncMessages.mockImplementation(() => Promise.resolve({ Data: [], MaxSeq: 0 }))
        const store = useChatStore()
        const p1 = store.catchUp()
        const p2 = store.catchUp()
        await Promise.all([p1, p2])
        expect(SyncMessages).toHaveBeenCalledTimes(1)
    })
})

describe('会话预览回写（§3-9 透传 CreateTime 且不倒退）', () => {
    it('新行前进预览带 CreateTime；更旧行不倒退预览', () => {
        const store = useChatStore()
        store.currentKey = 'other'
        store.sessions = [
            { SessionKey: 'grp', Total: 0, Unread: 0, Last: { Seq: 10, Content: 'old', CreateTime: 't10' } }
        ]
        store.messagesBySession = { grp: [] }
        const grp = () => store.sessions.find((s) => s.SessionKey === 'grp')

        store.applyMessage({
            Seq: 12,
            MsgId: 'm12',
            Direction: 1,
            Content: 'new',
            ContentType: 'text',
            SessionKey: 'grp',
            CreateTime: 't12'
        })
        expect(grp().Last.Seq).toBe(12)
        expect(grp().Last.CreateTime).toBe('t12')
        expect(grp().Last.Content).toBe('new')

        // 补拉到达更早行（Seq 11 < 12）→ 预览保持最新，不被旧帧回退
        store.applyMessage({
            Seq: 11,
            MsgId: 'm11',
            Direction: 1,
            Content: 'older',
            ContentType: 'text',
            SessionKey: 'grp',
            CreateTime: 't11'
        })
        expect(grp().Last.Seq).toBe(12)
        expect(grp().Last.CreateTime).toBe('t12')
    })
})

describe('会话预览与未读派生（§3-10）', () => {
    it('refreshSessions 给每个会话预算 previewText（正文首行、去富文本标记）', async () => {
        SessionsOverview.mockResolvedValue({
            Sessions: [
                {
                    SessionKey: 'grp',
                    Total: 3,
                    Unread: 1,
                    Last: { Seq: 4, Content: '第一行\n第二行', ContentType: 'text' }
                }
            ],
            MaxSeq: 4
        })
        const store = useChatStore()
        await store.refreshSessions()
        expect(store.sessions[0].previewText).toBe('第一行')
    })

    it('unreadTotal 为派生 getter，随 sessions 响应式重算', () => {
        const store = useChatStore()
        store.sessions = [
            { SessionKey: 'a', Unread: 2 },
            { SessionKey: 'b', Unread: 5 }
        ]
        expect(store.unreadTotal).toBe(7)
        store.sessions = [{ SessionKey: 'a', Unread: 2 }]
        expect(store.unreadTotal).toBe(2)
    })
})

// ==================================================================== 会话已读水位双端同步（2026-09-21 批次）

describe('session_read 帧处理（双端同步）', () => {
    beforeEach(() => {
        SessionsOverview.mockResolvedValue({ Sessions: [], MaxSeq: 0 })
    })

    it('推进本地水位 + 该会话未读快路径置 0 + 500ms 防抖合并一次权威刷新', async () => {
        vi.useFakeTimers()
        const store = useChatStore()
        store.readSeqs = { grp: 3, other: 1 }
        store.sessions = [
            { SessionKey: 'grp', Total: 5, Unread: 2, Last: null },
            { SessionKey: 'other', Total: 3, Unread: 4, Last: null }
        ]

        store.handleFrame({ type: 'session_read', session: 'grp', seq: 7 })

        // 快路径：目标会话未读即时清、其他会话不动；本地水位只进推进
        expect(store.readSeqs.grp).toBe(7)
        expect(store.sessions.find((s) => s.SessionKey === 'grp').Unread).toBe(0)
        expect(store.sessions.find((s) => s.SessionKey === 'other').Unread).toBe(4)
        expect(SessionsOverview).not.toHaveBeenCalled()

        // 防抖窗口内第二条帧合并为一次权威重算
        store.handleFrame({ type: 'session_read', session: 'other', seq: 9 })
        await vi.advanceTimersByTimeAsync(500)
        expect(SessionsOverview).toHaveBeenCalledTimes(1)

        // 回退/重复帧（seq 不高于本地水位）幂等忽略，不再调度刷新
        store.handleFrame({ type: 'session_read', session: 'grp', seq: 5 })
        expect(store.readSeqs.grp).toBe(7)
        expect(SessionsOverview).toHaveBeenCalledTimes(1)

        // 默认会话：session 缺省 → 空串键
        store.handleFrame({ type: 'session_read', seq: 4 })
        expect(store.readSeqs['']).toBe(4)
        vi.useRealTimers()
    })

    it('disconnect 清理防抖定时器', () => {
        vi.useFakeTimers()
        const store = useChatStore()
        store.readSeqs = { grp: 1 }
        store.handleFrame({ type: 'session_read', session: 'grp', seq: 2 })
        expect(store._sessionsRefreshTimer).toBeTruthy()
        store.disconnect()
        expect(store._sessionsRefreshTimer).toBeFalsy()
        vi.useRealTimers()
    })
})

describe('reportRead 按会话 map 上报（sessions/read 端点）', () => {
    it('3s 节流合批发 {ReadSeqs}：同会话取最大、多会话合入一批；失败静默', async () => {
        vi.useFakeTimers()
        SessionsRead.mockResolvedValue({ Advanced: [] })
        const store = useChatStore()

        store.reportRead('grp', 5)
        store.reportRead('grp', 8)
        store.reportRead('other', 3)
        expect(SessionsRead).not.toHaveBeenCalled()

        await vi.advanceTimersByTimeAsync(3000)
        expect(SessionsRead).toHaveBeenCalledTimes(1)
        expect(SessionsRead.mock.calls[0][0]).toEqual({ grp: 8, other: 3 })

        // 上报失败静默：不抛未处理拒绝，节流器回到空闲（下一轮可再报）
        SessionsRead.mockRejectedValue(new Error('network down'))
        store.reportRead('grp', 9)
        await vi.advanceTimersByTimeAsync(3000)
        expect(SessionsRead).toHaveBeenCalledTimes(2)
        expect(store._readTimer).toBeFalsy()
        vi.useRealTimers()
    })
})

describe('overview 吸收服务端权威 ReadSeq（离线追赶）', () => {
    it('响应条目 ReadSeq 只进合并进本地并持久化；服务端更低不回拨本地', async () => {
        SessionsOverview.mockResolvedValue({
            Sessions: [
                { SessionKey: 'grp', Total: 3, Unread: 0, ReadSeq: 10, Last: null },
                { SessionKey: 'other', Total: 1, Unread: 2, ReadSeq: 1, Last: null }
            ],
            MaxSeq: 10
        })
        const store = useChatStore()
        store.readSeqs = { grp: 4, other: 5 }

        await store.refreshSessions()

        expect(store.readSeqs.grp).toBe(10)
        expect(store.readSeqs.other).toBe(5)
        expect(localStorage.setItem).toHaveBeenCalled()
    })
})
