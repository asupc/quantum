/**
 * 可用环境变量 请通过环境变量添加量子变量
 * NO_CK_NOTIFY ，说明未提交京东CK时提醒信息。
 **/

const moment = require('moment');
const {
    sendNotify, getEnvs, getCustomData
} = require('./quantum');

let user_id = process.env.user_id; //用户id
!(async () => {
    if (!user_id) {
        return;
    }
    console.log("user_id:" + user_id);
    var cks = await getEnvs("JD_COOKIE", "pt_key", 2, user_id)
    if (cks.length == 0) {
        console.log("没有Cookies信息结束任务。");
        if (process.env.NO_CK_NOTIFY) {
            await sendNotify(process.env.NO_CK_NOTIFY);
        }
        return;
    }
    var quantumSNDatas = await getCustomData("QuantumSN", null, null, {
        Data11: user_id,
        Data9: "生效中"
    });
    var message = `一共绑定了${cks.length}个京东：`;
    console.log("查询京东权重有效期。");
    var jdvisns = await getCustomData("quantum_jdvi_sn", null, null, {
        Data9: '生效中',
        Data11: process.env.user_id
    });
    for (var i = 0; i < cks.length; i++) {
        var ck = cks[i];
        var pin = ck.Value.match(/pt_pin=([^; ]+)(?=;?)/)[1]
        var name = ck.UserRemark || ck.Value.match(/pt_pin=([^; ]+)(?=;?)/) && pin;
        var overdueDate = moment(ck.CreateTime);
        var day = moment(new Date()).diff(overdueDate, 'day');


        var st = "过期，请重新获取提交";
        var jdsnMsg = ""
        if (process.env.JD_COOKIE_DEFAULT_STATUS == "false") {
            var sn = quantumSNDatas.filter((item1) => item1.Data6 == pin);
            if (sn.length > 0) {
                jdsnMsg = "，卡密有效期：" + sn[0].Data8
            }
            else {
                if (!ck.Enable) {
                    st = "请提交卡密激活";
                }
            }
        }

        message += `\n${(i + 1)}：${name}，权重：${ck.Weight}，${(ck.Enable ? `✅正常，挂机${day}天` : '❌' + st)}`

        if (jdsnMsg) {
            message += jdsnMsg
        }
        var jdevi = jdvisns.filter((item1) => item1.Data6 == pin);
        if (jdevi.length > 0) {
            message += `，权重有效期：${jdevi[0].Data8}，权重：${jdevi[0].Data12}`;
        }
    }
    console.log(message);
    await sendNotify(message);
})().catch((e) => {
    console.log("执行脚本出现异常了。");
    console.log(e);
});
