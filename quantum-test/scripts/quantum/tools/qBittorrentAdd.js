/**
 * 
 * qBittorrent 添加 磁力任务脚本
 * 
 * 未实现通过账号密码登录，请关闭身份认证或添加白名单。
 * 
 * 请通过设置>Web UI>勾选     对本地主机上的客户端跳过身份验证   和   对 IP 子网白名单中的客户端跳过身份验证 ， 填入量子的IP 地址。
 * 如果不这样，你需要添加qb账号密码环境变量，以便机器人自动登录授权。
 * 
 * qbusername (账号)
 * qbpassword (密码)
 * 
 * 
 * 必要环境变量 qBittorrentURL  如 http://192.168.10.91:8080
 * 
 * 
 * 
 * */

const {
    sendNotify
} = require('./quantum');

const {
    qblogin, addTorrents
} = require('./qBittorrentBase');

!(async () => {
    const qBittorrentURL = process.env.qBittorrentURL
    if (!qBittorrentURL) {
        await sendNotify("未设置qBittorrent服务地址，请添加量子变量 ：qBittorrentURL。")
        return;
    }
    const cookie = await qblogin();
    await sendNotify(await addTorrents(cookie, process.env.command));
})().catch((e) => {
    console.log("脚本异常：" + e);
});