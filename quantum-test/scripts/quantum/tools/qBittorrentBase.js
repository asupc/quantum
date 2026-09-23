const got = require('got');
var FormData = require('form-data');

const api = got.extend({
    retry: { limit: 0 },
});

/**
 * 登录qb
 * @returns 
 */
module.exports.qblogin = async () => {
    let qBittorrentURL = process.env.qBittorrentURL
    if (!qBittorrentURL) {
        console.log("未配置环境变量：qBittorrentURL")
        return;
    }
    qBittorrentURL = qBittorrentURL.trimEnd("/");
    let data = new FormData();
    if (!process.env.qbusername || !process.env.qbpassword) {
        console.log("未配置qb 账号密码，跳过登录，可能出现请求拒绝访问（403）")
        return;
    }
    data.append('username', process.env.qbusername);
    data.append('password', process.env.qbpassword);
    let config = {
        method: 'post',
        url: qBittorrentURL + '/api/v2/auth/login',
        body: data
    };
    let cookie = "";
    await api(config).then(async response => {
        cookie = response.headers["set-cookie"][0];
        let SID = cookie.match(/SID=([^; ]+)(?=;?)/)[1]
        cookie = `SID=${SID}`;
        console.log("认证授权Cookie：" + cookie)
    }).catch(async function (error) {
        console.log("qb登录认证出现异常")
        console.log(error)
    });
    return cookie;
}

/**
 * 添加磁力
 * @param {*} cookie 授权token
 * @param {*} urls 磁力信息多个换行
 * @param {*} savepath 下载子目录
 * @returns 
 */
module.exports.addTorrents = async (cookie, urls,savepath) => {
    let qBittorrentURL = process.env.qBittorrentURL
    if (!qBittorrentURL) {
        console.log("未配置环境变量：qBittorrentURL")
        return;
    }
    qBittorrentURL = qBittorrentURL.trimEnd("/");
    let data = new FormData();
    data.append('urls', urls);
    data.append('autoTMM', 'false');
    if(savepath){
        data.append('savepath', '/downloads/'+savepath);
    }
    data.append('paused', 'false');
    data.append('contentLayout', 'Original');
    console.log("磁力信息：" + urls);
    let config = {
        method: 'post',
        url: qBittorrentURL + '/api/v2/torrents/add',
        headers: {
            Cookie: cookie
        },
        body: data
    };

    let msg = "";

    await api(config).then(async response => {
        console.log(response.body);
        msg="磁力任务添加成功！"
    }).catch(async function (error) {
        await sendNotify(`磁力任务添加失败：
${error.name}
${error.code}`)
        console.log(error)
    });

    return msg;
}