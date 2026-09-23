/**
 * 
 * 京东一键价保
 * 
 */

const { sendNotify, getEnvs, api, sleep } = require('./quantum');

const { universal } = require('./h5st')

!(async () => {

    let isQuantum = process.env.QuantumAssistantTemporaryToken && process.env.QuantumAssistantTemporaryToken.length > 0;
    let cookiesArr = [];
    if (isQuantum) {
        cookiesArr = await getEnvs("JD_COOKIE", null, 2, process.env.user_id)
        if(process.env.user_id){
            await sendNotify(`开始为您[${cookiesArr.length}]个账号申请价保...
如申请成功则通知，否则不另行通知。`)
        }
    } else {
        cookiesArr = [{
            Enable: true
        }]
    }
    process.env.CommunicationType = "";
    for (let i = 0; i < cookiesArr.length; i++) {
        if (cookiesArr[i].Value && cookiesArr[i].Enable) {
            let cookie = cookiesArr[i].Value;
            let pt_pin = cookie.match(/pt_pin=([^; ]+)(?=;?)/)[1]
            let UserName = (cookie.match(/pt_pin=([^; ]+)(?=;?)/) && pt_pin)
            let UserName2 = decodeURI(UserName);
            console.log(`----------开始执行账号：[${cookiesArr[i].UserRemark}]价保任务----------`)

            let h5st = await universal("siteppM_skuOnceApply",
                {
                    sid: "",
                    type: "25",
                    forcebot: ""
                }, {
                "appid": "siteppM",
                "appId": "d2f64",
                "version": "skuOnceApply",
                "pin": UserName2
            });
            let config = {
                method: 'post',
                url: `https://api.m.jd.com/api`,
                headers: {
                    "accept": "application/json, text/plain, */*",
                    "content-type": "application/x-www-form-urlencoded",
                    Origin: "https://msitepp-fm.jd.com",
                    "X-Requested-With": "com.jingdong.app.mall",
                    Referer: "https://msitepp-fm.jd.com/",
                    'User-Agent': h5st.ua,
                    'Cookie': cookie,
                },
                body: h5st.data,
            };
            let data = await api(config).json();
            if (data.flag) {
                if (data.succAmount && data.succAmount != 0) {
                    let msg = `京东账号：【${cookiesArr[i].UserRemark}】
您的帐号通过机器人保价成功
本次保价回血[${data.succAmount}]元🤑
查看记录可前往:京东APP-我的一客户服务一价格保护一申请记录`
                    console.log(msg)
                    await sendNotify(msg, false, cookiesArr[i].UserId);
                }
                else {
                    console.log("商品没有降价~想屁吃~")
                }
            }
            console.log(`[${cookiesArr[i].UserRemark}]价保申请响应：【${data.responseMessage}】`)
            await sleep(5000);
        }
    }
})()
    .catch((e) => {
        console.log("脚本执行异常：" + e)
    });