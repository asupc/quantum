/*
 * 删除过期账号提醒
 * 可配置环境变量名称：OVERDUE_DEL_NOTIFY_MSG  (删除过期提醒文字)
 */
const {
    sendNotify, allEnvs, syncEnv, deleteEnvByIds
} = require('./quantum');

var message = process.env.OVERDUE_DEL_NOTIFY_MSG || "您的以下京东账号已经过期，管理员已删除：";

!(async () => {
    var envs = await allEnvs("JD_COOKIE", 2, false, "");
    console.log("获取过期环境变量" + envs.length + "个");
    var ts = [];
    var ids = [];
    for (var i = 0; i < envs.length; i++) {
        ids.push(envs[i].Id);
        if (envs[i].UserId) {
            if (ts.length > 0 && ts.filter((t) => t.UserId === envs[i].UserId).length > 0) {
                ts.filter((t) => t.UserId === envs[i].UserId)[0].List.push(envs[i].UserRemark)
            } else {
                ts.push({
                    UserId: envs[i].UserId,
                    List: [envs[i].UserRemark]
                });
            }
        }
    }
    if (ids.length > 0) {
        var body1 = await deleteEnvByIds(ids);
        console.log("删除过期CK结果：" + JSON.stringify(body1));
        var body2 = await syncEnv();
        console.log("单项CK同步结果：" + JSON.stringify(body2));
        sendNotify(`删除过期CK${envs.length}个。`, true);
        console.log("开始给韭菜发通知了。");
        if (ts.length > 0) {
            process.env.CommunicationType = "";
            for (var i = 0; i < ts.length; i++) {
                await sendNotify(message + "\n" + ts[i].List.join(","), false, ts[i].UserId);
            }
        }
    } else {
        await sendNotify("没有过期的账号。", true);
    }
})().catch((e) => {console.log("脚本异常：" + e);});