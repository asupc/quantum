import axios from '@/libs/api.request'

// 登录（Web/App 共用 appsettings 中的唯一账号）
export const login = ({ userName, password }) => {
    const data = {
        userName,
        password
    }
    return axios.request({
        url: '/api/Login',
        data,
        method: 'post'
    })
}

// 扫码登录（App 登录账号授权）：票据 2 分钟有效，授权后 Token 一次性返回
export const QrCreate = () => {
    return axios.request({
        url: '/api/Login/qr-create',
        method: 'post'
    })
}

export const QrStatus = (ticket) => {
    return axios.request({
        url: '/api/Login/qr-status',
        params: { ticket },
        method: 'get'
    })
}
