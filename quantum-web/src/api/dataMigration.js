import axios from '@/libs/api.request'

export const DataMigration = (data) => {
    return axios.request({
        url: '/api/DataMigration',
        data,
        method: 'post'
    })
}
