/**
 * 旧版东东农场自动兑换，种植
 */

const {
    api, getEnvs, sleep, sendNotify
} = require('./quantum');

const { universal } = require('./h5st')

let retryCount = 0;

let functionAppId = {
    "totalWaterTaskForFarm": "102f5",
    "gotThreeMealForFarm": "57b30",
    "browseAdTaskForFarm": "53f09",
    "clockInFollowForFarm": "4a0b4",
    "waterFriendForFarm": "673a0",
    "awardFirstFriendForFarm": "9b655",
    "limitWaterInitForFarm": "6bdc2",
    "ddnc_surpriseModal": "e81c1",
    "friendInitForFarm": "a5a9c",
    "waterGoodForFarm": "0c010",
    "firstWaterTaskForFarm": "0cf1e",
    "waterFriendGotAwardForFarm": "d08ff",
    "ddnc_getTreasureBoxAward": "67dfc",
    "orderTaskGotWaterForFarm": "eed5c",
    "clockInForFarm": "32b94",
    "awardInviteFriendForFarm": "2b5ca",
    "awardCallOrInviteFriendForFarm": "b0b03",
    "getFullCollectionReward": "5c767",
    "getOrderPayLotteryWater": "ef089",
    "receiveStageEnergy": "15507",
    "exchangeGood": "52963",
    "initForFarm": "8a2af",
    "taskInitForFarm": "fcb5a",
    "userMyCardForFarm": "86ba5",
    "getCallUserCardForFarm": "2ca57",
    "deleteFriendForFarm": "eaf91",
    "gotLowFreqWaterForFarm": "8172b",
    "choiceGoodsForFarm": "5f4ca",
    "gotCouponForFarm": "b1515",
    "gotStageAwardForFarm": "81591",
    "followVenderForBrand": "71547",
    "clockInInitForFarm": "08dc3",
    "guideTaskAward": "59bc4",
    "farmAssistInit": "92354",
    "myCardInfoForFarm": "157b6",
    "gotPopFirstPurchaseTaskForFarm": "d432f",
    "gotWaterGoalTaskForFarm": "c901b",
    "gotNewUserTaskForFarm": "de8f8"
};

!(async () => {
    let isQuantum = process.env.QuantumAssistantTemporaryToken && process.env.QuantumAssistantTemporaryToken.length > 0;
    let cookiesArr = [];
    if (isQuantum) {
        cookiesArr = await getEnvs("JD_COOKIE", null, 2);
    } else {
        cookiesArr = [{
            Value: "pt_key=1111-111-TE;pt_pin=1111;",
            Enable: true
        }]
    }
    for (let i = 0; i < cookiesArr.length; i++) {
        let cookie = cookiesArr[i]
        if (cookie.Value && cookie.Enable) {

            let pin = cookie.Value.match(/pt_pin=([^; ]+)(?=;?)/)[1]
            console.log(`--------------开始处理第：【${(i + 1)}】个账号：[${pin}]--------------`)
            // if (blackPins.indexOf(pin) >= 0) {
            //     console.log("农场黑名单，跳过。")
            //     continue;
            // }
            try {
                retryCount = 0;
                let fruit = await request("initForFarm",
                    {
                        "babelChannel": "121",
                        "sid": "c58c8776f16696f164d7164a35306ebw",
                        "version": 19,
                        "channel": 1
                    }, cookie.Value);

                if (fruit.code != '0') {
                    console.log(fruit.message);
                    continue;
                }
                let dfdmsg = `【东东农场】${fruit.farmUserPro.name}(${((fruit.farmUserPro.treeEnergy / fruit.farmUserPro.treeTotalEnergy) * 100).toFixed(0)}%)
共种值${fruit.farmUserPro.winTimes}次
已浇水${fruit.farmUserPro.treeEnergy / 10}次
还需${(fruit.farmUserPro.treeTotalEnergy - fruit.farmUserPro.treeEnergy) / 10}次`


                if (fruit.farmUserPro.treeState == 1) {
                    let code = fruit.farmUserPro.shareCode;
                    dfdmsg += `
助力码：`+ code;
                }
                console.log(dfdmsg)

                if (fruit.farmUserPro.treeState > 1) {
                    console.log("已成熟。。。去兑换红包。。。。。")
                    let gotCouponForFarmResult = await request("gotCouponForFarm",
                        {
                            "version": 24,
                            "channel": 3,
                            "babelChannel": 0
                        }, cookie.Value);
                    // console.log("gotCouponForFarmResult", gotCouponForFarmResult)
                    await sleep(5000);
                    fruit = await request("initForFarm",
                        {
                            "babelChannel": "121",
                            "sid": "c58c8776f16696f164d7164a35306ebw",
                            "version": 19,
                            "channel": 1
                        }, cookie.Value);

                    if (fruit.farmUserPro.treeState === 3) {
                        let hongBao = fruit.myHongBaoInfo.hongBao;
                        let msg = `京东账号：【${cookie.UserRemark}】
旧版农场已兑换[${hongBao.discount}]元红包
红包有效期7天，请及时使用。`
                        await sendNotify(msg, false, cookie.UserId)
                    }
                    let goodsType = fruit.farmLevelWinGoods ? fruit.farmLevelWinGoods[2][0] : 0;
                    await sleep(5000);
                    if (goodsType) {
                        let choiceGoodsForFarmResult = await request("choiceGoodsForFarm", {
                            "imageUrl": "",
                            "nickName": "",
                            "shareCode": "",
                            "goodsType": goodsType.type,
                            "type": "0",
                            "version": 24,
                            "channel": 3,
                            "babelChannel": 0
                        }, cookie.Value);

                        if (choiceGoodsForFarmResult.code * 1 === 0) {
                            console.log(`再次种植：【${choiceGoodsForFarmResult.farmUserPro.name}】成功`)
                            let msg = `京东账号：【${cookie.UserRemark}】
旧版农场自动选择种植：【${choiceGoodsForFarmResult.farmUserPro.name}】成功`
                            await sendNotify(msg, false, cookie.UserId)
                        }
                    } else {
                        console.log("获取种子信息失败，请重试，如果多次失败，手动进APP查看")
                    }
                } else {

                }
            } catch (e) {
                console.log(`信息处理异常`, e)
            }
            await sleep(60 * 1000 * 2);
        }
    }
})().catch((e) => {
    console.log("脚本异常：" + e);
});

async function request(functionId, body, cookie) {
    var hdata = await universal(functionId, body, {
        "appid": "signed_wh5",
        "appId": functionAppId[functionId],
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

    // if (userProxy && (!proxy || moment(proxy.expire) < moment())) {
    //     proxy = await getXKProxy();
    // }
    // if (userProxy && proxy && moment(proxy.expire) > moment()) {
    //     var agent = new HttpsProxyAgent(`http://${proxy.ip}:${proxy.port}`);
    //     config.agent = {
    //         https: agent,
    //         http: agent,
    //     }
    // }
    let result = { code: "1", message: "请求异常" };
    try {
        result = await api(config).json()
    }
    catch {
    }
    if (functionId === "initForFarm" && result.code != '0') {
        userProxy = true;
        console.log(result.message);
        proxy = null;
        if (retryCount < 3) {
            retryCount++;
            await sleep(1000 * 60 * 1)
            return request(functionId, body, cookie);
        }
        return result;
    }
    return result;
}