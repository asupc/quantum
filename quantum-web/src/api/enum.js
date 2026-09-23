import axios from '@/libs/api.request';

export const AllEnums = () => {
    return axios.request({
        url: '/api/Enum',
        method: 'get'
    });
};

// 枚举全站稳定：模块级 Promise 记忆，logs/replay 等多页共用一次请求，不再各自 mounted 重复拉取。
// 失败时清缓存以允许下次重试（不把 rejected promise 永久留在缓存里）。
let allEnumsPromise = null;
export const AllEnumsCached = () => {
    if (!allEnumsPromise) {
        allEnumsPromise = AllEnums().catch((err) => {
            allEnumsPromise = null;
            throw err;
        });
    }
    return allEnumsPromise;
};
