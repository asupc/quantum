<template>
  <div class="channel-page">
    <n-alert type="warning" :bordered="false" class="intro">
      单用户模式：保存机器人凭据或完成扫码后即可使用，所有能与机器人的私聊账号均可触发系统指令和脚本任务。
      请勿公开机器人给不信任的人；平台接口“已受理”不等于用户已收到，首次上线请用测试账号核验。
    </n-alert>
    <n-space class="toolbar" align="center">
      <n-button :loading="loading" @click="load">刷新状态</n-button>
      <span class="hint">服务端首次启动会自动生成通道主密钥并写入 appsettings.json（丢失将无法解密已保存登录态，请随配置一起备份）；如需更高隔离可用环境变量 QUANTUM_CHANNEL_KEY_FILE 覆盖。QQ 机器人须先在开放平台提交审核并<strong>手动上线</strong>，否则客户端里搜不到；无公网 IP 请使用沙箱环境。「发送测试消息」只能按最近一条你发给机器人的消息原路回（三家平台都不支持凭据主动外发）；若提示没有可回复对象，先在手机上给机器人发一条纯文本再点。</span>
    </n-space>

    <n-grid cols="1 m:2" responsive="screen" :x-gap="16" :y-gap="16">
      <n-gi v-for="row in statuses" :key="row.Platform">
        <n-card :title="platformName(row.Platform) + ' · 私聊'" size="small">
          <template #header-extra>
            <n-tag :type="row.LastErrorCode ? 'error' : row.Enabled ? 'success' : 'default'">
              {{ row.LastErrorCode ? '连接异常' : row.Enabled ? '已接入' : '未接入' }}
            </n-tag>
          </template>
          <n-space vertical :size="10">
            <div class="row"><span>机器人</span><strong>{{ row.BotIdMasked || '未配置' }}</strong></div>
            <div class="row"><span>待发条数</span><strong>{{ row.PendingOutboxCount || 0 }}</strong></div>
            <div v-if="row.LastErrorCode" class="row error"><span>平台错误码</span><code>{{ row.LastErrorCode }}</code></div>
            <div v-if="row.Environment" class="row"><span>接口环境</span><strong>{{ row.Environment === 'Sandbox' ? '沙箱' : '正式' }}</strong></div>
            <n-space>
              <template v-if="row.Platform === 'QQBot'">
                <n-button size="small" @click="qqModal = true">配置机器人</n-button>
              </template>
              <template v-else-if="row.Platform === 'WeixinBot'">
                <n-button size="small" type="primary" @click="beginWeixin(row)">扫码接入</n-button>
              </template>
              <template v-else>
                <n-button size="small" type="primary" @click="beginFeishuQr(row)">扫码接入</n-button>
                <n-button size="small" quaternary @click="feishuModal = true">手动配置（高级）</n-button>
              </template>
              <n-button size="small" :disabled="!row.Enabled" :loading="testingPlatform === row.Platform"
                @click="sendTest(row)">发送测试消息</n-button>
              <n-button size="small" type="error" quaternary :disabled="!row.Configured"
                @click="unbind(row)">断开接入</n-button>
              <n-button size="small" @click="showDelivery(row)">投递记录</n-button>
            </n-space>
          </n-space>
        </n-card>
      </n-gi>
    </n-grid>

    <n-modal v-model:show="qqModal" preset="card" title="配置 QQ 官方机器人" class="channel-modal"
      @after-leave="qqForm.AppSecret = ''">
      <n-alert type="warning" :bordered="false">更新旧账号会废止绑定与未发送回复。AppSecret 只会发往服务端加密保存，本页不缓存。</n-alert>
      <div class="qr-guide">
        <n-qr-code :value="qqPortalUrl" :size="112" :padding="8" />
        <div class="hint">
          <p>① 用手机 QQ 扫码或点下方链接打开 QQ 开放平台；② 创建/查看机器人（个人开发者可免费创建，须提交审核并手动上线）；③ 把机器人详情页的 AppID / AppSecret 复制到下方保存。
            QQ 官方没有「扫码即绑定」的授权接口，这一步只是页面引导，凭据仍由表单提交并加密入库。</p>
          <a :href="qqPortalUrl" target="_blank" rel="noreferrer">不方便扫码？电脑端直接打开 QQ 开放平台</a>
        </div>
      </div>
      <n-divider style="margin: 10px 0" />
      <n-alert type="info" :bordered="false" style="margin-bottom: 12px">
        <strong>没有公网 IP 就选「沙箱」</strong>：平台对新增机器人默认启用 IP 白名单，正式环境只放行白名单内的公网 IP；
        沙箱环境不受该限制，<strong>接入地址留空即用官方默认沙箱地址</strong>（仅当腾讯迁移沙箱域名时才需要手动覆盖）。
      </n-alert>
      <n-form label-placement="left" label-width="100" class="form">
        <n-form-item label="AppID"><n-input v-model:value="qqForm.AppId" maxlength="128" /></n-form-item>
        <n-form-item label="AppSecret"><n-input v-model:value="qqForm.AppSecret" type="password" show-password-on="click" maxlength="512" /></n-form-item>
        <n-form-item label="接口环境">
          <n-radio-group v-model:value="qqForm.Environment">
            <n-radio value="Production">正式环境</n-radio>
            <n-radio value="Sandbox">沙箱环境（无公网 IP 选这个）</n-radio>
          </n-radio-group>
        </n-form-item>
        <n-form-item v-if="qqForm.Environment === 'Sandbox'" label="沙箱地址">
          <n-input v-model:value="qqForm.ApiBase" placeholder="留空即用官方默认 https://sandbox.api.sgroup.qq.com" />
        </n-form-item>
      </n-form>
      <template #footer><n-button type="primary" :loading="submitting" @click="saveQq">保存并接入</n-button></template>
    </n-modal>

    <n-modal v-model:show="feishuModal" preset="card" title="手动配置飞书企业自建应用（高级）" class="channel-modal"
      @after-leave="feishuForm.AppSecret = ''">
      <n-alert type="success" :bordered="false" style="margin-bottom: 8px">
        一般<strong>无需手动填写</strong>：使用上面的「扫码接入」，用飞书 App 扫一次码即可自动创建个人应用并保存凭据。
        本弹窗仅供已有企业自建应用的高级场景。
      </n-alert>
      <n-alert type="info" :bordered="false">
        使用<strong>企业自建应用</strong>。需在飞书开放平台开启「机器人」能力、申请
        <code>im:message</code>（收发单聊）与 <code>im:message.p2p_msg:readonly</code>（读取单聊）权限，
        事件订阅选择「<strong>使用长连接接收事件</strong>」并添加「接收消息 im.message.receive_v1」。
        配置后必须<strong>创建版本并发布</strong>，只保存不发布收不到任何事件。
      </n-alert>
      <n-alert type="warning" :bordered="false" style="margin-top: 8px">
        本通道走长连接，<strong>不需要公网 IP / 备案域名 / 回调地址</strong>。AppSecret 只发往服务端加密保存，本页不缓存。
      </n-alert>
      <n-divider style="margin: 10px 0" />
      <n-form label-placement="left" label-width="110" class="form">
        <n-form-item label="App ID">
          <n-input v-model:value="feishuForm.AppId" placeholder="cli_xxxxxxxxxx" maxlength="128" />
        </n-form-item>
        <n-form-item label="App Secret">
          <n-input v-model:value="feishuForm.AppSecret" type="password" show-password-on="click" maxlength="512" />
        </n-form-item>
      </n-form>
      <template #footer>
        <n-button type="primary" :loading="submitting" @click="saveFeishu">保存并接入</n-button>
      </template>
    </n-modal>

    <n-modal v-model:show="fsQrModal" preset="card" title="飞书扫码绑定" class="channel-modal" :mask-closable="false"
      @after-leave="stopFsQr">
      <n-space vertical align="center" :size="12">
        <n-alert type="warning" :bordered="false">请用<strong>飞书 App</strong> 扫码并在手机上确认授权：飞书会自动创建一个个人应用机器人（无需去开放平台手动建应用），AppSecret 由服务端加密保存、不回传本页。仅在可信设备上操作。</n-alert>
        <n-qr-code v-if="fsQrUrl" :value="fsQrUrl" :size="210" :padding="12" />
        <p class="hint">{{ fsQrStatusText }}</p>
      </n-space>
    </n-modal>

    <n-modal v-model:show="qrModal" preset="card" title="微信扫码绑定" class="channel-modal" :mask-closable="false" @after-leave="stopQr">
      <n-space vertical align="center" :size="12">
        <n-alert type="warning" :bordered="false">仅在可信设备扫描。二维码与验证码不要截图或转发；确认后令牌不会返回前端。</n-alert>
        <n-qr-code v-if="qrContent" :value="qrContent" :size="210" :padding="12" />
        <p class="hint">{{ qrStatusText }}</p>
        <n-input v-if="qrStatus === 'need_verifycode'" v-model:value="verifyCode" placeholder="平台要求的验证码"
          type="password" maxlength="32" style="max-width: 260px" />
        <n-button v-if="qrStatus === 'need_verifycode'" type="primary" @click="pollQr(true)">提交验证码</n-button>
      </n-space>
    </n-modal>

    <n-modal v-model:show="deliveryModal" preset="card" :title="platformName(deliveryPlatform) + ' · 投递记录（最多 50 条）'" class="channel-modal--wide">
      <n-alert type="info" :bordered="false" style="margin-bottom: 12px">Accepted 只表示平台接口受理，不保证手机收到；Unknown 不自动重发，需人工核查。</n-alert>
      <n-data-table :columns="deliveryColumns" :data="deliveries" :pagination="{ pageSize: 10 }" size="small" />
    </n-modal>
  </div>
</template>

<script setup>
defineOptions({ name: 'ChannelIndex' })
import { computed, h, onActivated, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { NButton, useDialog, useMessage } from 'naive-ui'
import {
  getChannelStatus, getChannelDelivery, configureQq, startWeixinQr, pollWeixinQr,
  unbindChannel, retryChannelDelivery, configureFeishu, startFeishuQr, pollFeishuQr, sendChannelTest
} from '@/api/channel'

const dialog = useDialog()
const message = useMessage()
const platformName = (p) => ({
  QQBot: 'QQ 官方机器人', WeixinBot: '微信机器人', FeishuBot: '飞书机器人'
}[p] || p)
// QQ 开放平台控制台是腾讯对外的公网入口（非私有服务地址），这里只用于扫码引导到创建/查看机器人的页面
const qqPortalUrl = 'https://q.qq.com/'
const loading = ref(false)
const submitting = ref(false)
const statuses = ref([])
// 自检消息按平台串行触发，避免连点把同一条原路路由刷出多条平台消息
const testingPlatform = ref('')
const qqModal = ref(false)
const qqForm = reactive({ AppId: '', AppSecret: '', Environment: 'Production', ApiBase: '' })
const feishuModal = ref(false)
const feishuForm = reactive({ AppId: '', AppSecret: '' })
const qrModal = ref(false)
const qrContent = ref('')
const qrSession = ref('')
const qrStatus = ref('wait')
const verifyCode = ref('')
const qrBusy = ref(false)
let qrTimer = null
const deliveryModal = ref(false)
const deliveryPlatform = ref('')
const deliveries = ref([])
const deliveryColumns = [
  { title: '受理时间（UTC）', key: 'CreatedAtUtc', width: 190 },
  { title: '状态', key: 'Status', width: 95 },
  { title: '用途', key: 'Purpose', width: 90 },
  { title: '尝试', key: 'Attempts', width: 70 },
  { title: '错误码', key: 'ErrorCode', width: 160, render: (row) => row.ErrorCode || '—' },
  { title: '操作', key: 'actions', width: 100, render: (row) =>
    ['Failed', 'Unknown'].includes(row.Status)
      ? h(NButton, { size: 'tiny', type: 'warning', onClick: () => retryDelivery(row) }, { default: () => '人工重试' })
      : '—' }
]
const qrStatusText = computed(() => ({
  wait: '等待扫码…', scaned: '已扫码，等待手机确认…', need_verifycode: '需要验证码',
  confirmed: '扫码成功，机器人已接入。', expired: '二维码已过期，请重新发起。',
  verify_code_blocked: '验证码被平台限制，请重新发起。', binded_redirect: '机器人已绑定到其他实例，请检查登录状态。'
}[qrStatus.value] || '平台返回未识别状态，请检查配置'))

// 飞书扫码绑定（设备码授权）：飞书没有微信那样的中间「已扫码」态，轮询只会在
// waiting / confirmed / expired / failed 间变化；轮询间隔以 begin 返回值为准（下限 2s）
const fsQrModal = ref(false)
const fsQrUrl = ref('')
const fsQrSession = ref('')
const fsQrStatus = ref('waiting')
const fsQrError = ref('')
const fsQrBusy = ref(false)
let fsQrInterval = 5000
let fsQrTimer = null
const fsQrStatusText = computed(() => ({
  waiting: '等待扫码… 请用飞书 App 扫描二维码并确认授权',
  confirmed: '扫码成功，机器人已接入。',
  expired: '二维码已过期，请重新发起。',
  failed: fsQrError.value || '飞书扫码授权失败，请重新发起。'
}[fsQrStatus.value] || '平台返回未识别状态'))

// 模态关闭后焦点仍留在被 aria-hidden 的容器里，浏览器会告警——所有模态都关闭时主动失焦
const anyModalOpen = computed(() =>
  qqModal.value || qrModal.value ||
  deliveryModal.value || feishuModal.value || fsQrModal.value)
watch(anyModalOpen, (open) => { if (!open) document.activeElement?.blur?.() })

async function load() {
  loading.value = true
  try { statuses.value = await getChannelStatus() || [] } catch { /* 统一拦截器显示错误 */ }
  finally { loading.value = false }
}
function confirmDanger(title, content, action) {
  dialog.warning({ title, content, positiveText: '确认', negativeText: '取消', onPositiveClick: action })
}
async function saveQq() {
  if (!qqForm.AppId.trim() || !qqForm.AppSecret.trim()) return message.warning('请填写完整的测试机器人凭据')
  const existing = statuses.value.find((r) => r.Platform === 'QQBot')
  const submit = async (confirm) => {
    const secret = qqForm.AppSecret
    submitting.value = true
    try {
      // 沙箱地址留空由服务端取官方默认接入点；正式环境留空即用正式接入点。
      const apiBase = qqForm.ApiBase.trim() || null
      await configureQq({ AppId: qqForm.AppId.trim(), AppSecret: secret, ConfirmRebind: confirm,
        ApiBase: apiBase, Environment: qqForm.Environment })
      qqModal.value = false
      await load()
      message.success('机器人已接入')
    } catch { /* 统一拦截器显示错误 */ }
    // 无论成败都清掉输入框里的 AppSecret：失败时也不让凭据残留在前端
    finally { qqForm.AppSecret = ''; submitting.value = false }
  }
  await submit(Boolean(existing?.Configured))
}
async function saveFeishu() {
  if (!feishuForm.AppId.trim() || !feishuForm.AppSecret.trim())
    return message.warning('请填写完整的飞书应用凭据')
  const existing = statuses.value.find((r) => r.Platform === 'FeishuBot')
  const submit = async (confirm) => {
    const secret = feishuForm.AppSecret
    submitting.value = true
    try {
      await configureFeishu({ AppId: feishuForm.AppId.trim(), AppSecret: secret, ConfirmRebind: confirm })
      feishuModal.value = false
      await load()
      message.success('飞书机器人已接入')
    } catch { /* 统一拦截器显示错误 */ }
    finally { feishuForm.AppSecret = ''; submitting.value = false }
  }
  await submit(Boolean(existing?.Configured))
}
async function beginWeixin(row) {
  // 协议版本元数据由服务端内置默认值解析（QUANTUM_CHANNEL_WEIXIN_* 环境变量可覆盖），
  // 扫码时无需填写协议版本。
  const run = async (confirm) => {
    stopQr()
    try {
      const result = await startWeixinQr({ ConfirmRebind: confirm })
      qrContent.value = result.QrContent
      qrSession.value = result.SessionId
      qrStatus.value = 'wait'
      qrModal.value = true
      qrTimer = window.setInterval(() => pollQr(false), 2500)
      await load()
    } catch { /* 统一拦截器显示错误 */ }
  }
  await run(Boolean(row.Configured))
}
async function beginFeishuQr(row) {
  const run = async (confirmRebind) => {
    stopFsQr()
    try {
      const result = await startFeishuQr(confirmRebind)
      fsQrUrl.value = result.QrUrl
      fsQrSession.value = result.SessionId
      fsQrStatus.value = 'waiting'
      fsQrError.value = ''
      fsQrInterval = Math.max(2000, (result.IntervalSeconds || 5) * 1000)
      fsQrModal.value = true
      fsQrTimer = window.setInterval(() => pollFsQr(), fsQrInterval)
      await load()
    } catch { /* 统一拦截器显示错误 */ }
  }
  await run(Boolean(row.Configured))
}
async function pollFsQr() {
  if (fsQrBusy.value || !fsQrSession.value) return
  fsQrBusy.value = true
  try {
    const result = await pollFeishuQr(fsQrSession.value)
    fsQrStatus.value = result.Status
    fsQrError.value = result.Error || ''
    if (['confirmed', 'expired', 'failed'].includes(result.Status)) {
      stopFsQr()
      fsQrUrl.value = ''
      await load()
      if (result.Status === 'confirmed') message.success('扫码成功，机器人已接入')
    }
  } catch { stopFsQr() }
  finally { fsQrBusy.value = false }
}
function stopFsQr() { if (fsQrTimer) window.clearInterval(fsQrTimer); fsQrTimer = null; fsQrSession.value = '' }
async function pollQr(withCode) {
  if (qrBusy.value || !qrSession.value) return
  qrBusy.value = true
  try {
    const result = await pollWeixinQr(qrSession.value, withCode ? verifyCode.value : null)
    qrStatus.value = result.Status
    if (result.Status === 'need_verifycode') {
      if (qrTimer) window.clearInterval(qrTimer)
      qrTimer = null
    } else if (withCode && result.Status === 'wait' && !qrTimer) {
      qrTimer = window.setInterval(() => pollQr(false), 2500)
    }
    if (['confirmed', 'expired', 'verify_code_blocked', 'binded_redirect'].includes(result.Status)) {
      stopQr()
      qrContent.value = ''
      await load()
      if (result.Status === 'confirmed') message.success('扫码成功，机器人已接入')
    }
  } catch { stopQr() }
  finally { qrBusy.value = false; if (withCode) verifyCode.value = '' }
}
function stopQr() { if (qrTimer) window.clearInterval(qrTimer); qrTimer = null; qrSession.value = ''; verifyCode.value = '' }
function unbind(row) {
  confirmDanger('断开机器人接入？', '将停止收发并清除平台凭据；历史聊天保留，重新接入需要重新配置或扫码。', async () => {
    try { await unbindChannel(row.Platform); stopQr(); await load() }
    catch { /* 统一拦截器显示错误 */ }
  })
}
function retryDelivery(row) {
  confirmDanger('确认人工重试？', '先在手机侧核对原消息未到达。Unknown 可能已被平台接受，重试有重复显示风险；已过期或已换绑的回复服务端会拒绝。', async () => {
    try {
      await retryChannelDelivery(deliveryPlatform.value, row.Id)
      deliveries.value = await getChannelDelivery(deliveryPlatform.value) || []
      await load()
      message.success('已重新排队（不代表平台收到）')
    } catch { /* 统一拦截器显示错误 */ }
  })
}
async function sendTest(row) {
  testingPlatform.value = row.Platform
  try {
    const result = await sendChannelTest(row.Platform)
    message.success(`自检消息已入队（状态 ${result?.Status || 'Pending'}），请在手机侧确认收到；结果可在「投递记录」查看`)
    await load()
  } catch { /* 统一拦截器显示错误：无可用回复路由时服务端会说明需先给机器人发一条消息 */ }
  finally { testingPlatform.value = '' }
}
async function showDelivery(row) {
  try {
    deliveryPlatform.value = row.Platform
    deliveries.value = await getChannelDelivery(row.Platform) || []
    deliveryModal.value = true
  } catch { /* 统一拦截器显示错误 */ }
}
let firstLoad = true
onMounted(load)
// 页签被 keep-alive 缓存，切回时刷新机器人接入状态
onActivated(() => { if (firstLoad) { firstLoad = false; return } load() })
onUnmounted(() => { stopQr(); stopFsQr() })
</script>

<style scoped>
.channel-page { padding: 16px; color: #dce9fc; }
.intro { margin-bottom: 14px; }
.toolbar { margin: 14px 0; }
.hint { font-size: 12px; color: #88a2c4; }
.row { display: flex; align-items: center; justify-content: space-between; gap: 20px; font-size: 13px; }
.row span { color: #8ba4c8; }
.row strong, .row code { overflow-wrap: anywhere; }
.row.error code { color: #ffad93; }
.form { margin-top: 16px; }
.challenge { margin: 22px auto; padding: 18px; font-family: monospace; text-align: center; font-size: 26px; letter-spacing: 3px; background: #10203a; border-radius: 8px; }
</style>

<style>
/* n-modal 会传送到 body，节点上不带本组件的 scope 属性，
   写在 scoped 块里的规则选择器匹配不到（实测弹窗被撑满视口、引导区排版丢失）。
   故弹窗内部的样式一律放非 scoped 块，并用 channel-modal 前缀避免影响其它页面的弹窗。
   修饰类必须排在 .channel-modal 之后：同为单类选择器，后写的才覆盖得了宽度。 */
.channel-modal { width: 400px; max-width: calc(100vw - 32px); }
.channel-modal--form { width: 520px; max-width: calc(100vw - 32px); }
.channel-modal--wide { width: 850px; max-width: calc(100vw - 32px); }
.channel-modal .qr-guide { display: flex; align-items: center; gap: 14px; margin-top: 14px; }
.channel-modal .qr-guide .hint { flex: 1; margin: 0; }
.channel-modal .qr-guide a, .channel-modal .src-links a {
  display: block; font-size: 12px; line-height: 1.8; color: var(--accent); overflow-wrap: anywhere;
}
.channel-modal .src-links { margin-top: 4px; }
.channel-modal .src-links p { margin: 0 0 6px; }
</style>
