import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import router from './router';
import config from '@/config';
import installPlugin from '@/plugin';
import { message, dialog, notification } from '@/libs/naive-discrete';
import './styles/tokens.css';
import '@/assets/fontawesome-7.1/css/all.min.css';

const app = createApp(App);

app.use(createPinia());
app.use(router);
app.use(installPlugin);

app.config.globalProperties.$config = config;
// 全局反馈 API（discrete，与 App.vue provider 同主题）：页面内 this.$message / this.$dialog / this.$notification
app.config.globalProperties.$message = message;
app.config.globalProperties.$dialog = dialog;
app.config.globalProperties.$notification = notification;

app.mount('#app');
