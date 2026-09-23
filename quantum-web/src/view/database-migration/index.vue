<style scoped>
.alert-question {
    color: var(--danger);
}

.alert-answer {
    color: var(--text-2);
}
</style>
<template>
    <div>
        <n-alert type="warning" show-icon>
            <p class="alert-question">
                请不要轻易尝试使用该功能！！！！！！！！！！！！！！！
            </p>
            <p class="alert-question">
                请不要轻易尝试使用该功能！！！！！！！！！！！！！！！
            </p>
            <p class="alert-question">
                请不要轻易尝试使用该功能！！！！！！！！！！！！！！！
            </p>
            <p class="alert-question">1. Quantum是如何完成迁移工作的？</p>
            <p class="alert-answer">
                第一步：系统自动生成新的数据库。<br />
                第二步：将原来数据库中的重要数据提取出来写入新的数据库。<br />
                第三步：自动修改数据库配置文件。<br />
            </p>
            <p class="alert-question">2. SQLite 和 MySQL可以相互迁移吗？</p>
            <p class="alert-answer">
                可以，支持从MySQL迁移到SQLite 或者SQLite 迁移到MySQL
            </p>
            <p class="alert-question">3. 如何配置迁移？</p>
            <p class="alert-answer">
                1. 数据库类型按需选择。<br />
                2. 填入数据库地址。SQLite原数据库地址为：Quantum.db ,
                新数据库地址则可以修改成Quantum2.db。
                新旧数据库地址不能一样。且保证db文件夹中不能有这个文件。<br />
                如需要迁移到MySQL 那么则填写MYSQL
                数据库连接如:server=192.168.2.2;port=3306;database=Quantum2;Uid=root;Pwd=123456;CharSet=utf8;
                新的数据库名称不能存在于数据库服务器中。
            </p>
            <p class="alert-question">
                迁移后请务必重启量子 docker restart quantum1
            </p>
            <p class="alert-question">
                迁移后请务必重启量子 docker restart quantum1
            </p>
            <p class="alert-question">
                迁移后请务必重启量子 docker restart quantum1
            </p>
        </n-alert>
        <n-form autocomplete="off" ref="MigrationFormData" :model="MigrationFormData" style="padding: 25px"
            label-placement="left" label-width="120" :rules="ruleInline">
            <n-form-item label="原数据库类型">
                <n-radio-group disabled v-model:value="Config.DBType">
                    <n-radio disabled value="SQLite">SQLite</n-radio>
                    <n-radio disabled value="MySQL">MySQL</n-radio>
                </n-radio-group>
            </n-form-item>
            <n-form-item label="原数据库地址">
                <n-input disabled v-model:value="Config.DBAddress" />
            </n-form-item>
            <n-form-item label="新数据库类型">
                <n-radio-group v-model:value="MigrationFormData.DBType">
                    <n-radio value="SQLite">SQLite</n-radio>
                    <n-radio value="MySQL">MySQL</n-radio>
                </n-radio-group>
            </n-form-item>
            <n-form-item label="新数据库地址" path="DBAddress">
                <n-input v-model:value="MigrationFormData.DBAddress" />
            </n-form-item>
            <n-form-item :show-feedback="false">
                <n-button type="primary" @click="handleSubmit('MigrationFormData')" style="float: right">保存</n-button>
            </n-form-item>
        </n-form>
        <n-modal preset="card" v-model:show="Warning" title="警告" style="width: 460px">
            <p style="color: var(--danger); font-size: 18px">
                请不要轻易尝试使用该功能！！！！！！
            </p>
            <p style="color: var(--danger); font-size: 18px">
                请不要轻易尝试使用该功能！！！！！！
            </p>
            <p style="color: var(--danger); font-size: 18px">
                请不要轻易尝试使用该功能！！！！！！
            </p>
            <p style="color: var(--danger); font-size: 18px">
                请不要轻易尝试使用该功能！！！！！！
            </p>
            <p style="color: var(--danger); font-size: 18px">
                请不要轻易尝试使用该功能！！！！！！
            </p>
        </n-modal>
    </div>
</template>

<script>
import { GetSetting } from "@/api/setting.js";
import { DataMigration } from "@/api/dataMigration.js";

export default {
    name: 'DatabaseMigration',
    data() {
        return {
            Warning: true,
            MigrationFormData: {},
            Config: {
                DBAddress: "",
                DBType: ""
            },
            UploadHeaders: {
                DBAddress: "",
                DBType: ""
            },
            ruleInline: {
                DBAddress: [
                    {
                        required: true,
                        message: "新的数据库地址不能为空！",
                        trigger: "blur"
                    }
                ]
            }
        };
    },
    methods: {
        getSystemConfig() {
            let v = this;
            GetSetting()
                .then(function (response) {
                    v.Config = JSON.parse(JSON.stringify(response));
                    v.MigrationFormData = JSON.parse(JSON.stringify(response));
                })
                .catch(function (error) {
                    console.log(error);
                });
        },
        handleSubmit(name) {
            let v = this;
            this.$refs[name].validate((errors) => {
                if (this.MigrationFormData.DBAddress == this.Config.DBAddress) {
                    this.$notification.error({
                        title: "配置错误",
                        content: "新的数据库地址不能和原数据库地址一样！",
                        duration: 5000
                    });
                    return false;
                }
                if (!errors) {
                    this.$dialog.warning({
                        title: "迁移确认",
                        content:
                            "确定按此配置进行数据迁移吗？配置错误将迁移失败，迁移中可能出现部分数据丢失。",
                        positiveText: "确定",
                        negativeText: "取消",
                        onPositiveClick: () => {
                            DataMigration(v.MigrationFormData)
                                .then(function (response) {
                                    v.$dialog.success({
                                        title: "迁移成功",
                                        content: response
                                    });
                                })
                                .catch(function (error) {
                                    v.$notification.error({
                                        title: "配置错误",
                                        content: String(error),
                                        duration: 5000
                                    });
                                    return false;
                                });
                        }
                    });
                }
            });
            return false;
        }
    },
    mounted() {
        this.getSystemConfig();
    }
};
</script>
