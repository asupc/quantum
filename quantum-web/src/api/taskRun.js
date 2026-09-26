import axios from '@/libs/api.request'

// 执行历史分页：{ taskId, status, page, pageSize, days }
export const GetTaskRuns = (query) => {
    return axios.request({
        url: '/api/TaskRun',
        params: query,
        method: 'get'
    })
}

// 单次执行详情（含同一根执行的尝试时间轴）
export const GetTaskRunDetail = (runId) => {
    return axios.request({
        url: '/api/TaskRun/' + runId,
        method: 'get'
    })
}

// 手动重新执行：产生新的根执行 Id
export const PostTaskRunRetry = (runId) => {
    return axios.request({
        url: '/api/TaskRun/' + runId + '/retry',
        method: 'post'
    })
}

export const GetTaskRunPolicy = (taskId) => {
    return axios.request({
        url: '/api/TaskRun/policy/' + taskId,
        method: 'get'
    })
}

export const PutTaskRunPolicy = (taskId, data) => {
    return axios.request({
        url: '/api/TaskRun/policy/' + taskId,
        data,
        method: 'put'
    })
}

// 受理式执行：返回每项 { taskId, runId }（旧 /api/Task/exec-task 仍保留 bool 语义）
export const ExecuteRuns = (ids) => {
    return axios.request({
        url: '/api/Task/execute-runs',
        data: ids,
        method: 'post'
    })
}
