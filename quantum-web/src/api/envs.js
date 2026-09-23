import axios from '@/libs/api.request'

export const GetEnvs = (query) => {
    return axios.request({
        url: '/api/Env',
        params: query,
        method: 'get'
    })
}

// 文件流下载：需携带 token 鉴权，axios 拦截器对 blob 响应直接放行完整 response
export const ExportEnvs = (query) => {
    return axios.request({
        url: '/api/Env/export',
        params: query,
        method: 'get',
        responseType: 'blob'
    })
}
// 批量保存（按名称合并：存在即更新、不存在即新增）
export const PostEnvs = (data) => {
    return axios.request({
        url: '/api/Env',
        data,
        method: 'post'
    })
}

// ids 以逗号拼接走 query（DELETE/条件类 PUT 请求统一不使用 body）
export const DeleteEnvs = (ids) => {
    return axios.request({
        url: '/api/Env/deletes',
        params: { ids: ids.join(',') },
        method: 'delete'
    })
}

export const Disable = (ids) => {
    return axios.request({
        url: '/api/Env/disable',
        params: { ids: ids.join(',') },
        method: 'put'
    })
}
export const Enable = (ids) => {
    return axios.request({
        url: '/api/Env/enable',
        params: { ids: ids.join(',') },
        method: 'put'
    })
}
