/**
 * 脚本依赖 cheerio
 * 需要在/app/quantum/scripts
 */

const cheerio = require('cheerio');
const { api, sendNotify, stepCommandTaskAddEnv, finshStepCommandTask } = require('./quantum');
const { qblogin, addTorrents } = require('./qBittorrentBase');
const envNameKey = "qbSearch-Result-"
!(async () => {
    if (process.env.scripts_qbsearch_downloads) {
        const qbCookie = await qblogin();
        for (let y = 0; y < process.env.scripts_qbsearch_downloads.split(" ").length; y++) {
            const element = process.env.scripts_qbsearch_downloads.split(" ")[y];
            await addTorrents(qbCookie, process.env[envNameKey + element])
        }
        return;
    }
    process.env.command = process.env.command || "西游记"
    console.log(`执行任务：【${process.env.command}】`)
    const key = process.env.command.replace("搜磁力", "")
    const url = `https://btsow.motorcycles/search/${key}`;
    const result = await api({
        method: "get",
        url: url
    })
    const $ = cheerio.load(result.body);
    const containerDiv = $('.container');
    const dataListDiv = containerDiv.find('.data-list');
    let msg = `未检索到[${key}]相关信息`
    if (!dataListDiv) {
        await sendNotify(msg)
        return;
    }
    let rows = dataListDiv.find('.row');
    if (!rows || rows.length < 1) {
        await sendNotify(msg)
        await finshStepCommandTask()
        return;
    }
    for (let index = 1; index < rows.length && index < 6; index++) {
        const element = rows[index]
        const aTag = $(element).find('a');
        const href = aTag.attr('href');
        const title = aTag.attr('title');
        const size = $(element).find('.size').text().trim();
        const date = $(element).find('.date').text().trim();
        let magnetLink = `magnet:?xt=urn:btih:${href.split("/")[href.split("/").length - 1]}`
        const msg = `磁力：${magnetLink}
标题：【${title}】
大小：【${size}】
日期：【${date}】`
        console.log(msg)
        await stepCommandTaskAddEnv(envNameKey + index, magnetLink)
        await sendNotify(msg)
    }
})().catch((e) => {
    console.log("脚本异常：" + e);
});

async function getMagnetLink(hash) {
    const url = `https://btsow.motorcycles/magnet/detail/hash/${hash}`
    console.log(`开始检索磁力链接：【${url}】`)
    var html = await api({
        method: "get",
        url: url
    })
    const $ = cheerio.load(html.body);
    const magnetLink = $('#magnetLink').val();
    return magnetLink;
}