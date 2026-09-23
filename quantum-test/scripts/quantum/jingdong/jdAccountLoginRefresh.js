/**
 * 京东账号密码登录刷新
 * 如账号过期，或超过N小时未登录刷新则自动登录
 * 
 * 指令强制刷新登录时间单位小时 未指定默认16小时
 * scripts_jdAccountLoginrefrenshInterval 
 * 
 * 
 */
const {
    sendNotify,
    getCustomData,
    getEnvs,
} = require("./quantum");

const { doCheck, login, customDataType } = require('./jdAccountLogin_base');

let moment = require('moment');

/**
 * 强制自动登录刷新时间间隔（小时）
 * 即便ck没有失效，超过这个时间也会自动登录
 */

let refrenshInterval = 16;

!(async () => {
    try {
        refrenshInterval = parseInt(process.env.scripts_jdAccountLoginrefrenshInterval)
    } catch {
        refrenshInterval = 16
    }

    let customDatas = await getCustomData(customDataType, null, null, { Data7: "是" });
    let successCount = 0;
    let failCount = 0;
    let skipCount = 0;
    for (let x = 0; x < customDatas.length; x++) {
        const cdata = customDatas[x];
        const envs = await getEnvs("JD_COOKIE", cdata.Data6, 2)
        console.log(`开始处理PIN：【${cdata.Data6}】，获取环境变量【${envs.length}】个`)
        if (!envs || envs.length <= 0 || !envs[0].Enable || moment(envs[0].UpdateTime) < moment().add(-refrenshInterval, "hours")) {
            console.log(`环境变量失效或更新时间超过[${refrenshInterval}]小时，开始自动登录获取ck`);
            process.env.user_id = cdata.Data1
            process.env.script_jd_account = cdata.Data4
            process.env.script_jd_password = cdata.Data5

            const result = await login(cdata.Data4, cdata.Data5, true)
            const checkResult = await doCheck(result.uid, true);
            /**
             * 刷新登录成功不提醒
             * 需要提醒将下面三行注释删掉
             */
            // if (envs && envs.length > 0 && !envs[0].Enable && checkResult) {
            //     await sendNotify(`京东账号【${envs[0].UserRemark}】密码自动登录获取CK成功！`)
            // }
            if (checkResult) {
                successCount++
            } else {
                failCount++
            }
        } else {
            console.log("未达成自动登录条件，跳过。。。。")
            skipCount++;
        }
    }
    const msg = `京东账号密码自动登录刷新结果
成功：【${successCount}】
失败：【${failCount}】
跳过：【${skipCount}】`
    // 想要通知到管理员直接放开注释
    // await sendNotify(msg, true)

})().catch(e => {
    console.error("【jdAccountLoginRefresh】出现错误:", e);
});
