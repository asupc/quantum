/**
 * 
* 京东账号密码登录
* 服务地址：script_jd_autologin_service 例如 http://192.168.2.123:1234
* 
**/
const {
    sendNotify,
    addOrUpdateCustomDataTitle,
    stepCommandTaskAddEnv,
    redoStepCommandTask
} = require("./quantum");

const { doCheck, login, sms, customDataType } = require('./jdAccountLogin_base');


// 定义环境变量获取京东账号和密码
let jdAccount = process.env.script_jd_account; // 京东账号
let jdPassword = process.env.script_jd_password; // 京东密码
let jdVerificationCode = process.env.script_jd_verification_code; // 京东密码


!(async () => {
    /**
     * 下面这行代码，执行过一次脚本之后就可以注释了
     */
    await initCustomTitle();

    if (!process.env.script_jd_autologin_service) {
        console.log("未指定京东账号密码登陆服务地址。变量名：script_jd_autologin_service");
        return;
    }
    if (process.env.script_jd_autologin_service && process.env.script_jd_autologin_service.endsWith("/")) {
        process.env.script_jd_autologin_service = process.env.script_jd_autologin_service.substring(0, process.env.script_jd_autologin_service.length - 1);
    }
    if (!jdAccount) {
        await sendNotify("请回复京东账号（手机号）：");
        return;
    }
    if (!jdPassword) {
        await sendNotify("请回复京东密码：");
        return;
    }
    if (jdVerificationCode) {
        await sendNotify("正在验证短信验证码，请稍后")
        const smsResult = await sms(process.env.jd_auto_login_uid_temp, jdVerificationCode);
        if (smsResult.status == "wrongSMS") {
            await sendNotify("验证码错误，请重新回复！如长时间未收到短信验证码请回复Q结束，再重新开始。");
            await redoStepCommandTask();
            return;
        }
        await doCheck(process.env.jd_auto_login_uid_temp)
        return;
    }
    if (process.env.jd_auto_login_uid_temp) {
        await doCheck(process.env.jd_auto_login_uid_temp);
        return;
    }
    await sendNotify(`收到账号和密码请稍等...`);
    const loginResult = await login(jdAccount, jdPassword, false)
    if (loginResult.uid) {
        await stepCommandTaskAddEnv("jd_auto_login_uid_temp", loginResult.uid);
        await doCheck(loginResult.uid);
    }


})().catch(e => {
    console.error("提交京东账号密码脚本执行异常：", e);
});

async function initCustomTitle() {
    // 创建或更新数据表单
    await addOrUpdateCustomDataTitle({
        Type: customDataType,
        TypeName: "JD账号密码库",
        // Title1: "用户 ID",
        Title2: "用户昵称",
        Title3: "QQ/WX",
        Title4: "账号",
        Title5: "密码",
        Title6: "pin",
        Title7: "是否有效"
    }).then(() => {
        console.log("数据表单创建或更新成功");
    }).catch(async (error) => {
        console.error("创建或更新数据表单时出错:", error);
        await sendNotify("创建或更新数据表单时出错: " + error.message);
    });
}