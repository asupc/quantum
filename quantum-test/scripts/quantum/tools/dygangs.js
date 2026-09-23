/**
 * 电影港续集监听
 * 
 * 手动执行一次脚本 自动生成自定义数据  影视监听配置 ， 影视监听记录
 * 
 * 手动添加 影视监听配置 ，配置需要监听的剧集的详情页 比如：https://www.dygangs.net/dmq/20211031/48089.htm，自动下载配置”是/否“，自动下载需要配置QB相关信息，是否启用”是否“
 * 
 */

const cheerio = require('cheerio');
const { api, sendNotify, addOrUpdateCustomDataTitle, getCustomData, addCustomData } = require('../quantum');

const { qblogin, addTorrents } = require("./qBittorrentBase")

const iconv = require('iconv-lite');



const customDataType = "video_update_monitoring_history"
const urlConfigDataType = "video_update_monitoring_item"

!(async () => {

    await init();

    const dygangsUrls = await getCustomData(urlConfigDataType, null, null, {
        Data4: "是"
    });

    for (let p = 0; p < dygangsUrls.length; p++) {
        const dygangsUrl = dygangsUrls[p];
        let videoName = dygangsUrl.Data1;
        let url = dygangsUrl.Data2;
        const response = await api(url, { responseType: 'buffer' });
        // const html = response.body; // 或根据需要的编码更改
        const html = iconv.decode(response.body, 'gb2312'); // 或根据需要的编码更改
        const $ = cheerio.load(html);
        let newDatas = []
        const datas = await getCustomData(customDataType, null, null, {
            Data15: dygangsUrl.Id
        })
        let count2 = 0;
        $('a[href^="magnet:?xt=urn:btih:"]').each((index, element) => {
            const link = element.attribs.href;
            const name = element.children[0].data.toUpperCase();
            if (datas.filter(n => n.Data4 == link).length > 0) {
                count2++;
            } else {
                newDatas.push({
                    Type: customDataType,
                    Data1: videoName,
                    Data2: url,
                    Data3: name,
                    Data4: link,
                    Data15: dygangsUrl.Id
                })
                console.log(`${videoName}采集到新剧集：${name},${link}`)
            }
        });
        let downloadList = []
        if (newDatas.length > 0) {
            await addCustomData(newDatas)
            console.log(`【${videoName}】新采集数量：【${newDatas.length}】`)
            let msg = `【${videoName}】采集到【${newDatas.length}】条更新剧集...`;
            if (dygangsUrl.Data3 == "是") {
                const qBittorrentURL = process.env.qBittorrentURL
                if (!qBittorrentURL) {
                    console.log(`
未设置qBittorrent服务地址，请添加量子变量 ：
qBittorrentURL qb链接地址 如：http://192.168.98.8:9091
qbusername qb登录账号
qbpassword  qb登录密码`)
                } else {
                    const cookie = await qblogin();
                    for (let x = 0; x < newDatas.length; x++) {
                        const data = newDatas[x];
                        if (!data.Data3.includes('2160P') && !data.Data3.includes('4K')) {
                            console.log(`${videoName} 跳过下载不包含指定分辨率的项目：${data.Data3}`);
                            continue; // 跳过不包含指定分辨率的项目
                        }
                        downloadList.push(data.Data4)
                        console.log("磁力下载提交结果----" + await addTorrents(cookie, data.Data4, videoName));
                    }
                    if (downloadList.length > 0) {
                        msg += `
已自动提交下载。`
                    }
                }
            }
            if (dygangsUrl.Data5 == "是") {
                for (let x = 0; x < newDatas.length; x++) {
                    const data = newDatas[x];
                    await sendNotify(`${videoName}
${data.Data3}
${data.Data4}`, true)
                }
            }
        }
        if (count2 > 0) {
            console.log(`【${videoName}】过往已采集数量：【${count2}】`)
        }
    }
})().catch((e) => {
    console.log("脚本异常：" + e);
});

async function init() {
    await addOrUpdateCustomDataTitle({
        Type: urlConfigDataType,
        TypeName: "影视监听配置",
        Title1: "影片名",
        Title2: "来源",
        Title3: "自动下载",
        Title4: "是否启用",
        Title5: "消息推送"
    });
    await addOrUpdateCustomDataTitle({
        Type: customDataType,
        TypeName: "影视监听记录",
        Title1: "影片名",
        Title2: "来源",
        Title3: "剧集标题",
        Title4: "磁力",
        // Title15: "影视监听配置Id" 不显示
    });
}