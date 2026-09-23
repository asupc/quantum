import axios from '@/libs/api.request'

// AI 脚本修复 Agent：会话、消息、运行轮询与修复提案（管理员专属，2026-09-20 新增）

export const GetConversations = () => {
    return axios.request({
        url: '/api/AiAgent/conversations',
        method: 'get'
    })
}

// 新建 / 改名会话（AllowEnvValues：本会话是否允许把环境变量值发给模型）
export const SaveConversation = (data) => {
    return axios.request({
        url: '/api/AiAgent/conversations',
        data,
        method: 'post'
    })
}

export const DeleteConversations = (ids) => {
    return axios.request({
        url: '/api/AiAgent/conversations/deletes?ids=' + ids.join(','),
        method: 'delete'
    })
}

// 增量拉消息（afterSeq 之后的新消息，afterSeq=0 取最近 limit 条）：轮询高频调用，关掉全局 loading 条
export const GetAiMessages = (query) => {
    return axios.request({
        url: '/api/AiAgent/messages',
        params: query,
        method: 'get',
        noLoading: true
    })
}

// 发起一次运行（立即返回 RunId/MessageId，正文走 runs/steps 与 messages 轮询）
export const SendAgentChat = (data) => {
    return axios.request({
        url: '/api/AiAgent/chat',
        data,
        method: 'post'
    })
}

export const GetAgentRun = (id) => {
    return axios.request({
        url: `/api/AiAgent/runs/${id}`,
        method: 'get',
        noLoading: true
    })
}

// 会话最近一次运行（任意状态；无 run 时 Data=null）——刷新/重进页面后的运行态恢复权威来源
export const GetLatestRun = (conversationId) => {
    return axios.request({
        url: '/api/AiAgent/runs/latest',
        params: { conversationId },
        method: 'get',
        noLoading: true
    })
}

export const CancelAgentRun = (id) => {
    return axios.request({
        url: `/api/AiAgent/runs/${id}/cancel`,
        method: 'post'
    })
}

// 运行过程轨迹（工具调用/结果，页面「过程」面板增量拉取）
export const GetAgentSteps = (id, afterSeq = 0) => {
    return axios.request({
        url: `/api/AiAgent/runs/${id}/steps`,
        params: { afterSeq },
        method: 'get',
        noLoading: true
    })
}

// 平台能力摘要长文本（页面「平台能力」弹层展示）
export const GetAgentContract = () => {
    return axios.request({
        url: '/api/AiAgent/contract',
        method: 'get'
    })
}

// 当前默认模型（顶栏展示供应商 / 模型 · 上下文 · 工具能力）
export const GetDefaultModel = () => {
    return axios.request({
        url: '/api/AiAgent/default-model',
        method: 'get'
    })
}

export const TestRunProposal = (id) => {
    return axios.request({
        url: `/api/AiAgent/proposals/${id}/test-run`,
        method: 'post'
    })
}

// 应用提案：返回 { Success, Blocked, Warnings, Errors }，Success=false 时按诊断清单展示
export const ApplyProposal = (id) => {
    return axios.request({
        url: `/api/AiAgent/proposals/${id}/apply`,
        method: 'post'
    })
}

export const DiscardProposal = (id) => {
    return axios.request({
        url: `/api/AiAgent/proposals/${id}/discard`,
        method: 'post'
    })
}

// 提案正文（{ FileName, NewContent, BaseContent }，页面做行级 diff）
export const GetProposalContent = (id) => {
    return axios.request({
        url: `/api/AiAgent/proposals/${id}/content`,
        method: 'get'
    })
}
