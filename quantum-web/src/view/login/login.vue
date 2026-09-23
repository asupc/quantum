<template>
    <div class="login">
        <div class="login-card glass-card">
            <img class="logo-mark" src="@/assets/images/logo-min.png" alt="Quantum Logo" />
            <h2>量子平台</h2>
            <div class="sub">QUANTUM AUTOMATION CONSOLE</div>
            <login-form @on-success-valid="handleSubmit"></login-form>
            <div class="qr-login" v-if="QrTicket">
                <div class="qr-tip">用量子助手 App（管理员）扫码授权</div>
                <div class="qr-code mono">{{ QrTicket }}</div>
                <div class="qr-tip" :class="{ done: QrStatus === 1 }">{{ QrStatusText }}</div>
            </div>
            <div class="qr-entry" v-else>
                <n-button quaternary type="primary" size="small" @click="startQrLogin">
                    扫码登录（量子助手 App）
                </n-button>
            </div>
        </div>
        <div class="footer" v-html="Footer"></div>
    </div>
</template>

<script>
import LoginForm from '_c/login-form'
import { useUserStore } from '@/stores/user'
import { GetFooter } from '@/api/setting.js'
import { QrCreate, QrStatus } from '@/api/user.js'

export default {
    components: {
        LoginForm
    },
    setup() {
        const userStore = useUserStore()
        return { userStore }
    },
    data() {
        return {
            Footer: '',
            QrTicket: '',
            QrStatus: 0,
            QrTimer: null
        }
    },
    computed: {
        QrStatusText() {
            if (this.QrStatus === 1) return '已授权，正在登录…'
            if (this.QrStatus === 2) return '票据已过期，请重新获取'
            return '等待 App 扫码授权（2 分钟内有效）'
        }
    },
    methods: {
        handleSubmit({ userName, password }) {
            this.userStore.handleLogin({ userName, password }).then((data) => {
                if (!data) return
                localStorage.setItem('accessToken', 'Bearer ' + data)
                // 落点交给主框架：它加载菜单后按「默认首页」标记跳转（首页可能是动态菜单路由）
                this.$router
                    .push({ path: '/' })
                    .catch((err) => {
                        console.error(err)
                    })
            })
        },
        // 扫码登录（A5.5 P2）：票据经 App「扫码授权」后此处轮询取回令牌自动登录
        async startQrLogin() {
            try {
                const res = await QrCreate()
                this.QrTicket = res.Ticket || res.ticket || ''
                this.QrStatus = 0
                this.pollQrStatus()
            } catch (error) {
                this.$notification?.error({ title: '扫码登录', content: String(error), duration: 3000 })
            }
        },
        pollQrStatus() {
            clearInterval(this.QrTimer)
            this.QrTimer = setInterval(async () => {
                try {
                    const res = await QrStatus(this.QrTicket)
                    this.QrStatus = Number(res.Status ?? res.status ?? 0)
                    const token = res.Token || res.token
                    if (this.QrStatus === 1 && token) {
                        clearInterval(this.QrTimer)
                        localStorage.setItem('accessToken', 'Bearer ' + token)
                        // 同密码登录：落到根路径，由主框架按「默认首页」解析最终落点
                        this.$router.push({ path: '/' }).catch(() => {})
                    }
                    if (this.QrStatus === 2) {
                        clearInterval(this.QrTimer)
                    }
                } catch (error) {
                    clearInterval(this.QrTimer)
                }
            }, 2000)
        },
        async getSystemConfig() {
            await GetFooter().then((response) => {
                this.Footer = response
            })
        }
    },
    async mounted() {
        await this.getSystemConfig()
    },
    beforeUnmount() {
        clearInterval(this.QrTimer)
    }
}
</script>

<style scoped>
/* 风格 B 登录页：网格底纹 + 玻璃卡片 + 青色按钮（token 见 styles/tokens.css） */
.login {
    height: 100%;
    position: relative;
    display: flex;
    align-items: center;
    justify-content: center;
    background:
        radial-gradient(1200px 600px at 20% -10%, #1e293b 0%, transparent 60%),
        radial-gradient(900px 500px at 110% 110%, #134e4a 0%, transparent 55%),
        var(--bg);
    overflow: auto;
}

.login::before {
    content: '';
    position: absolute;
    inset: 0;
    background-image:
        linear-gradient(var(--line) 1px, transparent 1px),
        linear-gradient(90deg, var(--line) 1px, transparent 1px);
    background-size: 44px 44px;
    opacity: .35;
    mask-image: radial-gradient(600px 400px at 50% 45%, #000 30%, transparent 100%);
    -webkit-mask-image: radial-gradient(600px 400px at 50% 45%, #000 30%, transparent 100%);
}

.login-card {
    position: relative;
    width: 380px;
    background: rgba(17, 26, 46, .85);
    backdrop-filter: blur(14px);
    border-color: var(--line-2);
    border-radius: 16px;
    padding: 36px 34px;
    box-shadow: 0 24px 80px rgba(0, 0, 0, .6), var(--glow);
    z-index: 1;
}

.logo-mark {
    width: 52px;
    height: 52px;
    margin: 0 auto 16px;
    border-radius: 13px;
    display: block;
    object-fit: cover;
    box-shadow: 0 8px 24px rgba(0, 0, 0, .5);
}

.login-card h2 {
    text-align: center;
    font-size: 22px;
    margin: 0 0 4px;
    color: var(--text);
}

.login-card .sub {
    text-align: center;
    color: var(--text-3);
    font-size: 12px;
    margin-bottom: 24px;
    letter-spacing: 1px;
}

.footer {
    position: absolute;
    bottom: 14px;
    left: 0;
    right: 0;
    text-align: center;
    color: var(--text-3);
    font-size: 12px;
    z-index: 1;
}

/* 扫码登录面板（A5.5 P2）：票据文本 + 轮询状态 */
.qr-login {
    margin-top: 18px;
    padding-top: 14px;
    border-top: 1px dashed var(--line);
}
.qr-login .qr-code {
    margin: 10px auto;
    padding: 8px 12px;
    max-width: 100%;
    font-size: 12px;
    word-break: break-all;
    background: var(--bg, #0f172a);
    border-radius: 6px;
}
.qr-login .qr-tip {
    font-size: 12px;
    color: var(--text-3, #94a3b8);
    text-align: center;
}
.qr-login .qr-tip.done {
    color: #10b981;
}
.qr-entry {
    margin-top: 14px;
    text-align: center;
}
</style>
