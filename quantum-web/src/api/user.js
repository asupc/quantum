import axios from '@/libs/api.request'

// 登录（凭据 = appsettings 管理员账号；单管理员体系已移除用户管理）
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

// 扫码登录（App 管理员授权，A5.5 P2）：票据 2 分钟有效，授权后 Token 一次性返回
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
