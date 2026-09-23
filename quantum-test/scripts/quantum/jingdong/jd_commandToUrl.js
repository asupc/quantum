/**
 * 
 * 口令转换链接
 * 使用第三方接口完成
 * 
 **/
const {
    sendNotify
} = require('./quantum');
const {
    jCommand
} = require('./jd_base');
!(async () => {
    var result = await jCommand(process.env.jd_command);
    if (!result) {
        await sendNotify("解析异常了！");
    } else {
        console.log(result)
        await sendNotify([{
            MessageType: 2,
            msg: result.data.img
        }, {
            MessageType: 1,
            msg: `标题：${result.data.title}
用户：${result.data.userName}
地址：${result.data.jumpUrl}`
        }]);
    }
})().catch((e) => {
    console.log("脚本异常：" + e);
});

