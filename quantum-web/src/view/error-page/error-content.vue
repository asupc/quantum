<template>
    <div class="error-page">
        <div class="content-con">
            <div class="code-badge glass-card">
                <div class="code mono">{{ code }}</div>
                <div class="desc">{{ desc }}</div>
                <div class="icon"><i class="fa-solid fa-triangle-exclamation"></i></div>
            </div>
            <back-btn-group class="back-btn-group"></back-btn-group>
        </div>
    </div>
</template>

<script>
import backBtnGroup from './back-btn-group.vue'

export default {
    name: 'error_content',
    components: {
        backBtnGroup
    },
    props: {
        code: String,
        desc: String
    }
}
</script>

<style scoped>
.error-page {
    height: 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--bg);
    overflow: auto;
}

.content-con {
    text-align: center;
}

.code-badge {
    display: inline-block;
    padding: 40px 64px;
}

.code {
    font-size: 56px;
    font-weight: 700;
    color: var(--accent);
    text-shadow: 0 0 24px rgba(34, 211, 238, .45);
}

.desc {
    margin-top: 10px;
    color: var(--text-2);
    font-size: 14px;
}

.icon {
    margin-top: 18px;
    color: var(--warn);
    font-size: 26px;
}

.back-btn-group {
    margin-top: 24px;
}
</style>
