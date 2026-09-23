import axios from '@/libs/api.request';

export const GetTask = (query) => {
    return axios.request({
        url: '/api/Task',
        params: query,
        method: 'get'
    });
};

// 新增任务
export const PostTask = (data) => {
    return axios.request({
        url: '/api/Task',
        data,
        method: 'post'
    });
};

// 更新任务
export const UpdateTask = (data) => {
    return axios.request({
        url: '/api/Task',
        data,
        method: 'put'
    });
};

// ids 以逗号拼接走 query（DELETE 请求统一不使用 body）
export const DeleteTask = (ids) => {
    return axios.request({
        url: '/api/Task',
        params: { ids: ids.join(',') },
        method: 'delete'
    });
};

export const DisableTask = (ids) => {
    return axios.request({
        url: '/api/Task/disable',
        data: ids,
        method: 'put'
    });
};

export const EnableTask = (ids) => {
    return axios.request({
        url: '/api/Task/enable',
        data: ids,
        method: 'put'
    });
};

export const GetScripts = (query) => {
    return axios.request({
        url: '/api/Task/scripts',
        params: query,
        method: 'get'
    });
};
export const UpdateScripts = (data) => {
    return axios.request({
        url: '/api/Task/scripts',
        data,
        method: 'put',
        noLoading: true
    });
};

export const ExecTask = (data) => {
    return axios.request({
        url: '/api/Task/exec-task',
        data,
        method: 'post'
    });
};

export const ScriptList = () => {
    return axios.request({
        url: '/api/Task/script-list',
        method: 'get'
    });
};

export const DeleteScripts = (name) => {
    return axios.request({
        url: '/api/Task/delete-scripts?fileName=' + name,
        method: 'delete'
    });
};

// 文件流下载：需携带 token 鉴权，axios 拦截器对 blob 响应直接放行完整 response
export const ExportTask = (query) => {
    return axios.request({
        url: '/api/Task/export',
        params: query,
        method: 'get',
        responseType: 'blob'
    });
};
