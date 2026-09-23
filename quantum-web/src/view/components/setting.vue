<template>
    <div id="System-Config">
        <n-form ref="SystemConfig" :model="SystemConfig" label-placement="left" label-width="150"
            style="padding: 0 10px">
            <n-alert type="info" :show-icon="true" style="margin-bottom: 16px">
                管理员用户名与登录密码请通过右上角用户菜单「修改密码」维护；此处为平台运行参数。
            </n-alert>

            <n-divider title-placement="left">安全配置</n-divider>
            <n-grid :cols="24" :x-gap="16">
                <n-gi :span="12">
                    <n-form-item label="Open AppKey">
                        <n-input-group>
                            <n-input-group-label>
                                <n-tooltip trigger="hover" placement="top" style="max-width: 400px">
                                    <template #trigger>
                                        <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                    </template>
                                    Open API / 外触内执的鉴权凭据，权限极高，请勿泄露。已配置时以掩码显示，留空保存不覆盖原值。
                                </n-tooltip>
                            </n-input-group-label>
                            <n-input type="password" show-password-on="click" placeholder="留空表示不修改"
                                v-model:value="SystemConfig.AppKey" />
                        </n-input-group>
                    </n-form-item>
                </n-gi>
                <n-gi :span="12">
                    <n-form-item label="可信反向代理">
                        <n-input-group>
                            <n-input-group-label>
                                <n-tooltip trigger="hover" placement="top" style="max-width: 400px">
                                    <template #trigger>
                                        <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                    </template>
                                    逗号分隔，支持 IP 或 CIDR（如 172.17.0.1,172.16.0.0/12）。仅这些直连方的
                                    X-Forwarded-For 会被采信（登录限流、白名单取 IP 的依据）。留空 = 不信任任何
                                    XFF（公网直连部署的默认安全态）。
                                </n-tooltip>
                            </n-input-group-label>
                            <n-input placeholder="如 172.17.0.1，留空不信任任何 XFF"
                                v-model:value="SystemConfig.KnownProxies" />
                        </n-input-group>
                    </n-form-item>
                </n-gi>
                <n-gi :span="12">
                    <n-form-item label="跨域白名单">
                        <n-input-group>
                            <n-input-group-label>
                                <n-tooltip trigger="hover" placement="top" style="max-width: 400px">
                                    <template #trigger>
                                        <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                    </template>
                                    允许跨域调用 API 的来源列表（逗号分隔 Origin，如 https://admin.example.com）。
                                    留空 = 拒绝一切跨域（前端与管理端同源部署时无需配置）。
                                </n-tooltip>
                            </n-input-group-label>
                            <n-input placeholder="逗号分隔 Origin，留空拒绝一切跨域"
                                v-model:value="SystemConfig.AllowedOrigins" />
                        </n-input-group>
                    </n-form-item>
                </n-gi>
                <n-gi :span="12">
                    <n-form-item label="Swagger">
                        <n-checkbox v-model:checked="SystemConfig.EnableSwagger">
                            <span class="checkbox-label">生产环境暴露 Swagger</span>
                            <n-tooltip trigger="hover" placement="top">
                                <template #trigger>
                                    <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                </template>
                                公网部署保持关闭；本项与可信代理、跨域白名单均保存后重启服务生效。
                            </n-tooltip>
                        </n-checkbox>
                    </n-form-item>
                </n-gi>
            </n-grid>

            <n-divider title-placement="left">消息与推送</n-divider>
            <n-grid :cols="24" :x-gap="16">
                <n-gi :span="12">
                    <n-form-item label="服务地址">
                        <n-input-group>
                            <n-input-group-label>
                                <n-tooltip trigger="hover" placement="top" style="max-width: 400px">
                                    <template #trigger>
                                        <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                    </template>
                                    对外服务地址（内网地址、外网地址或域名），配置示例：http://118.8.8.8:5088
                                </n-tooltip>
                            </n-input-group-label>
                            <n-input placeholder="http://118.8.8.8:5088" v-model:value="SystemConfig.ServerPath" />
                        </n-input-group>
                    </n-form-item>
                </n-gi>
                <n-gi :span="12">
                    <n-form-item label="消息发送间隔">
                        <n-input-group>
                            <n-input-group-label>
                                <n-tooltip trigger="hover" placement="top" style="max-width: 400px">
                                    <template #trigger>
                                        <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                    </template>
                                    通知消息发送节流：发送队列每 N×0.1 秒投递一条，防止通知过频刷屏。
                                </n-tooltip>
                            </n-input-group-label>
                            <n-input v-model:value="SystemConfig.MessageInterval" placeholder="如 5 = 每 0.5 秒一条" />
                        </n-input-group>
                    </n-form-item>
                </n-gi>
                <n-gi :span="12">
                    <n-form-item label="队列处理间隔">
                        <n-input-group>
                            <n-input-group-label>
                                <n-tooltip trigger="hover" placement="top" style="max-width: 400px">
                                    <template #trigger>
                                        <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                    </template>
                                    消息处理使用队列模式，间隔 N 毫秒处理一条。
                                </n-tooltip>
                            </n-input-group-label>
                            <n-input v-model:value="SystemConfig.MessageQueueInterval" placeholder="毫秒，默认 100" />
                        </n-input-group>
                    </n-form-item>
                </n-gi>
                <n-gi :span="12">
                    <n-form-item label="黑名单用户">
                        <n-input-group>
                            <n-input-group-label>
                                <n-tooltip trigger="hover" placement="top" style="max-width: 400px">
                                    <template #trigger>
                                        <i class="fa-solid fa-circle-exclamation" style="color: crimson"></i>
                                    </template>
                                    黑名单用户 ID，多个用逗号隔开；名单内用户发来的指令消息将被忽略。
                                </n-tooltip>
                            </n-input-group-label>
                            <n-input placeholder="用户 ID，多个用逗号隔开" v-model:value="SystemConfig.BlackQQ" />
                        </n-input-group>
                    </n-form-item>
                </n-gi>
                <n-gi :span="12">
                    <n-form-item label="登录提醒">
                        <n-checkbox v-model:checked="SystemConfig.LoginNotify">
                            <span class="checkbox-label">管理员登录时推送提醒</span>
                        </n-checkbox>
                    </n-form-item>
                </n-gi>
            </n-grid>

            <n-divider title-placement="left">站点与数据</n-divider>
            <n-form-item label="自定义页脚">
                <n-input type="textarea" :rows="3" v-model:value="SystemConfig.Footer"
                    placeholder="展示在登录页面页脚部分，支持html标签" />
            </n-form-item>

            <n-form-item :show-feedback="false">
                <n-space>
                    <n-button type="warning" @click="databaseShrink">
                        <template #icon>
                            <i class="fa-solid fa-box-archive"></i>
                        </template>
                        收缩数据库文件
                    </n-button>
                    <n-button type="success" @click="handleSubmit('SystemConfig')">
                        <template #icon>
                            <i class="fa-solid fa-check"></i>
                        </template>
                        保存配置
                    </n-button>
                </n-space>
            </n-form-item>
        </n-form>
    </div>
</template>

<script>
import { GetSetting, UpdateSetting, DatabaseShrink } from '@/api/setting.js'
export default {
    name: 'system-setting',
    data() {
        return {
            SystemConfig: {},
            UploadHeaders: {}
        }
    },
    methods: {
        databaseShrink() {
            let systemConfigApp = this
            this.$dialog.warning({
                title: '收缩数据库文件',
                content: `因数据频繁更新写入删除等会导致数据库文件变得庞大，影响数据库性能，建议定期执行收缩。`,
                positiveText: '确定',
                negativeText: '取消',
                onPositiveClick: function () {
                    DatabaseShrink()
                        .then(function (response) {
                            systemConfigApp.$notification.success({
                                title: '成功',
                                content: '收缩数据库文件成功！',
                                duration: 3000
                            })
                        })
                }
            })
        },
        handleSubmit(name) {
            let systemConfigApp = this
            this.$dialog.warning({
                title: '保存系统设置',
                content: '确认保存系统设置？可信代理、跨域白名单、Swagger 等启动期配置将在重启服务后生效。',
                positiveText: '确定',
                negativeText: '取消',
                onPositiveClick: function () {
                    UpdateSetting(systemConfigApp.SystemConfig)
                        .then(function (response) {
                            systemConfigApp.$notification.success({
                                title: '成功',
                                content: '更新配置信息成功！',
                                duration: 3000
                            })
                            systemConfigApp.$emit('save-success')
                            // 重新拉取：让掩码字段（AppKey）回显为脱敏值，避免界面残留明文
                            systemConfigApp.getSystemConfig()
                        })
                        .catch(function (error) {
                            systemConfigApp.$notification.error({
                                title: '失败',
                                content: String(error),
                                duration: 5000
                            })
                        })
                }
            })
        },
        getSystemConfig() {
            GetSetting().then((response) => {
                this.SystemConfig = response
            })
        }
    }
}
</script>

<style scoped>
#System-Config {
    padding: 20px;
}

.checkbox-label {
    font-weight: 500;
}
</style>
