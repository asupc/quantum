/**
 * 视图层加固回归（对抗性审计 F8/F12）：
 *  - F8：书签 URL（可来自导入的浏览器书签 HTML）只允许 http(s)，且必须带 noopener,noreferrer；
 *  - F12：列表载荷非数组（对象/null/字符串）时收敛为空列表，不得崩溃渲染。
 *
 * 直接导入 .vue 的 options 对象，用最小 this 上下文调用目标方法（不挂载渲染，环境为 node）。
 * 覆盖：navigation/openBookmark、navigation/loadBookmarks、ai/reloadMessages、ai/loadConversations、
 * docker/containers.loadContainers、docker/images.loadImages。
 */
import { describe, expect, it, vi, beforeAll } from 'vitest'

vi.mock('@/api/bookmark', () => ({
    getBookmarks: vi.fn(),
    createBookmark: vi.fn(),
    updateBookmark: vi.fn(),
    deleteBookmark: vi.fn(),
    importBookmarks: vi.fn()
}))
vi.mock('@/api/aiAgent', () => ({
    GetConversations: vi.fn(),
    SaveConversation: vi.fn(),
    DeleteConversations: vi.fn(),
    GetAiMessages: vi.fn(),
    SendAgentChat: vi.fn(),
    GetAgentRun: vi.fn(),
    GetLatestRun: vi.fn(),
    CancelAgentRun: vi.fn(),
    GetAgentSteps: vi.fn(),
    GetAgentContract: vi.fn(),
    GetDefaultModel: vi.fn(),
    TestRunProposal: vi.fn(),
    ApplyProposal: vi.fn(),
    DiscardProposal: vi.fn(),
    GetProposalContent: vi.fn()
}))
vi.mock('@/api/task', () => ({ GetTask: vi.fn(), ScriptList: vi.fn() }))
vi.mock('@/api/docker', () => ({
    listImages: vi.fn(),
    removeImage: vi.fn(),
    listContainers: vi.fn(),
    startContainer: vi.fn(),
    stopContainer: vi.fn(),
    restartContainer: vi.fn(),
    removeContainer: vi.fn(),
    getContainerLogs: vi.fn(),
    getContainerStats: vi.fn(),
    getContainersStats: vi.fn(),
    inspectContainer: vi.fn()
}))
// echarts 在 node 环境无渲染能力，仅需可导入
vi.mock('echarts', () => ({ init: vi.fn() }))

import { listContainers, listImages } from '@/api/docker'
import { GetConversations } from '@/api/aiAgent'
import { getBookmarks } from '@/api/bookmark'

const openSpy = vi.fn()
vi.stubGlobal('window', { open: openSpy, location: { origin: 'http://test.local' } })

let NavigationPage
let AiPage
let DockerContainers
let DockerImages

beforeAll(async () => {
    NavigationPage = (await import('@/view/navigation/index.vue')).default
    AiPage = (await import('@/view/ai/index.vue')).default
    DockerContainers = (await import('@/view/docker/containers.vue')).default
    DockerImages = (await import('@/view/docker/images.vue')).default
})

/** AI 页最小 this 上下文（只装被测方法用到的字段） */
function aiContext(overrides = {}) {
    const ctx = {
        Conversations: [],
        CurrentId: '',
        Messages: [],
        MaxSeq: 0,
        openConversation: vi.fn(),
        scrollToBottom: vi.fn(),
        $message: { error: vi.fn(), warning: vi.fn(), success: vi.fn() },
        ...overrides
    }
    ctx.ensureStepEntry = vi.fn()
    ctx.ensureStepEntries = () => AiPage.methods.ensureStepEntries.call(ctx)
    return ctx
}

/** 等一个宏任务：被测方法不返回其内部 Promise（loadConversations/loadBookmarks） */
const flush = () => new Promise((resolve) => setTimeout(resolve, 0))

describe('F8 书签 URL 协议白名单（openBookmark）', () => {
    it('javascript:/data:/协议相对地址一律不打开且给出提示', () => {
        const ctx = { $message: { warning: vi.fn() } }
        openSpy.mockClear()
        ;['javascript:alert(1)', 'data:text/html,<script>1</script>', '//evil.example/x', 'file:///etc/passwd'].forEach((Url) => {
            NavigationPage.methods.openBookmark.call(ctx, { Title: 'x', Url })
        })
        expect(openSpy).not.toHaveBeenCalled()
        expect(ctx.$message.warning).toHaveBeenCalledTimes(4)
    })

    it('https 地址正常打开且带 noopener,noreferrer', () => {
        const ctx = { $message: { warning: vi.fn() } }
        openSpy.mockClear()
        NavigationPage.methods.openBookmark.call(ctx, { Title: 'x', Url: 'https://ok.example/a' })
        expect(openSpy).toHaveBeenCalledWith('https://ok.example/a', '_blank', 'noopener,noreferrer')
        expect(ctx.$message.warning).not.toHaveBeenCalled()
    })

    it('空/非字符串 Url 不打开也不抛', () => {
        const ctx = { $message: { warning: vi.fn() } }
        openSpy.mockClear()
        expect(() => NavigationPage.methods.openBookmark.call(ctx, { Url: '' })).not.toThrow()
        expect(() => NavigationPage.methods.openBookmark.call(ctx, { Url: { evil: 1 } })).not.toThrow()
        expect(() => NavigationPage.methods.openBookmark.call(ctx, null)).not.toThrow()
        expect(openSpy).not.toHaveBeenCalled()
    })
})

describe('F12 列表载荷兜底（视图层）', () => {
    it('书签列表 Data 非数组时收敛为空列表（不抛「不可迭代」）', async () => {
        getBookmarks.mockResolvedValue({ Data: { oops: 1 } })
        const ctx = { searchKeyword: '', bookmarks: [], filteredBookmarks: [] }
        await expect(NavigationPage.methods.loadBookmarks.call(ctx)).resolves.toBeUndefined()
        expect(ctx.bookmarks).toEqual([])
        expect(ctx.filteredBookmarks).toEqual([])
    })

    it('AI 会话列表非数组时收敛为空列表且 CurrentConversation 不炸', async () => {
        GetConversations.mockResolvedValue({ oops: 1 })
        const ctx = aiContext()
        AiPage.methods.loadConversations.call(ctx)
        await flush()
        expect(ctx.Conversations).toEqual([])
        expect(() => AiPage.computed.CurrentConversation.call(ctx)).not.toThrow()
    })

    it('AI 消息重拉非数组时收敛为空列表（不把对象写进 Messages）', async () => {
        const { GetAiMessages } = await import('@/api/aiAgent')
        GetAiMessages.mockResolvedValue({ oops: 1 })
        const ctx = aiContext({ CurrentId: 'c1' })
        await AiPage.methods.reloadMessages.call(ctx)
        expect(Array.isArray(ctx.Messages)).toBe(true)
        expect(ctx.Messages).toEqual([])
    })

    it('docker 容器列表非数组时收敛为空列表（不整页 loadContainers 崩）', async () => {
        listContainers.mockResolvedValue({ oops: 1 })
        const ctx = { tab: 'all', containers: null, loadResourceUsage: vi.fn() }
        await expect(DockerContainers.methods.loadContainers.call(ctx)).resolves.toBeUndefined()
        expect(ctx.containers).toEqual([])
    })

    it('docker 镜像列表非数组时收敛为空列表', async () => {
        listImages.mockResolvedValue({ oops: 1 })
        const ctx = { images: null }
        await expect(DockerImages.methods.loadImages.call(ctx)).resolves.toBeUndefined()
        expect(ctx.images).toEqual([])
    })
})
