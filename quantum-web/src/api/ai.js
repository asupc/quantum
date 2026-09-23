import axios from '@/libs/api.request'

// AI 供应商与模型配置（管理员专属，2026-09-20 新增）

export const GetAiProviders = () => {
    return axios.request({
        url: '/api/AiProvider',
        method: 'get'
    })
}

export const SaveAiProvider = (data) => {
    return axios.request({
        url: '/api/AiProvider',
        data,
        method: 'post'
    })
}

export const DeleteAiProviders = (ids) => {
    return axios.request({
        url: '/api/AiProvider/deletes?ids=' + ids.join(','),
        method: 'delete'
    })
}

// 显示明文密钥（唯一明文出口，后端记操作日志）
export const RevealAiKey = (id) => {
    return axios.request({
        url: `/api/AiProvider/${id}/reveal`,
        method: 'post'
    })
}

// 连通性测试（拉模型清单 + 最小对话 + 工具能力探测）
export const TestAiProvider = (id, modelId) => {
    return axios.request({
        url: `/api/AiProvider/${id}/test`,
        params: { modelId },
        method: 'post',
        noLoading: true
    })
}

export const SyncAiModels = (id) => {
    return axios.request({
        url: `/api/AiProvider/${id}/models`,
        method: 'get'
    })
}

export const ImportAiModels = (data) => {
    return axios.request({
        url: '/api/AiProvider/models/import',
        data,
        method: 'post'
    })
}

export const SaveAiModel = (data) => {
    return axios.request({
        url: '/api/AiProvider/models',
        data,
        method: 'put'
    })
}

export const SetDefaultAiModel = (id) => {
    return axios.request({
        url: `/api/AiProvider/models/default/${id}`,
        method: 'put'
    })
}

export const DeleteAiModels = (ids) => {
    return axios.request({
        url: '/api/AiProvider/models/deletes?ids=' + ids.join(','),
        method: 'delete'
    })
}

export const GetAiSetting = () => {
    return axios.request({
        url: '/api/AiProvider/setting',
        method: 'get'
    })
}

export const SaveAiSetting = (data) => {
    return axios.request({
        url: '/api/AiProvider/setting',
        data,
        method: 'put'
    })
}
