/**
 * 本脚本支持环境变量 及说明
 * ADD_COOKIE_USE_SCORE  添加CK需要多少积分。（设置为0 或者 不设置时则表示不需要积分。）
 * 
 * 因 GetJDUserInfoUnion 接口限流，调整方案，先查询有效性，只要有效均保存环境变量。
 * 
 **/

let ADD_COOKIE = process.env.ADD_COOKIE || "pt_key=AAJmGTyZADDThzz5aoEz1wlfQHIAvPRcyboyLVvizY9yOK_syYXKLtOc63FFrSRBst6_meXG2II; pt_pin=jd_gnjxWdxXYJyI;";


if (process.env.JD_COOKIE) {
    jdCookies = process.env.JD_COOKIE.split("&");
}

let cookies = [];
// const { sendNotify, getUserInfo, uuid, deductionIntegral
// } = require('./quantum');

const { checkAddJDCookie } = require('./jd_base');
const { sleep } = require('./quantum');

!(async () => {
    cookies = ADD_COOKIE.split("&");
    for (let i = 0; i < cookies.length; i++) {
        var cookie = cookies[i];
        if (cookie) {
            await checkAddJDCookie(cookie);
            await sleep(3000)
        }
    }
})().catch((e) => {
    console.log("addCookie.js 出现异常：" + e);
});