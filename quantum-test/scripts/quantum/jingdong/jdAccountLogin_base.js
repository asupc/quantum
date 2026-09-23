const {
    sendNotify,
    getCustomData,
    updateCustomData,  // 已添加引用
    addCustomData,
    sleep,
    api,
    finshStepCommandTask,
    redoStepCommandTask
} = require("./quantum");

const { addOrUpdateJDCookie, addWskeyCustomDataTitle } = require('./jd_base');


const customDataType = "jd_AutoLogin_Account";

/**
 * 提交验证码
 * @param {*} uid 会话ID
 * @param {*} code 验证码
 * @returns 
 */
async function sms(uid, code) {
    const body = {
        "uid": uid,
        "code": code
    };
    const smsResult = await api({
        url: `${process.env.script_jd_autologin_service}/sms`,
        method: "post",
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(body)
    }).json();
    return smsResult;
}

/**
 * 提交账号密码进行登录
 * @param {*} jdAccount 京东账号
 * @param {*} jdPassword  京东密码
 * @param {*} isAuto  是否自动登录
 * @returns 
 */
async function login(jdAccount, jdPassword, isAuto) {
    const body = {
        "id": jdAccount,
        "pw": jdPassword,
        "isAuto": isAuto
    };
    const loginResult = await api({
        url: `${process.env.script_jd_autologin_service}/login`,
        method: "post",
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(body)
    }).json();

    return loginResult;
}

/**
 * 检查登录结果
 * @param {*} uid  登录返回的会话ID
 * @returns 
 */
async function check(uid) {
    const body = { uid }
    const checkResult = await api({
        url: `${process.env.script_jd_autologin_service}/check`,
        method: "post",
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(body)
    }).json();
    return checkResult;
}

/**
 * 执行检查
 * @param {*} uid 
 * @returns 
 */
async function doCheck(uid, refrensh) {
    let retryCount = 0;
    do {
        /**
         * 每隔5秒检查一次状态
         */
        await sleep(1000 * 5);
        const checkResult = await check(uid);
        retryCount++;
        console.log(`第【${retryCount}】次检查登录结果：` + JSON.stringify(checkResult));
        if (checkResult.cookie) {
            const pt_pin = checkResult.cookie.match(/pt_pin=([^; ]+)(?=;?)/)[1]
            const notifyStatus = process.env.system_enable_notify;
            process.env.system_enable_notify = "false"
            await addOrUpdateJDCookie(checkResult.cookie, process.env.user_id)
            process.env.system_enable_notify = notifyStatus;
            if (!refrensh) {
                const newEntry = {
                    Type: customDataType,
                    Data1: process.env.user_id,
                    Data2: process.env.CommunicationUserName,
                    Data3: process.env.CommunicationUserId,
                    Data4: process.env.script_jd_account,
                    Data5: process.env.script_jd_password,
                    Data6: pt_pin,
                    Data7: "是"
                };
                let customDatas = await getCustomData(customDataType, null, null, { Data4: process.env.script_jd_account });
                if (customDatas && customDatas.length > 0) {
                    console.log("更新京东账号信息到自定义数据中");
                    newEntry.Id = customDatas[0].Id;
                    await updateCustomData(newEntry);
                } else {
                    await addCustomData([newEntry]);
                    console.log("新增京东账号信息到自定义数据中");
                }
                await sendNotify(`京东账号密码提交成功了!
请不要修改登录密码
否则自动登录将失效`)
                await finshStepCommandTask();
            }
            return true;
        }
        if (!refrensh && checkResult.status == "SMS") {
            await sendNotify(`本次登录需要验证短信验证码
请回复您收到的6位验证码！`);
            break;
        }
        if (checkResult.status == "wrongSMS") {
            await sendNotify(`验证码错误，请重新回复！
如长时间未收到短信验证码请回复Q结束，再重新开始。`);
            await redoStepCommandTask();
        }
        if (checkResult.status == "error") {
            if (refrensh) {
                let customDatas = await getCustomData(customDataType, null, null, {
                    Data4: process.env.script_jd_account
                });
                let msg = ""
                if (checkResult.msg.indexOf("账号或密码不正确") > -1) {
                    if (customDatas && customDatas.length > 0) {
                        customDatas[0].Data7 = "否";
                    }
                    msg = `京东账号[${customDatas[0].Data4}]密码错误
如需继续使用请重新提交。`
                } else if (checkResult.msg.indexOf("自动续期时不能使用短信验证") > -1) {
                    if (customDatas && customDatas.length > 0) {
                        customDatas[0].Data7 = "否";
                    }
                    msg = `京东账号[${customDatas[0].Data4}]
自动登录时出现短信验证
建议重新提交一次账号密码`
                } else {
                    console.log(`出现其他未知异常：【${checkResult.msg}】`);
                    await sendNotify(`京东自动登录刷新出现未知异常：
【${checkResult.msg}】`, true);
                }
                await updateCustomData(customDatas[0]);
                await sendNotify(msg, false, process.env.user_id);
            } else {
                let msg = "，请重新开始"
                if (checkResult.msg.indexOf("账号或密码不正确") > -1) {
                    msg = "账号或密码不正确" + msg
                } else if (checkResult.msg.indexOf("登录超时") > -1) {
                    msg = "登录超时" + msg
                }
                await sendNotify(msg);
            }
            await finshStepCommandTask();
            break;
        }
    } while (retryCount < 30)
}

module.exports.doCheck = doCheck;
module.exports.check = check;
module.exports.sms = sms;
module.exports.login = login;
module.exports.customDataType = customDataType;