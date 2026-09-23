<template>
    <div class="user-avatar-dropdown">
        <n-dropdown :options="options" @select="handleClick">
            <button class="user-chip" type="button">
                <span class="ava">Q</span>
                <i class="fa-solid fa-chevron-down"></i>
            </button>
        </n-dropdown>

        <n-modal preset="card" v-model:show="UpdatePasswordModal.Show" :title="UpdatePasswordModal.Title"
            style="width: 520px" :mask-closable="false">
            <n-form :rules="updatePasswordRuleInline" :model="UpdatePasswordModal.Data" ref="UpdatePasswordForm"
                label-placement="left" label-width="90">
                <n-form-item label="旧用户" path="OldUserName">
                    <n-input v-model:value="UpdatePasswordModal.Data.OldUserName" placeholder="请输入旧用户名" />
                </n-form-item>
                <n-form-item label="新用户" path="NewUserName">
                    <n-input v-model:value="UpdatePasswordModal.Data.NewUserName" placeholder="不填写则默认不修改用户名" />
                </n-form-item>
                <n-form-item label="旧密码" path="OldPassword">
                    <n-input type="password" show-password-on="click" v-model:value="UpdatePasswordModal.Data.OldPassword" />
                </n-form-item>
                <n-form-item label="新密码" path="NewPassword">
                    <n-input type="password" show-password-on="click" v-model:value="UpdatePasswordModal.Data.NewPassword" />
                </n-form-item>
                <n-form-item label="重复密码" path="ConfigPassword">
                    <n-input type="password" show-password-on="click" v-model:value="UpdatePasswordModal.Data.ConfigPassword" />
                </n-form-item>
            </n-form>
            <template #footer>
                <div class="modal-footer">
                    <n-button @click="UpdatePasswordModal.Show = false">取消</n-button>
                    <n-button type="primary" @click="submitChangePassword('UpdatePasswordForm')">确认</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>

<script>
import { UpdatePassword } from '@/api/setting.js'
import { useChatStore } from '@/stores/chat'

export default {
    name: 'User',
    data() {
        const pwdAgainCheck = async (rule, value) => {
            if (!value || value.length < 1) {
                return new Error('重复密码不能为空')
            } else if (
                this.UpdatePasswordModal.Data.NewPassword !=
                this.UpdatePasswordModal.Data.ConfigPassword
            ) {
                return new Error('两次输入密码不一致')
            }
            return true
        }
        return {
            options: [
                { label: '修改密码', key: 'changePassWord' },
                { label: '退出登录', key: 'logout' }
            ],
            UpdatePasswordModal: {
                Show: false,
                Title: '修改密码',
                Data: {}
            },
            updatePasswordRuleInline: {
                OldUserName: [
                    {
                        required: true,
                        message: '请输入旧用户名',
                        trigger: 'blur'
                    }
                ],
                OldPassword: [
                    {
                        required: true,
                        message: '请输入旧密码',
                        trigger: 'blur'
                    }
                ],
                NewPassword: [
                    {
                        required: true,
                        message: '请输入新密码',
                        trigger: 'blur'
                    }
                ],
                ConfigPassword: [
                    {
                        required: true,
                        trigger: 'blur',
                        validator: pwdAgainCheck
                    }
                ]
            }
        }
    },
    methods: {
        logout() {
            // 退出前断开会话长连接并清媒体 blob 缓存，避免令牌失效后仍挂着推送连接
            try {
                useChatStore().disconnect()
            } catch (e) {
                // 实时态未初始化时忽略
            }
            localStorage.removeItem('accessToken')
            this.$router.push({
                name: 'login'
            })
        },
        submitChangePassword(name) {
            var c = this
            this.$refs[name].validate((errors) => {
                if (!errors) {
                    var d = JSON.parse(JSON.stringify(c.UpdatePasswordModal.Data))
                    UpdatePassword(d)
                        .then(function (response) {
                            window.$notification.success({
                                content: '成功',
                                meta: '更新用户名密码成功！',
                                duration: 3000
                            })
                            c.UpdatePasswordModal.Show = false
                        })
                        .catch(function () {
                            window.$message.error('修改密码失败，请稍后重试')
                        })
                }
            })
        },
        handleClick(name) {
            switch (name) {
                case 'logout':
                    this.logout()
                    break
                case 'changePassWord':
                    this.UpdatePasswordModal.Show = true
                    break
            }
        }
    }
}
</script>

<style scoped>
.user-chip {
    background: transparent;
    border: none;
    display: flex;
    align-items: center;
    gap: 8px;
    color: var(--text-2);
    cursor: pointer;
    padding: 4px 6px;
    border-radius: 8px;
    font-size: 13px;
}

.user-chip:hover {
    background: rgba(148, 163, 184, .08);
}

.ava {
    width: 30px;
    height: 30px;
    border-radius: 50%;
    background: linear-gradient(135deg, #22d3ee, #6366f1);
    color: #04121b;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 12px;
    font-weight: 700;
}

.modal-footer {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
}
</style>
