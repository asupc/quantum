import axios from 'axios';
import Router from '../router/index.js';
import { message, loadingBar } from '@/libs/naive-discrete';

// §3-7：401 自动登出时连带重置会话实时态（清 ready/socket 等），否则重登录后 WS 双死锁收不到实时帧。
// 动态 import 打破 axios→api→store→api 静态循环，且仅在运行时 401 触发（pinia 此时已就绪）。
function bustChatSession() {
    import('@/stores/chat')
        .then(({ useChatStore }) => useChatStore().disconnect())
        .catch(() => { /* 会话 store 尚未初始化时忽略 */ });
}

class HttpRequest {
    constructor(baseUrl = '') {
        this.baseUrl = baseUrl;
        this.queue = [];
    }
    getInsideConfig() {
        const config = {
            baseURL: this.baseUrl,
            headers: {
                //
            }
        };
        return config;
    }
    destroy(url, noLoading = false) {
        if (!noLoading) {
            this.queue.pop(); // 弹出遵循先进先出原则
            setTimeout(() => {
                if (!this.queue.length) {
                    loadingBar.finish();
                }
            }, 300);
        }
    }
    interceptors(instance, url) {
        instance.interceptors.request.use(
            (config) => {
                // 检查是否需要显示 loading
                const noLoading = config.noLoading || false;

                if (!noLoading) {
                    this.queue.push(url); // 把url添加到队列
                    setTimeout(() => {
                        if (this.queue.length) {
                            loadingBar.start();
                        }
                    }, 500);
                }
                config.headers.Authorization =
                    localStorage.getItem('accessToken');
                return config;
            },
            (error) => {
                return Promise.reject(error);
            }
        );
        // 响应拦截
        instance.interceptors.response.use(
            (response) => {
                const noLoading = response.config.noLoading || false;
                this.destroy(url, noLoading);
                // 文件流下载（responseType: 'blob'）不经过 Code 包装判断，直接放行完整 response
                if (response.config.responseType === 'blob') {
                    return response;
                }
                if (response.status == 200) {
                    if (response.data.Code == 200) {
                        return response.data.Data;
                    } else if (response.data.Code == 401) {
                        localStorage.removeItem('accessToken');
                        bustChatSession();
                        Router.push({ name: 'login' });
                        return Promise.reject(response.data.Message || '未授权');
                    } else {
                        message.error(response.data.Message);
                        return Promise.reject(response.data.Message);
                    }
                } else {
                    return response;
                }
            },
            (error) => {
                const noLoading =
                    (error.config && error.config.noLoading) || false;
                this.destroy(url, noLoading);
                if (error.message == 'Request failed with status code 500') {
                    message.error('请求服务失败，服务端异常！');
                } else if (error.response && error.response.status == 401) {
                    localStorage.removeItem('accessToken');
                    bustChatSession();
                    Router.push({ name: 'login' });
                } else {
                    message.error(error.message || '网络请求失败');
                }
                return Promise.reject(error);
            }
        );
    }
    request(options) {
        const instance = axios.create();
        options = Object.assign(this.getInsideConfig(), options);
        this.interceptors(instance, options.url);
        return instance(options);
    }
}
export default HttpRequest;
