import axios from '@/libs/api.request'

export const Version = () => {
    return axios.request({
        url: '/api/Login/Version',
        method: 'get'
    })
}
