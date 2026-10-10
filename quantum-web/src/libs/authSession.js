import Router from '../router/index.js';

/**
 * 登录态作废（REST 401 与 WS 鉴权失败共用同一份实现，避免两处各自抄一遍而走偏）。
 *
 * §3-7：401 自动登出时连带重置会话实时态（清 ready/socket 等），否则重登录后 WS 双死锁收不到实时帧。
 * 动态 import 打破 axios→api→store→api 静态循环，且仅在运行时触发（pinia 此时已就绪）；
 * 本文件因此不引入 naive-discrete 等有副作用模块，WS 客户端可安全静态引用。
 */
function bustChatSession() {
    import('@/stores/chat')
        .then(({ useChatStore }) => useChatStore().disconnect())
        .catch(() => { /* 会话 store 尚未初始化时忽略 */ });
}

/** 清令牌 + 断会话长连接 + 回登录页（信封 401 与 WS 鉴权被拒的唯一登出出口） */
export function clearLoginSession() {
    localStorage.removeItem('accessToken');
    bustChatSession();
    Router.push({ name: 'login' });
}
