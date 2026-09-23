const {
    api
} = require('./quantum');

/**
 * H5ST 服务地址（小菜鸡标准）
 */
const h5stServer = process.env.scripts_h5stserver;

module.exports.h5st = async (functionId, body) => {
    if (!h5stServer) {
        console.log("请指定H5ST服务地址...")
        return {};
    }
    var config = {
        method: 'post',
        url: h5stServer + functionId,
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(body)
    };
    const result = await api(config).json();
    return result;
}

module.exports.universal = async (functionId, body, config) => {
    if (!h5stServer) {
        console.log("请指定H5ST服务地址...")
        return {};
    }
    var data = {
        method: 'post',
        url: h5stServer + 'universal',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({
            functionId: functionId,
            body: body,
            config: config
        })
    };
    const result = await api(data).json();
    return result;
}



