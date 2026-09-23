import axios from '@/libs/api.request'

// 脚本版本管理（管理员专属，2026-09-20 新增）

export const GetScriptVersions = (query) => {
    return axios.request({
        url: '/api/ScriptVersion',
        params: query,
        method: 'get'
    })
}

// 有版本记录的脚本文件清单（版本页左侧筛选）
export const GetVersionFiles = () => {
    return axios.request({
        url: '/api/ScriptVersion/files',
        method: 'get'
    })
}

// 版本详情（含正文）
export const GetScriptVersion = (id) => {
    return axios.request({
        url: '/api/ScriptVersion/' + id,
        method: 'get'
    })
}

// 回滚到指定版本（返回保存流水线三类诊断）
export const RollbackVersion = (id) => {
    return axios.request({
        url: `/api/ScriptVersion/${id}/rollback`,
        method: 'post',
        noLoading: true
    })
}

export const PinVersion = (id, pinned) => {
    return axios.request({
        url: `/api/ScriptVersion/${id}/pin`,
        params: { pinned },
        method: 'put'
    })
}

export const DeleteVersions = (ids) => {
    return axios.request({
        url: '/api/ScriptVersion/deletes?ids=' + ids.join(','),
        method: 'delete'
    })
}
