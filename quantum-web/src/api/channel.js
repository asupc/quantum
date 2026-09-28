import axios from '@/libs/api.request'

const post = (url, data) => axios.request({ url, method: 'post', data })

export const getChannelStatus = () => axios.request({ url: '/api/Channel/status', method: 'get' })
export const getChannelDelivery = (platform) => axios.request({ url: `/api/Channel/${platform}/delivery`, method: 'get' })
export const configureQq = (data) => post('/api/Channel/qq/config', data)
export const startWeixinQr = (data) => post('/api/Channel/weixin/qr/start', data)
export const pollWeixinQr = (sessionId, verifyCode) => post('/api/Channel/weixin/qr/status', {
    SessionId: sessionId, VerifyCode: verifyCode || null
})
export const configureFeishu = (data) => post('/api/Channel/feishu/config', data)
// 飞书扫码绑定：设备码授权注册，扫码成功后凭据由服务端加密入库并登记候选
export const startFeishuQr = (confirmRebind) => post('/api/Channel/feishu/qr/start', { ConfirmRebind: confirmRebind })
export const pollFeishuQr = (sessionId) => post('/api/Channel/feishu/qr/status', { SessionId: sessionId })
export const unbindChannel = (platform) => post(`/api/Channel/${platform}/unbind`, { Confirm: true })
// 手动自检：目标由服务端取该平台最近一条有效原路回复路由，前端不传收件人
export const sendChannelTest = (platform) => post(`/api/Channel/${platform}/test-send`, {})

export const retryChannelDelivery = (platform, id) => post(
    `/api/Channel/${platform}/delivery/${encodeURIComponent(id)}/retry`, { Confirm: true }
)
