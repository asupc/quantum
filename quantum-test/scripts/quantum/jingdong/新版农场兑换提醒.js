/**
 * 旧版东东农场自动兑换，种植
 */

const {
    api, getEnvs, sleep, sendNotify
} = require('./quantum');

!(async () => {
    let isQuantum = process.env.QuantumAssistantTemporaryToken && process.env.QuantumAssistantTemporaryToken.length > 0;
    let cookiesArr = [];
    if (isQuantum) {
        cookiesArr = await getEnvs("JD_COOKIE", null, 2);
    } else {
        cookiesArr = []
    }
    for (let i = 0; i < cookiesArr.length; i++) {
        let cookie = cookiesArr[i]
        if (cookie.Value && cookie.Enable) {
            let pin = cookie.Value.match(/pt_pin=([^; ]+)(?=;?)/)[1]
            console.log(`--------------开始处理第：【${(i + 1)}】个账号：[${pin}]--------------`)

            let result = await farm_award_detail(cookie.Value);
            if (result && result.data && result.data.result && result.data.result.plantAwards && result.data.result.plantAwards.length > 0) {
                let pa = result.data.result.plantAwards.filter(n => n.awardStatus == 1);
                if (pa && pa.length > 0) {
                    console.log(pa.length + "个奖品未兑换。")
                    let pa1 = pa[0]
                    await sendNotify(`新版农场种植完成兑换提醒：
账号名称：【${cookie.UserRemark || pin}】
${pa1.plantCompleteTip}
${pa1.exchangeRemind}
请通过东东农场>左上角【记录】兑换`, false, cookie.UserId)
                } else {
                    console.log(result.data.result.plantAwards.length + "个奖品兑换完成")
                }
            } else {
                console.log("未完成种植")
            }

            await sleep(1000 * 5);
        }
    }
})().catch((e) => {
    console.log("脚本异常：" + e);
});

/**
 * 新版农场奖品详情
 * @param {any} cookie
 * @returns
 */
async function farm_award_detail(cookie) {
    var data = 'appid=signed_wh5&client=android&clientVersion=12.6.6&screen=412*0&wqDefault=false&build=99162&osVersion=14&networkType=wifi&d_brand=vivo&d_model=V2307A&partner=vivo&uuid=4303564663669313-5343261693563333&t=1714023334489&body={"version":3,"type":1}&functionId=farm_award_detail&x-api-eid-token=jdd03LEB5QB7RYGBJ2UES3GRITGBDOVZB3VTFETWK2JFAH5HNA652B4X4APWPLGE4YUMTHANBTMOICPWGVXZETBSUR3N4JMAAAAMPCO7634QAAAAAD3SHDBRWOVPKC4X';
    var config = {
        method: 'post',
        url: 'https://h5.m.jd.com/client.action',
        headers: {
            'Host': ' api.m.jd.com',
            'x-referer-page': 'https://h5.m.jd.com/pb/015686010/Bc9WX7MpCW7nW9QjZ5N3fFeJXMH/index.html',
            'User-Agent': 'jdapp;android;12.6.6;;;M/5.0;appBuild/99162;ef/1;ep/%7B%22hdid%22%3A%22JM9F1ywUPwflvMIpYPok0tt5k9kW4ArJEU3lfLhxBqw%3D%22%2C%22ts%22%3A1714023294857%2C%22ridx%22%3A-1%2C%22cipher%22%3A%7B%22sv%22%3A%22CJG%3D%22%2C%22ad%22%3A%22DNLvZNZwEJO1DQTrEWUzCm%3D%3D%22%2C%22od%22%3A%22EWPvDWYnZWVrCQTvZJU0YJGzEJc3EJU1D2O5DzC0DWPvDwUnC2GzDwSyCtY3DtPuZJc0CQU2DwHtCtU1EWTwDm%3D%3D%22%2C%22ov%22%3A%22CzG%3D%22%2C%22ud%22%3A%22DNLvZNZwEJO1DQTrEWUzCm%3D%3D%22%7D%2C%22ciphertype%22%3A5%2C%22version%22%3A%221.2.0%22%2C%22appname%22%3A%22com.jingdong.app.mall%22%7D;jdSupportDarkMode/1;Mozilla/5.0 (Linux; Android 14; V2307A Build/UP1A.231005.007; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/123.0.6312.40 Mobile Safari/537.36',
            'accept': ' application/json, text/plain, */*',
            'Origin': ' https://h5.m.jd.com',
            'X-Requested-With': ' com.jingdong.app.mall',
            'Referer': ' https://h5.m.jd.com/',
            'Cookie': cookie,
            'content-type': ' application/x-www-form-urlencoded'
        },
        body: data
    };
    return await api(config).json();
}

