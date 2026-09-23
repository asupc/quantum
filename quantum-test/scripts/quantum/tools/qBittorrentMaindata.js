/**
 * 
 * qBittorrent 下载任务详情
 * 
 * 未实现通过账号密码登录，请关闭身份认证或添加白名单。
 * 
 * 请通过设置>Web UI>勾选     对本地主机上的客户端跳过身份验证   和   对 IP 子网白名单中的客户端跳过身份验证 ， 填入量子的IP 地址。
 * 
 * 如果不这样，你需要添加qb账号密码环境变量，以便机器人自动登录授权。
 * 
 * qbusername (账号)
 * qbpassword (密码)
 * 
 * 
 * 必要环境变量 qBittorrentURL  如 http://192.168.10.91:8080
 * 
 * */

const got = require('got');
var moment = require('moment');
const {
    sendNotify
} = require('./quantum');
const {
    qblogin
} = require('./qBittorrentBase');

const api = got.extend({
    retry: { limit: 0 },
});

!(async () => {
    var qBittorrentURL = process.env.qBittorrentURL;
    if (!qBittorrentURL) {
        await sendNotify("未设置qBittorrent服务地址，请添加量子变量 ：qBittorrentURL。")
        return;
    }
    if (!qBittorrentURL.startsWith("http")) {
        qBittorrentURL = "http://" + qBittorrentURL;
    }
    qBittorrentURL = qBittorrentURL.trimEnd("/");

    var cookie = await qblogin(qBittorrentURL);
    if (cookie) {
        console.log("认证返回cookie：" + cookie)
    }
    console.log("开始获取 maindata")
    var config = {
        method: 'get',
        url: qBittorrentURL + '/api/v2/sync/maindata',
        headers: {
            Cookie: cookie
        }
    };

    api(config)
        .then(async function (response) {
            var data = JSON.parse(response.body);
            var msg = `可用磁盘空间：${(data.server_state.free_space_on_disk/1024/1024/1024).toFixed(2)}G
获取下载任务数量：`;
            var taskCount = 0;
            var details= "";
            for (let key in data.torrents) {
                var torrent = data.torrents[key]
                taskCount++;
                details+=`
任务序号：${taskCount}
任务名：${torrent.name}
添加时间：${moment(torrent.added_on*1000).format("YYYY-MM-DD HH:mm")}
任务状态：${getStateText(torrent.state)}
文件大小：${(torrent.total_size/1024/1024).toFixed(2)}MB
比率：${(torrent.ratio).toFixed(2)}
下载进度：${(torrent.progress*100).toFixed(1)}%`

            }
            msg = msg + taskCount+details;
            console.log(msg);
            await sendNotify(msg);
        })

        .catch(function (error) {
            console.log("请求数据异常：")
            console.log(error);
        });

})().catch((e) => {
    console.log("脚本执行异常")
    console.log(e)
});

function getStateText(state){
    switch (state) {
        case "downloading":
            status = "下载";
            break;
        case "stalledDL":
            status = "等待";
            break;
        case "metaDL":
            status = "下载元数据";
            break;
        case "forcedMetaDL":
            status = "[F] 下载元数据";
            break;
        case "forcedDL":
            status = "[F] 下载";
            break;
        case "uploading":
        case "stalledUP":
            status = "做种";
            break;
        case "forcedUP":
            status = "[F] 做种";
            break;
        case "queuedDL":
        case "queuedUP":
            status = "排队";
            break;
        case "checkingDL":
        case "checkingUP":
            status = "校验";
            break;
        case "queuedForChecking":
            status = "排队等待校验";
            break;
        case "checkingResumeData":
            status = "校验恢复数据";
            break;
        case "pausedDL":
            status = "暂停";
            break;
        case "pausedUP":
            status = "完成";
            break;
        case "moving":
            status = "移动中";
            break;
        case "missingFiles":
            status = "丢失文件";
            break;
        case "error":
            status = "错误";
            break;
        default:
            status = "未知";
    }
    return status;
}