<template>
    <div class="back-btn-group">
        <n-button quaternary type="primary" size="large" @click="backHome">返回首页</n-button>
        <n-button quaternary size="large" @click="backPrev">返回上一页({{ second }}s)</n-button>
    </div>
</template>

<script>
export default {
    name: 'backBtnGroup',
    data() {
        return {
            second: 5,
            timer: null
        }
    },
    methods: {
        backHome() {
            this.$router.replace({
                name: this.$config.homeName
            })
        },
        backPrev() {
            this.$router.go(-1)
        }
    },
    mounted() {
        this.timer = setInterval(() => {
            if (this.second === 0) this.backPrev()
            else this.second--
        }, 1000)
    },
    beforeUnmount() {
        clearInterval(this.timer)
    }
}
</script>

<style scoped>
.back-btn-group {
    display: flex;
    justify-content: center;
    gap: 8px;
}
</style>
