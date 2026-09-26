import axios from '@/libs/api.request'

export const GetExternalPushCredentials = () => {
    return axios.request({
        url: '/api/ExternalPushCredential',
        method: 'get'
    })
}

// 明文密钥只在创建/轮换响应里出现一次，前端不得写入任何本地存储
export const CreateExternalPushCredential = (data) => {
    return axios.request({
        url: '/api/ExternalPushCredential',
        data,
        method: 'post'
    })
}

export const RotateExternalPushCredential = (id) => {
    return axios.request({
        url: '/api/ExternalPushCredential/' + id + '/rotate',
        method: 'put'
    })
}

export const SetExternalPushEnabled = (id, enabled) => {
    return axios.request({
        url: '/api/ExternalPushCredential/' + id + '/enabled',
        params: { enabled },
        method: 'put'
    })
}

export const DeleteExternalPushCredential = (id) => {
    return axios.request({
        url: '/api/ExternalPushCredential/' + id,
        method: 'delete'
    })
}
