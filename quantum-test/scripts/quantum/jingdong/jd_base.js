/**
 * 京东基础方法脚本
 * */

if (!process.env.NO_CK_NOTIFY) {
    process.env.NO_CK_NOTIFY = "您没有提交CK。请按照教程获取CK发送给机器人。";
}

const { uuid, deductionIntegral, getUserInfo, sendNotify, addEnvs, allEnvs, api, getCustomData, updateCustomData, addCustomData, addOrUpdateCustomDataTitle
} = require('./quantum');

const wskeyCustomDataType = "wskey_record";
const ProCustomDataType = "Prowskey_record";

const moment = require('moment');
/**
 * 检查京东ck登录状态
 * @param {any} jdCookie
 */
module.exports.islogin = islogin;

async function islogin(jdCookie) {
    try {
        const options = {
            url: 'https://plogin.m.jd.com/cgi-bin/ml/islogin',
            headers: {
                "Cookie": jdCookie,
                "referer": "https://h5.m.jd.com/",
                "User-Agent": "jdapp;iPhone;10.1.2;15.0;network/wifi;Mozilla/5.0 (iPhone; CPU iPhone OS 15_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148;supportJDSHWK/1",
            },
            method: 'get',
        }
        const body = await api(options).json();
        return (body.islogin == "1");
    } catch (e) {
        console.log(" https://plogin.m.jd.com/cgi-bin/ml/islogin 验证登录状态请求异常。");
    }
}

async function getJD_COOKIE_Pin_status(pin) {
    if (process.env.JD_COOKIE_DEFAULT_STATUS == "false") {
        var dd = await getCustomData("QuantumSN", null, null, {
            Data6: pin,
            Data9: "生效中"
        });
        console.log(`【${pin}】，卡密状态：【${(dd && dd.length > 0)}】`)
        return dd && dd.length > 0;
    }
    return true;
}

/**
 * 检查并添加cookie
 * @param {*} cookie 
 * @returns 
 */
async function checkAddCookie(cookie) {
    let user_id = process.env.user_id;
    let ADD_COOKIE_USE_SCORE = (process.env.ADD_COOKIE_USE_SCORE || 0) * 1;

    if (ADD_COOKIE_USE_SCORE > 0) {
        user = (await getUserInfo()) || {};
        if (!user || user.MaxEnvCount < ADD_COOKIE_USE_SCORE) {
            await sendNotify(`该操作需要${ADD_COOKIE_USE_SCORE}积分
您当前积分剩余：${user.MaxEnvCount}`)
            return;
        }
    }

    var pt_key = "";
    var pt_pin = ""
    if (cookie.indexOf("pt_pin") < 0) {
        cookie = cookie + "pt_pin=" + uuid(8) + ";"
    }
    cookie = cookie.replace(/[\r\n]/g, "");
    try {
        pt_key = cookie.match(/pt_key=([^; ]+)(?=;?)/)[1]
        pt_pin = cookie.match(/pt_pin=([^; ]+)(?=;?)/)[1]
    }
    catch (e) {
        console.log("CK： " + cookie + "格式不对，已跳过");
        return;
    }
    if (!pt_key || !pt_pin) {
        return;
    }
    user_id = cookie.match(/qq=([^; ]+)(?=;?)/)
    if (user_id) {
        user_id = user_id[1];
    } else {
        user_id = process.env.user_id;
    }
    //处理pt_pin中带中文的问题
    var reg = new RegExp("[\\u4E00-\\u9FFF]+", "g");
    if (reg.test(pt_pin)) {
        pt_pin = encodeURI(pt_pin);
    }
    cookie = `pt_key=${pt_key};pt_pin=${pt_pin};`
    let UserName = pt_pin
    let UserName2 = decodeURI(UserName);

    let loginState = false;
    try {
        loginState = await islogin(cookie);
    } catch (e) {
        console.log("检测CK出现异常，" + cookie);
        console.log("异常信息，" + JSON.stringify(e));
        await sendNotify("检查账号登录状态异常，建议稍后重新提交。");
        return;
    }
    if (!loginState) {
        await sendNotify(`【${cookie}】提交失败，Cookie可能过期了`)
        return;
    }

    if (ADD_COOKIE_USE_SCORE && ADD_COOKIE_USE_SCORE > 0) {
        var result = await deductionIntegral(ADD_COOKIE_USE_SCORE)
        if (result.Code != 200) {
            await sendNotify(result.Message);
            return false;
        }
    }
    let nickName = UserName2;
    let jdInfo = await GetJDUserInfoUnion(cookie);
    let msg = "京东账号提交成功！";
    if (jdInfo.retcode == 0) {
        nickName = jdInfo.data.userInfo.baseInfo.nickname || nickName
        msg += `
用户等级：${jdInfo.data.userInfo.baseInfo.levelName}
京东昵称：${jdInfo.data.userInfo.baseInfo.nickname || nickName}`
        try {
            if (jdInfo.data.assetInfo.beanNum) {
                msg += "\r\n剩余京豆：" + jdInfo.data.assetInfo.beanNum
            }
            if (jdInfo.data.assetInfo.redBalance && parseInt(jdInfo.data.assetInfo.redBalance) > 0) {
                msg += "\r\n剩余红包：" + jdInfo.data.assetInfo.redBalance
            }
        } catch {

        }
    } else {
        console.log("获取账号基本信息限流。。")
        msg += `
未查询账户基本信息，建议稍后查询！`
    }
    await sendNotify(msg);
    await addOrUpdateJDCookieEnv(cookie, process.env.user_id, nickName);
}

async function addOrUpdateJDCookieEnv(jdCookie, user_id, nickname) {
    var pt_key = jdCookie.match(/pt_key=([^; ]+)(?=;?)/)[1]
    var pt_pin = jdCookie.match(/pt_pin=([^; ]+)(?=;?)/)[1]
    if (!nickname) {
        nickname = pt_pin
    }
    if (!pt_key || !pt_pin) {
        return;
    }
    var c = {
        Name: "JD_COOKIE",
        Enable: await getJD_COOKIE_Pin_status(pt_pin),
        Value: `pt_key=${pt_key};pt_pin=${pt_pin};`,
        UserRemark: nickname,
        UserId: user_id,
        EnvType: 2
    }
    var data2 = await allEnvs(pt_pin, 2);
    var temp = null;
    if (data2.length > 0) {
        console.log("pt_pin存在，尝试更新JD_COOKIE");
        c.Id = data2[0].Id;
        c.Weight = data2[0].Weight;

        if (nickname == pt_pin) {
            if (!data2[0].UserRemark) {
                c.UserRemark = nickname;
            } else {
                c.UserRemark = data2[0].UserRemark;
            }
        } else {
            c.UserRemark = nickname;
        }
        c.QLPanelEnvs = data2[0].QLPanelEnvs;
        c.Remark = data2[0].Remark;
        if (process.env.UPDATE_COOKIE_NOTIFY) {
            await sendNotify(`Cookie更新通知
用户ID：${process.env.CommunicationUserId}
用户昵称：${process.env.CommunicationUserName || ""}
京东昵称：${nickname}`, true)
        }
        temp = data2[0];
    } else {
        console.log("全新韭菜上线拉！");
        c.Id = null;
        if (process.env.ADD_COOKIE_NOTIFY) {
            await sendNotify(`Cookie新增通知
用户ID：${process.env.CommunicationUserId}
用户昵称：${process.env.CommunicationUserName || ""}
京东昵称：${nickname}`, true)
        }
    }
    var data = await addEnvs([c]);
    // console.log("环境变量提交结果：" + JSON.stringify(data));
    return temp;
}

async function GetJDUserInfoUnion(jdCookie) {
    const options = {
        url: "https://me-api.jd.com/user_new/info/GetJDUserInfoUnion",
        headers: {
            Host: "me-api.jd.com",
            Accept: "*/*",
            Connection: "keep-alive",
            Cookie: jdCookie,
            "User-Agent": "jdapp;iPhone;9.4.4;14.3;network/4g;Mozilla/5.0 (iPhone; CPU iPhone OS 14_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148;supportJDSHWK/1",
            "Accept-Language": "zh-cn",
            "Referer": "https://home.m.jd.com/myJd/newhome.action?sceneval=2&ufc=&",
            "Accept-Encoding": "gzip, deflate, br"
        }
    }
    const body = await api(options).json();
    return body;
}

/**
 * 将wskey 转换成 app_open
 * wskey 转换服务可以替换成其他的，兼容标准的服务
 * 或者根据自己服务调整此处代码即可
 * @param {any} wskey
 */
module.exports.convertWskey = async (wskey, bbkJd) => {
    console.log("wskey是否bbkJd：" + bbkJd);
    if (bbkJd == "是") {
        if (!process.env.bbk_qr_url_jd) {
            console.log("未配置量子变量：bbk_qr_url_jd，该变量为BBK 京东扫码地址如：http://www.xyz.xyz:3081")
            return {
                success: false,
                data: ""
            };
        }
        if (!process.env.bbk_token) {
            console.log("未配置环境变量 bbk_token 无法完成转换");
            return {
                success: false,
                data: " "
            };
        }
        const options = {
            url: process.env.bbk_qr_url_jd + `/d/convert?token=${process.env.bbk_token}&wskey=${wskey.match(/wskey=([^; ]+)(?=;?)/)[1]}&pin=${wskey.match(/pin=([^; ]+)(?=;?)/)[1]}`,
            headers: {
                'Content-Type': 'application/json'
            },
            method: 'get'
        }
        const body = await api(options).json();
        console.log("wskey 转换结果：" + JSON.stringify(body));
        return {
            success: true,
            data: body.data
        };
    }
    var convertServiceUrl = "http://quantum.cqzhilai.com.cn:8015/api/open/ConvertWskey";
    if (process.env.WskeyConvertService) {
        var services = process.env.WskeyConvertService.split("&");
        if (services.length > 1) {
            console.log("似乎有多个WskeyConvertService，随机一个吧");
            convertServiceUrl = services[Math.floor(Math.random() * services.length)];
        }
        else {
            convertServiceUrl = process.env.WskeyConvertService;
        }
        console.log("使用自定义的Wskey转换服务！");
    }
    console.log("Wskey 转换服务地址：" + convertServiceUrl);
    try {
        const options = {
            url: convertServiceUrl,
            headers: {
                'Content-Type': 'application/json'
            },
            method: 'post',
            body: JSON.stringify([wskey])
        }
        const body = await api(options).json();
        console.log("wskey 转换结果：" + JSON.stringify(body));
        if (body.success) {
            return {
                success: true,
                data: body.data[0]
            }
        } else {
            console.log("wskey 转换失败，可能是转换服务IP黑了");
        }
    }
    catch (e) {
        console.log("wskey转换 app_open出现了异常！");
    }
    return {
        success: false
    };
}

/**
 * 获取京东账户信息信息
 * @param {any} jdCookie
 * @returns {
   "base": {
      "TipUrl": "http://wqs.jd.com/my/accountv2.shtml?sceneid=11110&state=0&rurl=https://wqs.jd.com/my/jingdou/my.shtml?sceneval=2",
      "accountType": 0,
      "curPin": "jd_4b992df9995e1", //pin
      "headImageUrl": "http://storage.360buyimg.com/i.imageUpload/6a645f3462393932646639393935653131343438353036383930323739_big.jpg",
      "isJTH": "1",
      "jdNum": 855, //京豆
      "jvalue": 9508, // 京享值
      "levelName": "锟斤拷石锟矫伙拷",
      "mobile": "156*****880", 
      "nickname": "Aurarol_99", //昵称
      "userLevel": 105 //用户等级
   },
   "definePin": 0,
   "isHitArea": 0,
   "isHomeWhite": 0,
   "isLongPwdActive": 1,
   "isPlusVip": true, //是否plus会员
   "isRealNameAuth": true, // 是否实名
   "isShortPwdActive": 1,
   "msg": "success.",
   "orderFlag": 1,
   "retcode": 0,
   "userFlag": 1
}
 */
module.exports.QueryJDUserInfo = async (jdCookie) => {
    var config = {
        method: 'get',
        url: 'https://wq.jd.com/user/info/QueryJDUserInfo?sceneval=2',
        headers: {
            'Accept': 'application/json,text/plain, */*',
            'Referer': 'https://wqs.jd.com/my/jingdou/my.shtml?sceneval=2',
            'User-Agent': 'jdapp;iPhone;9.4.4;14.3;network/4g;Mozilla/5.0 (iPhone; CPU iPhone OS 14_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148;supportJDSHWK/1',
            'Cookie': jdCookie,
            'Content-Type': 'application/x-www-form-urlencoded'
        }
    };
    const body = await api(config).json();
    return body;
}

/**
 * 获取账号基本信息
 * @param {any} jdCookie
 */
module.exports.GetJDUserInfoUnion = GetJDUserInfoUnion;

module.exports.checkAddJDCookie = checkAddCookie;

/**
 * 添加或者更新jdCookie pt_key 格式
 * 
 * 支持变量：JD_COOKIE_DEFAULT_STATUS， 京东Cookie默认状态，默认true
 * 
 * @param {any} jdCookie 京东ck
 * @param {any} user_id 用户id
 * @param {any} nickname 京东账号昵称
 * @param {bool} JD_COOKIE_DEFAULT_STATUS 指定环境变量状态
 */
module.exports.addOrUpdateJDCookie = addOrUpdateJDCookieEnv;

/**
 * 京东口令
 * @param {any} command
 */
module.exports.jCommand = async (command) => {
    var result = null;
    try {
        var options = {
            'method': 'POST',
            'url': process.env.jdCommandService || 'http://119.3.233.105:8080/JDSign/jCommand',
            'headers': {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                code: command
            })
        };
        var response = await api(options);
        console.log(`${command}
解析结果：${response.body}`);
        return JSON.parse(response.body);
    } catch (e) {
        console.log("转链失败：" + JSON.stringify(e));
    }
    return result;
}

/**
 * 添加或更新wskey 到自定义数据表中
 * @param {any} wskey key
 * @param {any} pin pin
 * @param {any} nickname 京东账号昵称
 */
module.exports.addOrUpdateWskey = async (wskey, pin, nickname, bbkJd) => {
    console.log("开始提交wskey到自定义数据中");
    var customDatas = await getCustomData(wskeyCustomDataType, null, null, { Data5: pin })
    var customData = {
        Type: wskeyCustomDataType,
        Data1: process.env.user_id,
        Data2: process.env.CommunicationUserName,
        Data3: process.env.CommunicationUserId,
        Data4: wskey,
        Data5: pin,
        Data6: nickname,
        Data7: await getJD_COOKIE_Pin_status(pin) ? "是" : "否",
        Data8: moment().format("YYYY-MM-DD HH:mm:ss"),
        Data9: bbkJd
    }
    if (customDatas && customDatas.length > 0) {
        console.log("更新wskey信息到自定义数据中");
        customData.Id = customDatas[0].Id;
        await updateCustomData(customData);
    }
    else {
        var result = await addCustomData([customData]);
        console.log("新增wskey信息到自定义数据中，提交结果" + JSON.stringify(result));
    }
}

module.exports.addWskeyCustomDataTitle = async () => {
    await addOrUpdateCustomDataTitle({
        Type: wskeyCustomDataType,
        TypeName: "京东wskey",
        Title1: "用户ID",
        Title2: "用户昵称",
        Title3: "QQ/WX",
        Title4: "wskey",
        Title5: "pin",
        Title6: "账号名称",
        Title7: "是否有效",
        Title8: "转换时间",
        Title9: "是否BBK京东wskey"
    })
}

/**
 * 自定义卡密天数
 * key 能不重复
 * name 为提示标题
 * value 为天数
 * */
module.exports.sntypes = [{
    "name": "1天",
    "value": 1,
    "key": "1"
}, {
    "name": "7天",
    "value": 7,
    "key": "2"
}, {
    "name": "1月",
    "value": 30,
    "key": "3"
}, {
    "name": "1年",
    "value": 365,
    "key": "4"
}, {
    "name": "永久",
    "value": 99 * 365,
    "key": "5"
}];

/**
 * 新版农场基本信息
 * @param {any} cookie
 * @returns
 */
async function farm_home(cookie) {
    var hdata = await universal("farm_home",
        {
            "version": 2
        }, {
        "appid": "signed_wh5",
        "appId": "c57f6",
        "version": "4.2"
    });
    let config = {
        method: 'post',
        url: 'https://h5.m.jd.com/client.action',
        headers: {
            'Host': 'api.m.jd.com',
            'User-Agent': hdata.ua,
            'accept': 'application/json, text/plain, */*',
            'Origin': 'https://h5.m.jd.com',
            'X-Requested-With': 'com.jingdong.app.mall',
            'Sec-Fetch-Site': 'same-site',
            'Sec-Fetch-Mode': 'cors',
            'Sec-Fetch-Dest': 'empty',
            'Referer': 'https://h5.m.jd.com/',
            'Cookie': cookie,
            'content-type': 'application/x-www-form-urlencoded'
        },
        body: hdata.data
    };
    let result = await api(config).json()
    return result;
    //return result.data.result.farmHomeShare.inviteCode;
}

/**
 * 将wskey 转换成 app_open
 * wskey 转换服务可以替换成其他的，兼容标准的服务
 * 或者根据自己服务调整此处代码即可
 * @param {any} wskey
 */
module.exports.ProWskey = async (wskey) => {
    var data = JSON.stringify({
        "botApitoken": process.env.Pro_BotApiToken,
        "wskey": wskey
    });
    try {
        const options = {
            url: process.env.Pro_URL + "/env/wskey",
            headers: {
                'Content-Type': 'application/json'
            },
            method: 'post',
            body: data
        }
        const body = await api(options).json();
        console.log("ProWskey 转换结果：" + JSON.stringify(body));
        if (body.success) {
            return {
                success: true,
                data: body.data.appck
            }
        } else {
            console.log("ProWskey 转换失败，可能是转换服务IP黑了");
        }
    }
    catch (e) {
        console.log("ProWskey 转换 app_open出现了异常！");
    }
    return {
        success: false
    };
}


/**
 * 添加或更新wskey 到自定义数据表中
 * @param {any} wskey key
 * @param {any} pin pin
 * @param {any} nickname 京东账号昵称
 */
module.exports.addOrUpdateProWskey = async (wskey, pin, nickname) => {
    console.log("开始提交ProWskey到自定义数据中");
    var customDatas = await getCustomData(ProCustomDataType, null, null, { Data5: pin })
    var customData = {
        Type: ProCustomDataType,
        Data1: process.env.user_id,
        Data2: process.env.CommunicationUserName,
        Data3: process.env.CommunicationUserId,
        Data4: wskey,
        Data5: pin,
        Data6: `wskey=${wskey};pin=${pin};`,
        Data7: nickname,
        Data8: await getJD_COOKIE_Pin_status(pin) ? "是" : "否",
        Data9: moment().format("YYYY-MM-DD HH:mm:ss"),
    }
    if (customDatas && customDatas.length > 0) {
        console.log("更新ProWskey信息到自定义数据中");
        customData.Id = customDatas[0].Id;
        await updateCustomData(customData);
    }
    else {
        var result = await addCustomData([customData]);
        console.log("新增ProWskey信息到自定义数据中，提交结果" + JSON.stringify(result));
    }
}

module.exports.addProWskeyCustomDataTitle = async () => {
    await addOrUpdateCustomDataTitle({
        Type: ProCustomDataType,
        TypeName: "Pro_wskey",
        Title1: "用户ID",
        Title2: "用户昵称",
        Title3: "QQ/WX",
        Title4: "wskey",
        Title5: "pin",
        Title6: "完整wskey",
        Title7: "账号名称",
        Title8: "是否有效",
        Title9: "转换时间"
    })
}

module.exports.wskeyCustomDataType = wskeyCustomDataType;
module.exports.ProCustomDataType = ProCustomDataType;

/**
 * 新版农场基本信息
 */
module.exports.farm_home = farm_home;