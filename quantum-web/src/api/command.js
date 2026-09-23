import axios from '@/libs/api.request';

export const GetCommand = (query) => {
    return axios.request({
        url: '/api/Command',
        params: query,
        method: 'get'
    });
};

// 新增快捷回复
export const PostCommand = (data) => {
    return axios.request({
        url: '/api/Command',
        data,
        method: 'post'
    });
};

// 修改快捷回复
export const UpdateCommand = (data) => {
    return axios.request({
        url: '/api/Command',
        data,
        method: 'put'
    });
};

// ids 以逗号拼接走 query（DELETE 请求统一不使用 body）
export const DeleteCommand = (ids) => {
    return axios.request({
        url: '/api/Command',
        params: { ids: ids.join(',') },
        method: 'delete'
    });
};

export const DisableCommand = (data) => {
    return axios.request({
        url: '/api/Command/Disable',
        data,
        method: 'put'
    });
};

export const EnableCommand = (data) => {
    return axios.request({
        url: '/api/Command/Enable',
        data,
        method: 'put'
    });
};
