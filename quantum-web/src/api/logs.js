import axios from '@/libs/api.request'

export const GetLogs = (query) => {
    return axios.request({
        url: '/api/Logs',
        params: query,
        method: 'get'
    })
}

// ids 以逗号拼接走 query（DELETE 请求统一不使用 body）
export const DeleteLogs = (ids) => {
    return axios.request({
        url: '/api/Logs',
        params: { ids: ids.join(',') },
        method: 'delete'
    })
}


export const ClearLogs = () => {
    return axios.request({
        url: '/api/Logs/clear',
        method: 'delete'
    })
}

export const LogDetails=(id)=>{
    return axios.request({
        url: '/api/Logs/details/'+id,
        method: 'get'
    })
}
// 日志统计（按级别计数 + 近 N 天按日计数）
export const GetLogStatistics = (days = 7) => {
    return axios.request({
        url: '/api/Logs/statistics?days=' + days,
        method: 'get',
        noLoading: true
    })
}
