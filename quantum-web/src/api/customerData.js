import axios from '@/libs/api.request';

export const GetCustomData = (type, query) => {
    return axios.request({
        url: '/api/CustomData/' + type,
        method: 'get',
        params: query
    });
};

// 文件流下载：需携带 token 鉴权，axios 拦截器对 blob 响应直接放行完整 response
export const ExportCustomData = (type, query) => {
    return axios.request({
        url: '/api/CustomData/export/' + type,
        method: 'get',
        params: query,
        responseType: 'blob'
    });
};

export const DeleteCustomData = (data) => {
    return axios.request({
        url: '/api/CustomData',
        data,
        method: 'delete'
    });
};

export const UpdateCustomData = (data) => {
    return axios.request({
        url: '/api/CustomData',
        data,
        method: 'put'
    });
};

export const UpdateCustomDatas = (data) => {
    return axios.request({
        url: '/api/CustomData/updates',
        data,
        method: 'put'
    });
};

export const AddCustomData = (datas) => {
    return axios.request({
        url: '/api/CustomData',
        data: datas,
        method: 'post'
    });
};

export const GetCustomTypes = () => {
    return axios.request({
        url: '/api/CustomDataTitle',
        method: 'get',
        noLoading: true
    });
};

export const GetCustomTypePages = (query) => {
    return axios.request({
        url: '/api/CustomDataTitle/page',
        method: 'get',
        params: query
    });
};

export const ClearCustomData = (type) => {
    return axios.request({
        url: '/api/CustomData/Clear/' + type,
        method: 'delete'
    });
};

/**
 * 更新数据标题
 * @param {*} data
 * @returns
 */
export const UpdateCustomeDataTitle = (data) => {
    return axios.request({
        url: '/api/CustomDataTitle',
        data,
        method: 'post'
    });
};

/**
 * 删除数据标题
 * @param {*} data
 * @returns
 */
export const DeleteCustomeDataTitle = (type, deleteData) => {
    return axios.request({
        url: `/api/CustomDataTitle/${type}/${deleteData}`,
        method: 'delete'
    });
};
