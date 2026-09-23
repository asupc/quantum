<template>
    <div class="login-form-wrapper">
        <n-form ref="loginForm" :model="form" :rules="rules">
            <n-form-item path="userName">
                <n-input v-model:value="form.userName" size="large" placeholder="请输入量子账号" @keyup.enter="handleSubmit">
                    <template #prefix>
                        <i class="fa-solid fa-user input-icon"></i>
                    </template>
                </n-input>
            </n-form-item>
            <n-form-item path="password">
                <n-input type="password" show-password-on="click" v-model:value="form.password" size="large"
                    placeholder="请输入量子密码" @keyup.enter="handleSubmit">
                    <template #prefix>
                        <i class="fa-solid fa-lock input-icon"></i>
                    </template>
                </n-input>
            </n-form-item>
            <n-button type="primary" block size="large" :loading="loading" class="login-button" @click="handleSubmit">
                {{ loading ? '登录中...' : '登 录' }}
            </n-button>
        </n-form>

        <div class="login-hints">
            如忘记密码，请修改部署目录下 appsettings.json 中 Quantum 节的 PassWord 字段，保存后即时生效
        </div>
    </div>
</template>

<script>
export default {
    name: 'LoginForm',
    props: {
        userNameRules: {
            type: Array,
            default: () => {
                return [{ required: true, message: '请输入您的量子账号', trigger: 'blur' }]
            }
        },
        passwordRules: {
            type: Array,
            default: () => {
                return [{ required: true, message: '请输入您的量子密码', trigger: 'blur' }]
            }
        }
    },
    emits: ['on-success-valid'],
    data() {
        return {
            loading: false,
            form: {
                userName: '',
                password: ''
            }
        }
    },
    computed: {
        rules() {
            return {
                userName: this.userNameRules,
                password: this.passwordRules
            }
        }
    },
    methods: {
        handleSubmit() {
            this.$refs.loginForm.validate((errors) => {
                if (!errors) {
                    this.loading = true;
                    // 添加延迟以显示加载状态
                    setTimeout(() => {
                        this.$emit('on-success-valid', {
                            userName: this.form.userName,
                            password: this.form.password
                        });
                        this.loading = false;
                    }, 800);
                }
            })
        }
    }
}
</script>

<style scoped>
.login-form-wrapper {
    padding: 0;
}

.input-icon {
    color: var(--accent);
    font-size: 14px;
}

.login-button {
    margin-top: 10px;
    height: 44px;
    font-size: 15px;
    font-weight: 700;
    letter-spacing: 2px;
    --n-border-radius: 8px;
}

.login-hints {
    margin-top: 22px;
    padding: 12px 14px;
    background: rgba(148, 163, 184, .06);
    border-radius: 8px;
    border-left: 3px solid var(--accent);
    color: var(--text-3);
    font-size: 12px;
    line-height: 1.7;
}
</style>
