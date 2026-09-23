/**
 * 可用环境变量 请通过环境变量添加量子变量
 *
 * NO_CK_NOTIFY ，说明未提交京东CK时提醒信息。
 * CK_Failure_Notify   （检测到失效CK是否通知管理员，默认通知，不需要通知则配置为 false）
 *
 * */

if (!process.env.NO_CK_NOTIFY) {
    process.env.NO_CK_NOTIFY = "您没有提交CK。请按照教程获取CK发送给机器人。";
}
/**
 * 
 * 京东CK失效检查
 * 可用环境变量CK_Failure_Notify  失效CK是否通知管理员， 默认不通知，如果需要通知请设置量子变量CK_Failure_Notify，值为true
 * 
 * */
let CK_Failure_Notify = process.env.CK_Failure_Notify == "true"; //失效CK是否通知管理员

const { disableEnvs, sendNotify, getEnvs, getCustomData } = require('./quantum');



const { customDataType } = require('./jdAccountLogin_base');
const { islogin } = require('./jd_base');


!(async () => {
    var cookiesArr = await getEnvs("JD_COOKIE", null, 2);
    var managerNotifyMessage = "";
    var overdueCKs = [];
    process.env.CommunicationType = "";
    for (let i = 0; i < cookiesArr.length; i++) {
        if (cookiesArr[i].Value && cookiesArr[i].Enable) {
            const cookie = cookiesArr[i].Value;
            var pt_pin = cookie.match(/pt_pin=([^; ]+)(?=;?)/)[1]
            var UserName = (cookie.match(/pt_pin=([^; ]+)(?=;?)/) && pt_pin)
            var UserName2 = decodeURI(UserName);
            console.log(`开始检测【京东账号】${UserName2} ....\n`);

            let isLogin = true;
            try {
                isLogin = await islogin(cookie);
            } catch (e) {
                console.log("检测CK出现异常，" + cookie);
                console.log("异常信息，" + JSON.stringify(e));
                continue;
            }
            if (!isLogin) {
                console.log(cookie + "失效，自动禁用失效COOKIE！")
                if (cookiesArr[i].UserId && cookie.indexOf("app_open") == -1) {
                    let msg = `账号：【${cookiesArr[i].UserRemark}】已失效`
                    let customDatas = await getCustomData(customDataType, null, null, {
                        Data7: "是",
                        Data6: pt_pin
                    });
                    if (customDatas.length > 0) {
                        msg += `
该账号已提交账号密码，稍后将自动为您登录获取CK`
                        //提交了账号密码的过期不提醒。需要提醒将注释闪电
                        // await sendNotify(msg, false, cookiesArr[i].UserId);
                    } else {
                        //这里的【密码登录】请自行修改为京东账号密码登录的触发指令
                        msg += `
请重新获取提交
建议提交账号密码，失效后自动为您登录获取
请回复：【密码登录】`
                        await sendNotify(msg, false, cookiesArr[i].UserId);
                    }
                }
                if (CK_Failure_Notify) {
                    managerNotifyMessage += `pt_pin：${pt_pin || '-'}，账号名：${UserName2}，过期！\n`
                }
                overdueCKs.push(cookie)
            }
        }
    }
    if (CK_Failure_Notify && managerNotifyMessage) {
        await sendNotify(managerNotifyMessage, true);
    }
    if (overdueCKs && overdueCKs.length > 0) {
        console.log("禁用失效返回结果:" + JSON.stringify(await disableEnvs(overdueCKs)));
    } else {
        console.log("无过期CK.");
    }
})().catch((e) => {
    console.log("checkCookie.js 执行异常信息：" + e)
})