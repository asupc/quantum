/**
 * 用户提交京东wskey
 * 
 * wskey将存放在 自定义数据表中，
 * 请通过数据管理查看信息
 * */
const { sendNotify } = require('./quantum');

const { convertWskey, GetJDUserInfoUnion, addOrUpdateJDCookie, addOrUpdateWskey, addWskeyCustomDataTitle } = require('./jd_base');

let user_id = process.env.user_id; //用户id
let command = process.env.command;


let key = '';
let pin = '';

!(async () => {
    let wskeys = command.split("&");
    await addWskeyCustomDataTitle();
    for (var i = 0; i < wskeys.length; i++) {
        var wskey = wskeys[i] + ";";
        if (!wskey) {
            continue;
        }
        let userInfo = await GetJDUserInfoUnion(wskey);

        if (!userInfo || !userInfo.data || userInfo.retcode != "0") {
            await sendNotify(`wskey似乎失效了：【${wskey}】`);
            continue;
        }
        if (wskey.indexOf("pin") < 0) {
            pin = userInfo.data.userInfo.baseInfo.curPin;
            wskey = `pin=${pin};${wskey};`
        }
        wskey = wskey.replace(/[\r\n]/g, "");
        try {
            key = wskey.match(/wskey=([^; ]+)(?=;?)/)[1]
            pin = wskey.match(/pin=([^; ]+)(?=;?)/)[1]
        }
        catch (e) {
            console.log("wskey：【 " + wskey + "】格式不对，已跳过");
            continue;
        }
        var reg = new RegExp("[\\u4E00-\\u9FFF]+", "g");
        if (reg.test(pin)) {
            pin = encodeURI(pin);
        }
        wskey = `wskey=${key};pin=${pin};`
        console.log("开始将wskey转换成app_open格式：" + wskey)
        var convertResult = await convertWskey(wskey);

        if (!convertResult.success || convertResult.data.indexOf("pt_key=app_open") < 0) {
            console.log("wskey转换失败了，给韭菜发送通知。");
            await sendNotify(`wskey提交失败：【${wskey}】`);
            continue;
        }
        //var cookie = convertResult.data;
        //console.log("开始获取京东账户基本信息");
        //var userInfo = await GetJDUserInfoUnion(cookie)
        //console.log("获取京东账户基本信息结果：" + JSON.stringify(userInfo));

        var msg = `提交成功辣！
账号昵称：${userInfo.data.userInfo.baseInfo.nickname}
用户等级：${userInfo.data.userInfo.baseInfo.levelName}
剩余京豆：${userInfo.data.assetInfo.beanNum}
剩余红包：${userInfo.data.assetInfo.redBalance}`;
        await sendNotify(msg);
        await addOrUpdateWskey(key, pin, userInfo.data.userInfo.baseInfo.nickname)
        console.log("开始处理提交JDCOOKIE：" + convertResult.data)
        await addOrUpdateJDCookie(convertResult.data, user_id, userInfo.data.userInfo.baseInfo.nickname);
    }
})().catch((e) => {
    console.log("脚本异常：" + e);
});
