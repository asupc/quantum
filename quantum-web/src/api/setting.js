import axios from '@/libs/api.request';

export const GetSetting = () => {
    return axios.request({
        url: '/api/SystemConfig',
        method: 'get'
    });
};

export const UpdateSetting = (data) => {
    return axios.request({
        url: '/api/SystemConfig',
        data,
        method: 'put'
    });
};

export const UpdatePassword = (data) => {
    return axios.request({
        url: '/api/SystemConfig/password',
        data,
        method: 'post'
    });
};

export const GetFooter = () => {
    return axios.request({
        url: '/api/SystemConfig/footer',
        method: 'get',
        noLoading: true
    });
};

export const DatabaseShrink = () => {
    return axios.request({
        url: '/api/SystemConfig/database-shrink',
        method: 'post'
    });
};
