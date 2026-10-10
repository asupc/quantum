import axios from '@/libs/api.request'

// 首次部署初始化向导（匿名端点；仅系统未初始化期间有效，初始化完成后后端即关闭）
// 返回信封 { Code, Message, Data } 由 axios 拦截器解包，这里直接拿到 Data。

export const GetSetupStatus = () => {
    return axios.request({
        url: '/api/Setup/status',
        method: 'get',
        noLoading: true
    })
}

// 数据库试连（向导第 2 步「测试连接」）：{ DBType, DBAddress }
export const TestSetupDatabase = (data) => {
    return axios.request({
        url: '/api/Setup/test-database',
        data,
        method: 'post'
    })
}

// 完成初始化：{ UserName, PassWord, DBType, DBAddress, Scripts }
export const CompleteSetup = (data) => {
    return axios.request({
        url: '/api/Setup/complete',
        data,
        method: 'post'
    })
}
