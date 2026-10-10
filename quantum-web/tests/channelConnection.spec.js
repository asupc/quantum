import { describe, it, expect } from 'vitest'
import { channelConnectionState } from '../src/utils/channelConnection.js'

describe('机器人连接状态', () => {
  it.each(['QQBot', 'WeixinBot', 'FeishuBot'])('%s 临时故障提示自动重连而不是重新扫码', (Platform) => {
    const state = channelConnectionState({ Platform, Enabled: true, LastErrorCode: 'NETWORK_TIMEOUT' })
    expect(state.label).toBe('重连中')
    expect(state.type).toBe('warning')
    expect(state.hint).toContain('无需再次扫码')
  })
  it('明确登录失效即使已停用也提示重新扫码', () => {
    const state = channelConnectionState({ Enabled: false, LastErrorCode: 'WEIXIN_SESSION_EXPIRED' })
    expect(state.label).toBe('登录失效')
    expect(state.hint).toContain('重新扫码接入')
  })
  it.each(['CHANNEL_KEY_UNAVAILABLE', 'CHANNEL_CREDENTIAL_UNREADABLE'])('%s 引导恢复原密钥而不是误报平台退出', (LastErrorCode) => {
    const state = channelConnectionState({ Enabled: true, LastErrorCode })
    expect(state.label).toBe('凭据不可用')
    expect(state.hint).toContain('恢复原通道主密钥')
  })
  it('恢复后旧错误清除显示已接入', () => {
    expect(channelConnectionState({ Enabled: true }).label).toBe('已接入')
    expect(channelConnectionState({ Enabled: true }).hint).toBe('')
  })
  it('手动停用不显示正在重连', () => {
    expect(channelConnectionState({ Enabled: false, LastErrorCode: 'NETWORK_ERROR' }).label).toBe('未接入')
  })
  it('QQ 应用鉴权失败需检查配置而不是重新扫码', () => {
    expect(channelConnectionState({ Enabled: true, LastErrorCode: 'QQ_AUTH_INVALID' }).label).toBe('配置异常')
  })
})
