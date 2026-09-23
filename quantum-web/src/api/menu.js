import axios from '@/libs/api.request';

export function GetMenus() {
    return axios.request({
        url: '/api/Menu',
        method: 'get'
    });
}

export function CreateMenu(data) {
    return axios.request({
        url: '/api/Menu',
        data,
        method: 'post'
    });
}

export function UpdateMenu(data) {
    return axios.request({
        url: '/api/Menu',
        data,
        method: 'put'
    });
}

export function DeleteMenu(name) {
    return axios.request({
        url: `/api/Menu/${encodeURIComponent(name)}`,
        method: 'delete'
    });
}

export function ResetMenu() {
    return axios.request({
        url: '/api/Menu/reset',
        method: 'post'
    });
}
