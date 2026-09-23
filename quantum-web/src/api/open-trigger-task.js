/**
 * 外触内执API
 */
import axios from '@/libs/api.request'

export const GetTask = (query) => {
    return axios.request({
        url: '/api/OpenTriggerTask',
        params: query,
        method: 'get'
    })
}

export const PostTask = (data) => {
    return axios.request({
        url: '/api/OpenTriggerTask',
        data,
        method: 'post'
    })
}
// ids 以逗号拼接走 query（DELETE 请求统一不使用 body）
export const DeleteTask = (ids) => {
    return axios.request({
        url: '/api/OpenTriggerTask',
        params: { ids: ids.join(',') },
        method: 'delete'
    })
}

export const DisableTask = (ids) => {
    return axios.request({
        url: '/api/OpenTriggerTask/Disable',
        data: ids,
        method: 'put'
    })
}

export const EnableTask = (ids) => {
    return axios.request({
        url: '/api/OpenTriggerTask/Enable',
        data: ids,
        method: 'put'
    })
}

export const GetScripts = (query) => {
    return axios.request({
        url: '/api/Task/scripts',
        params: query,
        method: 'get'
    })
}

export const UpdateScripts = (data) => {
    return axios.request({
        url: '/api/Task/scripts',
        data,
        method: 'put'
    })
}