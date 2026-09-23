/**
 * 
 * 定时转换wskey
 * 转换依赖脚本 jd_base.js , 如需修改转换服务请自行修改。
 * 建议每12小时转换一次。
 * 表达式，请自行调整时间，否则同一时间转换太多会黑IP
 * 
 * 0 12 0/12 * * ?
 * 
 * 支持 量子变量 WskeyConvertService  wskey 转换服务，不指定默认使用小菜鸡的服务。
 * 自建服务教程：https://blog.csdn.net/h394047464/article/details/126680864?spm=1001.2014.3001.5502\
 * WSKEY_MIN_CONVERT_HOUR  转换小时， 如果距离上一次转换时间超过了这个时间才转换，默认8个小时，即8个小时内转换成功过就不会转换这个wskey。
 * 
 * 
 * */

const { sendNotify, getCustomData, updateCustomDatas, sleep, allEnvs } = require('./quantum');
const { convertWskey, addOrUpdateJDCookie, addWskeyCustomDataTitle, wskeyCustomDataType } = require('./jd_base');
const moment = require('moment');
const {
    syncEnvs
} = require('./quantum_syncEnv');
let WSKEY_MIN_CONVERT_HOUR = parseInt(process.env.WSKEY_MIN_CONVERT_HOUR || 8);

var successCount = 0;
var overdueCount = 0;
var failedCount = 0;
!(async () => {
    await addWskeyCustomDataTitle();
    var datas = await getWskey();
    var m1 = `开始转换，有效wskey：${datas.length}个。`
    console.log(m1)
    await sendNotify(m1, true)
    for (var i = 0; i < datas.length; i++) {
        var data = datas[i];
        if (data.Data8 && moment(data.Data8).add(WSKEY_MIN_CONVERT_HOUR, 'hours') > moment()) {
            console.log(`pin：${data.Data5}，上一次成功转换时间：${data.Data8}，未超过：${WSKEY_MIN_CONVERT_HOUR}小时，跳过转换。`)
            var data2 = await allEnvs(data.Data5, 2, true);
            if (data2.length == 0) {
                console.log(`在环境变量中未找到有效的 pt_pin 为${data.Data5}的 JD_COOKIE，还是要转换。`);
            }
            else {
                continue;
            }
        }
        // 3秒转一个，防止过快转换失败了
        await sleep(3000);
        var wskey = `pin=${data.Data5};wskey=${data.Data4};`
        var convertResult = await convertWskey(wskey, data.Data9);
        if (!convertResult.success) {
            failedCount += 1;
            console.log(`wskey：【${wskey}】，转换失败。`)
            continue;
        }
        if (!convertResult.data || convertResult.data.indexOf("pt_key=app_open") < 0) {
            var msg = `wskey失效了，账户昵称：【${data.Data6 || "-"}】，pin：【${data.Data5}】`
            console.log(msg);
            await sendNotify(msg, false, data.Data1);
            console.log("开始禁用失效wskey。")
            data.Data7 = "否";
            overdueCount += 1;
        } else {
            successCount += 1;
            console.log("开始处理提交JDCOOKIE：" + convertResult.data)
            data.Data8 = moment().format("YYYY-MM-DD HH:mm:ss");
            await addOrUpdateJDCookie(convertResult.data, data.Data1, data.Data6);
        }
    }
    await updateCustomDatas(datas);
    await sendNotify(`wskey转换完成，成功：${successCount}，失效：${overdueCount}，转换失败：${failedCount}。`, true)
    if (successCount > 0) {
        console.log("开始同步环境变量到青龙。")
        await syncEnvs(true);
    } else {
        console.log("没有转换成功的wskey，跳过同步环境变量。");
    }
})().catch((e) => {
    console.log("执行脚本出现异常了。" + e);
});

/**
 * 获取有效的wskey信息
 * */
async function getWskey() {
    var datas = await getCustomData(wskeyCustomDataType, null, null, {
        Data7: "是"
    });
    return datas;
}