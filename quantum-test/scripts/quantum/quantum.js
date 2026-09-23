const got = require('got');
const moment = require('moment');
var HttpsProxyAgent = require("https-proxy-agent");
process.env['NODE_TLS_REJECT_UNAUTHORIZED'] = 0;

console.log("脚本库更新时间：2023年10月18日");

//------------- 量子助手系统环境变量部分 -------------

if (!process.env.serverAddres) {
    process.env.serverAddres = "http://localhost:5088"
}

let serverAddres = process.env.serverAddres; //服务地址
let CommunicationType = process.env.CommunicationType; //通讯类型
let CommunicationId = process.env.CommunicationId; //通讯工具ID
let group_id = process.env.group_id; //群组ID


if (process.env.serverAddres && !process.env.serverAddres.endsWith("/")) {
    serverAddres = serverAddres + "/";
}
module.exports.serverAddres = serverAddres;

var request_proxy_white_list = ['xkdaili.com', 'xiongmaodaili', serverAddres]


if (process.env.request_proxy_white_list) {
    process.env.request_proxy_white_list.split("&").forEach(item => {
        request_proxy_white_list.push(item)
    })
}


var retryReponseCodes = ['401', '403', '500', '503']

const apiExtend = got.extend({
    retry: { limit: 0 },
    hooks: {
        beforeError: [
            (error) => {
                console.log(`请求时出现异常：
错误代码：【${error.code}】
请求地址：【${error.options.method} ：${error.request.options.url}】
异常信息：【${error.message}】`)
                if (process.env.system_enable_proxy == "true" && request_proxy_white_list.filter((s) => error.request.options.url.toString().indexOf(s) > -1).length == 0 && proxyInfo) {
                    console.log("当前请求使用了代理IP，清理该代理：" + proxyInfo.ip)
                    this.clearProxy();
                }
                if (retryReponseCodes.filter(n => error.message.indexOf(n) > -1).length > 0) {
                    if (!error.request.options.retryCount) {
                        error.request.options.retryCount = 0;
                    }
                    if (error.request.options.retryCount == 0) {
                        console.log("对当前请求发起一次重试。")
                        error.request.options.retryCount++;
                        return apiExtend(error.request.options)
                    }
                }
                throw error;
            }
        ],
        beforeRequest: [
            async options => {

                if (process.env.QuantumAssistantTemporaryToken && options.url.toString().indexOf(serverAddres) > -1) {
                    options.headers.Authorization = "Bearer " + process.env.QuantumAssistantTemporaryToken;
                }
                if (process.env.system_enable_proxy == "true" && request_proxy_white_list.filter((s) => options.url.toString().indexOf(s) > -1).length == 0) {
                    if (proxyInfo == null || moment(proxyInfo.expire) <= moment().add(5, "s")) {
                        proxyInfo = await getXKProxy();
                        if (proxyInfo == null) {
                            proxyInfo = await getXMProxy();
                        }
                    }
                    if (proxyInfo && moment(proxyInfo.expire) > moment()) {
                        var agent = new HttpsProxyAgent(`http://${proxyInfo.ip}:${proxyInfo.port}`);
                        options.agent = {
                            https: agent,
                            http: agent,
                        }
                    }
                }
            }
        ]
    }
});

var proxyInfo = null;

/**
 * 由脚本判断代理是否可用，如不可用则调用该方法清除代理。
 */
module.exports.clearProxy = () => {
    proxyInfo = null;
}

module.exports.api = apiExtend;

/**
 * 
 * 获取所有的青龙面板
 * 
 * */
async function getQLPanels() {
    const body = await apiExtend({
        url: serverAddres + 'api/QLPanel',
        headers: {
            Accept: 'text/plain',
        },
    }).json();
    return body.Data;
}

/**
 * 获取青龙容器中的环境变量
 * @param {any} ql 青龙ID
 * @param {any} searchValue 搜索关键字
 */
async function getQLEnvs(ql, searchValue) {
    const body = await apiExtend({
        url: serverAddres + 'api/qlPanel/envs/' + ql.Id,
        method: 'get',
        searchParams: {
            searchValue: searchValue,
            t: Date.now(),
        },
        headers: {
            "Content-Type": "application/json"
        }
    }).json();
    return body.Data.data;
}

/**
 * 删除青龙中的环境变量
 * @param {any} ql 青龙id
 * @param {any} ids 环境变量id 数组
 */
async function deleteQLEnvs(ql, ids) {
    const body = await apiExtend({
        url: serverAddres + 'api/qlPanel/envs/' + ql.Id,
        method: 'delete',
        body: JSON.stringify(ids),
        headers: {
            "Content-Type": "application/json"
        }
    }).json();
    return body.Data.data;
}

/**
 * 添加青龙环境变量
 * @param {any} ql
 * @param {any} envs
 */
async function addQLEnvs(ql, envs) {
    const body = await apiExtend({
        url: serverAddres + 'api/qlPanel/envs/' + ql.Id,
        method: 'delete',
        body: JSON.stringify(envs),
        headers: {
            "Content-Type": "application/json"
        }
    }).json();
    return body.Data.data;
}


/**
 * 获取青龙面板信息
 */
module.exports.getQLPanels = getQLPanels;

/**
 * 获取当前用户的环境变量
 * */
module.exports.getCookies = async () => {
    var envs = await getEnvs("JD_COOKIE", "pt_key", 2, process.env.user_id);
    console.log(`用户id：【${process.env.user_id}】，获取JD_COOKIE：【${envs.length}】个。`);
    var cookies = [];
    //var envCookies = [];
    //if (process.env.JD_COOKIE) {
    //    envCookies = process.env.JD_COOKIE.split("&");
    //    console.log(`系统提供JD_COOKIE环境变量：${envCookies.length}个。`)
    //}
    //if (envCookies.length == 0) {
    //    console.log("系统未提供环境变量。");
    //    return [];
    //}
    //envs = envs.filter((n => envCookies.indexOf(n.Value) > -1));

    for (var i = 0; i < envs.length; i++) {
        var env = envs[i];
        var cookie = env.Value;
        if (!cookie.match(/pt_pin=(.+?);/) || !cookie.match(/pt_key=(.+?);/)) {
            console.log(cookie + "-----不规范，已跳过。");
            continue;
        }
        //var pt_key = cookie.match(/pt_key=([^; ]+)(?=;?)/)[1]
        var pt_pin = cookie.match(/pt_pin=([^; ]+)(?=;?)/)[1]
        var reg = new RegExp("[\\u4E00-\\u9FFF]+", "g");
        if (reg.test(pt_pin)) {
            pt_pin = encodeURI(pt_pin);
            env.Value = `pt_key=${$.pt_key};pt_pin=${$.pt_pin};`
        }
        if (!env.Enable) {
            var m1 = `账号：${env.UserRemark || pt_pin}已经过期了，请重新获取提交吧！`;
            console.log(m1)
            await sendNotify(m1);
        } else {
            cookies.push(env)
        }
    }
    return cookies;
}

/**
 * 获取青龙容器中的环境变量
 * @param {any} qlPanel
 */
module.exports.getQLEnvs = getQLEnvs;


// 同步环境变量
module.exports.syncEnv = async () => {
    const body = await apiExtend({
        url: serverAddres + 'api/env/sync',
        method: "get",
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json"
        },
    }).json();
    return body.Data;
};

/**
 * 删除青龙环境变量
 * @param {any} ql
 * @param {any} ids
 */
module.exports.deleteQLEnvs = async (ql, ids) => {
    const body = await apiExtend({
        url: serverAddres + 'api/qlPanel/envs/' + ql.Id,
        body: JSON.stringify(ids),
        method: 'delete',
        headers: {
            "Content-Type": "application/json"
        }
    }).json();
    return body.Data;
};

/**
 * 直接添加环境变量到青龙容器
 * @param {any} ql
 * @param {any} envs
 */

module.exports.addQLEnvs = async (ql, envs) => {
    const body = await apiExtend({
        url: serverAddres + 'api/qlPanel/envs/' + ql.Id,
        body: JSON.stringify(envs),
        method: 'post'
    }).json();
    return body.Data;
};


/**
 * 添加环境变量（数组）
 * @param {any} env
 */
module.exports.addEnvs = async (env) => {
    const body = await apiExtend({
        url: serverAddres + 'api/env',
        method: 'post',
        body: JSON.stringify(env),
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body;
};

/**
 * 禁用环境变量值，数组
 * @param {any} envs
 */
module.exports.disableEnvs = async (envs) => {
    if (envs && envs.length > 0) {
        const body = await apiExtend({
            url: serverAddres + 'api/env/DisableEnvs',
            method: 'put',
            body: JSON.stringify(envs),
            headers: {
                Accept: 'text/plain',
                "Content-Type": "application/json-patch+json"
            },
        }).json();
        return body;
    }
}


/**
 * 获取环境变量信息，包含和青龙的关系数据
 * @param {any} key
 * @param {any} envType
 * @param {any} enable
 * @param {any} qlPanelId
 * @param {any} userId
 */
module.exports.allEnvs = async (key, envType, enable, qlPanelId, userId) => {
    const body = await apiExtend({
        url: serverAddres + 'api/env',
        method: 'get',
        searchParams: {
            key: key,
            envType: envType,
            enable: enable,
            qlPanelId: qlPanelId,
            UserId: userId,
            PageIndex: 1,
            PageSize: 999999999
        },
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body.Data.Data;
};

async function getEnvs(name, key, envType, userId) {
    const body = await apiExtend({
        url: serverAddres + 'api/env/Query',
        method: 'get',
        searchParams: {
            key: key,
            name: name,
            envType: envType,
            userId: userId,
            t: Date.now(),
            PageIndex: 1,
            PageSize: 999999999
        },
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body.Data;
};

/**
 * 获取自定义数据
 * @param {any} type 数据类型，必填
 * @param {any} startTime 数据创建时间 开始
 * @param {any} endTime 数据创建时间 截至
 * @param {any} dataQuery Data筛选字段
 */
async function getCustomData(type, startTime, endTime, dataQuery) {
    if (!type) {
        console.log("未指定type。");
        return;
    }
    if (!dataQuery) {
        dataQuery = {};
    }
    dataQuery.createTimeStart = startTime;
    dataQuery.createTimeEnd = endTime;
    const body = await apiExtend({
        url: serverAddres + 'api/CustomData/' + type,
        method: 'get',
        searchParams: dataQuery,
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body.Data.Data;
};


async function deleteCustomData(ids) {
    const body = await apiExtend({
        url: serverAddres + `api/CustomData`,
        method: 'delete',
        body: JSON.stringify(ids),
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body;
}

async function updateCustomData(data) {
    const body = await apiExtend({
        url: serverAddres + `api/CustomData`,
        method: 'put',
        body: JSON.stringify(data),
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body;
}

/**
 * 批量修改数据管理 （数组）
 * */
async function updateCustomDatas(datas) {
    const body = await apiExtend({
        url: serverAddres + `api/CustomData/updates`,
        method: 'put',
        body: JSON.stringify(datas),
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body;
}

/**
 * 添加自定义数据
 * @param {[{}]} data 数组
 */
async function addCustomData(data) {
    const body = await apiExtend({
        url: serverAddres + `api/CustomData`,
        method: 'post',
        body: JSON.stringify(data),
        headers: {
            Accept: '*/*',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body.Data;
}


async function deleteEnvByIds(ids) {
    const body = await apiExtend({
        url: serverAddres + `api/env/deletes`,
        method: 'delete',
        body: JSON.stringify(ids),
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body;
}

/**
 * 发送通知消息
 * @param {*} content 发送消息内容 可以是 文本，
 * 或者 {msg:"",MessageType:1}  MessageType=1 即文本，2 为图片，3 为视频，如果是图片地址必须是完整的http 地址。 
 * 或者是[{msg:"",MessageType:1},{msg:"",MessageType:2}] 数组时，会根据通讯工具选择是否合并消息发送或者分开发送。 
 * @param {*} isManager 是否发送给管理员
 * @param {*} userId 指定发送给某人 （不@时传入字符串 NULL）
 * @param {*} groupId 发送到群 
 * @param {*} communicationType   QQ = 1,  公众号 = 2,  WxPusher = 3, VLW = 4, Web = 5
 * @returns 
 */
async function sendNotify(content, isManager, userId, groupId, communicationType) {
    var uuid = process.env.user_id;
    if (isManager && !process.env.ManagerQQ) {
        console.log(`消息内容：
${content}
指定发送给管理员，但似乎没有配置管理员QQ？`);
        return;
    }
    if (isManager) {
        uuid = process.env.ManagerQQ;
    } else {
        if (userId) {
            uuid = userId;
            if (userId == "NULL") {
                uuid = null
            }
        } else {
            uuid = process.env.user_id;
        }
    }
    if (communicationType) {
        CommunicationType = communicationType;
    } else {
        CommunicationType = process.env.CommunicationType
    }
    if (groupId) {
        group_id = groupId;
        if (groupId == "NULL") {
            group_id = null;
        }
    } else {
        group_id = process.env.group_id
    }


    if (serverAddres) {
        var body = {
            message: `${content}`,
            title: "小助手通知",
            CommunicationType: CommunicationType,
            CommunicationId: CommunicationId,
            TextToPicture: process.env.TextToPicture,
            user_id: uuid,
            group_id: isManager ? "" : group_id
        };

        var bodys = [];

        if (content instanceof Array) {
            if (CommunicationType == 1) {
                var msg = ""
                for (var i = 0; i < content.length; i++) {
                    if (content[i].MessageType == 1) {
                        msg += content[i].msg + "\r";
                    } else if (content[i].MessageType == 2) {
                        msg += `[CQ:image,file=${content[i].msg},type=show,id=40000,cache=0]` + "\r";
                    }
                }
                body.message = msg;
                body.MessageType = 1;
                bodys.push(body);
            }
            else {
                for (var i = 0; i < content.length; i++) {
                    var b = JSON.parse(JSON.stringify(body));
                    b.message = content[i].msg;
                    b.MessageType = content[i].MessageType;
                    bodys.push(b);
                }
            }
        } else if (Object.prototype.toString.call(content) === '[object Object]') {
            body.message = content.msg;
            body.MessageType = content.MessageType || 1
            bodys.push(body);
        } else {
            body.message = content;
            body.MessageType = 1;
            bodys.push(body);
        }

        if (process.env.system_enable_notify === "true") {
            for (var i = 0; i < bodys.length; i++) {
                var b = JSON.stringify(bodys[i]);
                const body = await apiExtend({
                    url: serverAddres + `api/Notifiy`,
                    method: 'post',
                    body: b,
                    headers: {
                        Accept: 'text/plain',
                        "Content-Type": "application/json-patch+json"
                    },
                }).json();

                if (body.Data) {
                    console.log('发送通知消息成功！');
                }
                else {
                    console.log(`发送通知消息异常\n${JSON.stringify(body)}`,);
                }
            }
        } else {
            console.log("当前脚本未开启通知。忽略以下通知内容：")
            bodys.forEach((item) => {
                console.log(`用户：【${item.user_id}】，消息内容：【${item.message}】，通讯方式：【${item.MessageType}】`)
            });
        }
    }
}

/**
 * 获取环境变量
 * @param {any} name 环境变量名称，全匹配 允许空
 * @param {any} key 环境变量值，模糊匹配 允许空
 * @param {any} envType 环境变量类型 允许空
 * @param {any} userId 用户id 允许空
 */
module.exports.getEnvs = getEnvs;

/**
 * 发送通知消息
 * @param {any} content 发送消息内容
 * @param {any} isManager 是否发送给管理员
 * @param {any} userId 指定接受消息的用户ID
 */
module.exports.sendNotify = sendNotify;

/**
 * 发送通知消息
 * @param {any} content 发送消息内容
 * @param {any} isManager 是否发送给管理员
 * @param {any} userId 指定接受消息的用户ID
 */
module.exports.sendNotify2 = sendNotify;

/**
 * 通过账号id集合删除环境变量
 * */
module.exports.deleteEnvByIds = deleteEnvByIds;

/**
 * 获取自定义数据
 * @param {any} type 数据类型(必填)
 * @param {any} startTime 开始时间
 * @param {any} endTime 结束时间
 */
module.exports.getCustomData = getCustomData;

/**
 * 删除自定义数据
 *
 * @param {any} ids 数据id 集合
 */
module.exports.deleteCustomData = deleteCustomData;


/**
 * 修改自定义数据
 * @param {any} data 
 */
module.exports.updateCustomData = updateCustomData;

/**
 * 批量修改自定义数据 
 * @param {any} data 
 */
module.exports.updateCustomDatas = updateCustomDatas;

/**
 * 添加自定义数据
 * @param {any} data 数组
 */
module.exports.addCustomData = addCustomData;


/**
 * 获取用户信息
 * */
module.exports.getUserInfo = async () => {

    console.log(JSON.stringify(process.env))

    let body = await apiExtend({
        url: serverAddres + 'api/User/' + process.env.user_id,
        method: 'get',
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    });

    body = body.json()

    return body.Data;
}

/**
 * 更新用户信息
 * @param {any} user
 */
module.exports.updateUserInfo = async (user) => {
    const body = await apiExtend({
        url: serverAddres + 'api/User',
        method: 'put',
        body: JSON.stringify(user),
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body;
}

/**
 * 查询所有的用户信息
 * */
module.exports.getUser = async () => {
    const body = await apiExtend({
        url: serverAddres + 'api/User',
        method: 'get',
        searchParams: {
            PageIndex: 1,
            PageSize: 999999999
        },
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body.Data;
}


/**
 * 删除用户信息
 * @param {any} ids
 */
module.exports.deleteUser = async (ids) => {
    const body = await apiExtend({
        url: serverAddres + `api/User`,
        method: 'delete',
        body: JSON.stringify(ids),
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body;
}

/**
 * 获取自定义数据标题信息
 * @param {any} type
 */
module.exports.getCustomDataTitle = async (type) => {
    const body = await apiExtend({
        url: serverAddres + 'api/CustomDataTitle/' + type,
        method: 'get',
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body.Data;
};

/**
 * 添加自定义数据标题
 * @param {any} data 集合
 */
module.exports.addOrUpdateCustomDataTitle = async (data) => {
    const body = await apiExtend({
        url: serverAddres + `api/CustomDataTitle`,
        method: 'post',
        body: JSON.stringify(data),
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body;
};


async function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms))
}

/**
 * 线程等待
 * @param {any} ms 毫秒
 */
module.exports.sleep = sleep;

/**
 * 获取一个随机字符串
 * @param {any} len 字符串长度
 * @param {any} radix 
 * @param {any} append 在随机字符串中追加自定义字符
 */
module.exports.uuid = function (len, radix, append) {
    var chars = ('0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz' + append).split('');
    var uuid = [],
        i;
    radix = radix || chars.length;
    for (i = 0; i < len; i++) uuid[i] = chars[0 | Math.random() * radix];
    return uuid.join('');
};

/**
 * 设置群全员禁言
 * @param {any} groups 群号，多个用&隔开
 * @param {boolean} enable 是否禁言
 * @param {any} 通知消息
 */
module.exports.set_group_whole_ban = async (groups, enable, notify) => {
    const body = await apiExtend({
        url: serverAddres + `api/GroupManagement/set_group_whole_ban/${groups}/${enable}?notify=${notify}`,
        method: 'get',
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    return body;
}

/**
 * 
 * 青龙相关封装接口
 * 
 * */
module.exports.qinglong = {

    /**
     * 查找所有青龙指定任务信息
     * @param {any} taskName 任务名称
     */
    getTask: async function (taskName) {
        const body = await apiExtend({
            url: serverAddres + `api/QLTask?Key=${taskName}&PageIndex=1&PageSize=999`,
            method: 'get',
            headers: {
                "Content-Type": "application/json-patch+json"
            },
        }).json();
        return body.Data;
    },
    /**
     * 运行青龙任务
     * @param {any} data 运行的任务信息，参数形式请参照方法内
     */
    runTask: async function (data) {
        const body = await apiExtend({
            url: serverAddres + `api/QLTask/run`,
            method: 'post',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(data)
        }).json();
        return body.Data;
    },
    /**
     * 搜索青龙中的环境变量
     * @param {any} name
     */
    getEnvs: getQLEnvs,

    /**
     * 删除青龙中的环境变量
     */
    deleteEnvs: deleteQLEnvs,

    /**
     * 向青龙中添加环境变量
     */
    addEnvs: addQLEnvs
}

/**
 * 手动结束多步骤任务
 */
module.exports.finshStepCommandTask = async () => {
    if (!process.env.StepCommandTaskThreadId) {
        console.log("无多步骤任务线程ID 信息，跳过。");
        return;
    }
    const body = await apiExtend({
        url: serverAddres + `api/Task/finish/${process.env.StepCommandTaskThreadId}`,
        method: 'get',
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    console.log("手动结束当前任务。");
    return body;
};

/**
 * 重试当前子任务
 */
module.exports.redoStepCommandTask = async () => {
    if (!process.env.StepCommandTaskThreadId) {
        console.log("无多步骤任务线程ID 信息，跳过。");
        return;
    }
    const body = await apiExtend({
        url: serverAddres + `api/Task/redo/${process.env.StepCommandTaskThreadId}`,
        method: 'get',
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
    }).json();
    console.log("重试当前子任务。");
    return body;
};

/**
 * 为当前多步骤任务新增一个自定义变量，任务结束将自动销毁，如果当前任务有同名的变量将被替换
 */
module.exports.stepCommandTaskAddEnv = async (name, value) => {
    if (!process.env.StepCommandTaskThreadId) {
        console.log("无多步骤任务线程ID 信息，跳过。");
        return;
    }
    var pattern = /^[a-zA-Z][a-zA-Z0-9_]{1,64}$/
    if (!pattern.test(name)) {
        console.log("环境变量 只能包含数字和字母下划线长度1-64位");
        return;
    }
    const body = await apiExtend({
        url: serverAddres + `api/Task/AddEnv/${process.env.StepCommandTaskThreadId}`,
        method: 'post',
        headers: {
            Accept: 'text/plain',
            "Content-Type": "application/json-patch+json"
        },
        body: JSON.stringify({
            name: name,
            value: value
        })
    }).json();
    return body;
};

/**
 * 扣除当前用户积分
 * @param {Integer} USE_SCORE 扣除积分数量
 * */
module.exports.deductionIntegral = async (USE_SCORE) => {
    var userId = process.env.user_id;
    const body = await apiExtend({
        url: serverAddres + `api/User/DeductionIntegral/${userId}/${USE_SCORE}`,
        headers: {
            'User-Agent': 'apifox/1.0.0 (https://www.apifox.cn)',
        },
        method: 'get',
    }).json();
    return body;
}

var getProxyRetryCount = 0;

async function getXMProxy() {
    var XM_PROXY = process.env.XM_PROXY;
    if (XM_PROXY == null) {
        console.log("未配置熊  XM_PROXY API地址");
        return null;
    }
    ///http://pandavip.xiongmaodaili.com/xiongmao-web/apiPlus/vgl?secret=&orderNo=&count={0}&isTxt=0&proxyType=1&validTime=1&removal=1&cityIds=
    if (XM_PROXY.indexOf("validTime") < 0) {
        XM_PROXY += "&validTime=1"
    }
    if (XM_PROXY.indexOf("count") < 0) {
        XM_PROXY += "&count=1"
    }
    console.log("开始获取熊猫代理");
    var result = null;
    getProxyRetryCount++;
    try {
        var options = {
            'method': 'get',
            'url': XM_PROXY,
            'headers': {
                'Content-Type': 'application/json'
            }
        };
        var response = await apiExtend(options);
        console.log("获取代理IP结果：" + response.body);
        result = JSON.parse(response.body);
    } catch (e) {
        console.log("熊猫代理获取异常，等待一下尝试重新获取");
        await sleep(1200);
        await getProxy();
    }
    if (getProxyRetryCount >= 10) {
        console.log("获取代理错误次数超过10次，不再获取代理。")
        return null;
    }
    if (result && result.code == "0" && result.obj) {
        var proxy = result.obj[0];
        getProxyRetryCount = 0
        return {
            ip: proxy.ip,
            port: proxy.port,
            expire: proxy.expire
        };
    } else if (result.code == "-102") {
        console.log(result.msg)
        await sleep(1200);
        await getProxy();
    }
}

async function getXKProxy() {
    var XK_PROXY = process.env.XK_PROXY || "http://api2.xkdaili.com/tools/XApi.ashx?apikey=XK3ADBB87FDE01D92C97&qty=1&format=json&split=0&sign=cf955b8f84b47e97ff50e112773198d8&fmt=6";
    if (XK_PROXY == null) {
        console.log("未配置星空代理API地址。");
        return null;
    }
    if (XK_PROXY.indexOf("&fmt=6") < 0) {
        XK_PROXY += "&fmt=6";
    }
    var result = null;
    getProxyRetryCount++;
    try {
        var options = {
            'method': 'get',
            'url': XK_PROXY,
        };
        var result = await apiExtend(options).json();
    } catch (e) {
        console.log("星空代理获取异常，尝试重新获取。");
        await sleep(1000);
        return await getXKProxy();
    }
    if (getProxyRetryCount >= 10) {
        console.log("获取代理错误次数超过10次，不再获取代理。")
        return null;
    }
    if (result && result.status == 100 && result.data) {
        var proxy = result.data[0];
        console.log("获取到星空代理：" + JSON.stringify(proxy))
        getProxyRetryCount = 0
        return proxy;
    } else {
        console.log("星空代理获取失败了：" + result.info)
        await sleep(1000);
        return await getXKProxy();
    }
}

/**
 * 获取熊猫代理IP
 */
module.exports.getXMProxy = getXMProxy;


/**
 * 获取星空代理IP
 */
module.exports.getXKProxy = getXKProxy;