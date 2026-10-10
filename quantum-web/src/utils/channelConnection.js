// 临时断线由后端复用保存的凭据重试；只有明确登录失效才引导重新扫码。
export function channelConnectionState(row = {}) {
  const code = row.LastErrorCode
  if (code === 'WEIXIN_SESSION_EXPIRED') {
    return { label: '登录失效', type: 'error', hint: '微信已确认登录态失效，请重新扫码接入。普通断网不会要求重新扫码。' }
  }
  if (['CHANNEL_KEY_UNAVAILABLE', 'CHANNEL_CREDENTIAL_UNREADABLE'].includes(code)) {
    return { label: '凭据不可用', type: 'error', hint: '请恢复原通道主密钥并检查密钥来源；不要通过反复扫码掩盖密钥变化。' }
  }
  if (!row.Enabled) return { label: '未接入', type: 'default', hint: '' }
  if (code === 'QQ_AUTH_INVALID' || code === 'QQ_CREDENTIAL_INVALID' || code === 'FEISHU_CREDENTIAL_INVALID') {
    return { label: '配置异常', type: 'error', hint: '请检查已保存的应用凭据和平台配置；这不是普通断网。' }
  }
  if (code) return { label: '重连中', type: 'warning', hint: '服务端正在使用已保存凭据自动重试，无需再次扫码。持续失败请根据错误码检查网络或平台配置。' }
  return { label: '已接入', type: 'success', hint: '' }
}
