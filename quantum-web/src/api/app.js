import axios from '@/libs/api.request';

/**
 * App 会话通道封装（Web 管理端复用 App 端点，见 docs/App端API契约.md §3.1）。
 * 字段一律 PascalCase（后端 Newtonsoft 默认），响应已被 axios 拦截器解出 Data。
 * 会话相关调用统一 noLoading：轮询/补拉频繁，不能触发全局加载条。
 */

// 增量同步：全局 Seq 升序拉取 afterSeq 之后的消息（断线补拉用）
export const SyncMessages = (afterSeq = 0, limit = 100) => {
    return axios.request({
        url: '/api/App/messages',
        params: { afterSeq, limit },
        method: 'get',
        noLoading: true
    });
};

// 会话列表快照：每会话最后一条 + 总数 + 未读（未读按传入水位的算法在服务端）
export const SessionsOverview = (readSeqs = {}) => {
    return axios.request({
        url: '/api/App/sessions/overview',
        data: { ReadSeqs: readSeqs },
        method: 'post',
        noLoading: true
    });
};

// 会话内分页：某会话 Seq 倒序一页（升序返回），beforeSeq ≤ 0 = 从最新一条取
export const SessionMessages = (session = '', beforeSeq = 0, limit = 50) => {
    return axios.request({
        url: '/api/App/session/messages',
        params: { session, beforeSeq, limit },
        method: 'get',
        noLoading: true
    });
};

// 已读回执（旧全局端点：服务端按全局 Seq 推进已读状态；保留供旧链路，新客户端改用 SessionsRead）
export const MarkRead = (upToSeq) => {
    return axios.request({
        url: '/api/App/messages/read',
        params: { upToSeq },
        method: 'post',
        noLoading: true
    });
};

// 会话已读水位上报（2026-09-21 双端同步批次）：按会话推进服务端权威水位（只进 + 钳制），
// 实际推进的会话由服务端广播 session_read 帧给其他在线端。返回 { Advanced: [{ Session, Seq }] }
export const SessionsRead = (readSeqs = {}) => {
    return axios.request({
        url: '/api/App/sessions/read',
        data: { ReadSeqs: readSeqs },
        method: 'post',
        noLoading: true
    });
};

// 删除会话（session 空 = 默认会话）：只删会话实体（会话数据 + 会话窗口），消息记录（业务真实
// 产生的数据）服务端保留——消息删除入口只有日志中心；该任务再推送消息时会话自动重建。
// 返回 { Deleted, MaxSeq }，MaxSeq 为当前全局最大 Seq（消息不删后号段不复用，回拨 min 为 no-op，兼容保留）
export const DeleteSession = (session = '') => {
    return axios.request({
        url: '/api/App/session/delete',
        params: { session },
        method: 'post',
        noLoading: true
    });
};

// 未读总数（全局，按 Status 统计）
export const UnreadCount = () => {
    return axios.request({
        url: '/api/App/messages/unread-count',
        method: 'get',
        noLoading: true
    });
};

// 历史检索（关键字匹配 Content/ContentText，Seq 倒序）
export const History = (keyword, page = 1, pageSize = 20) => {
    return axios.request({
        url: '/api/App/history',
        params: { keyword, page, pageSize },
        method: 'get',
        noLoading: true
    });
};

// 提交指令/消息（文本；图片/文件时 Content = FileId）
export const SubmitCommand = (data) => {
    return axios.request({
        url: '/api/App/command',
        data,
        method: 'post',
        noLoading: true
    });
};

// 上传会话图片（.jpg/.jpeg/.png/.gif/.webp，≤10MB）→ { FileId, FileName, Size }
export const UploadImage = (file) => {
    const formData = new FormData();
    formData.append('file', file);
    return axios.request({
        url: '/api/AppUpload/image',
        data: formData,
        method: 'post'
    });
};

// 上传会话附件（导入白名单 + .pdf，≤50MB）→ { FileId, FileName, Size }
export const UploadFile = (file) => {
    const formData = new FormData();
    formData.append('file', file);
    return axios.request({
        url: '/api/AppUpload/file',
        data: formData,
        method: 'post'
    });
};

// 站内通知列表（通知卡元数据来源：标题/分类/jump 以 t_app_notification 为准，与 App 同做法）
export const NotificationList = (page = 1, pageSize = 100, unreadOnly = false) => {
    return axios.request({
        url: '/api/AppNotification/list',
        params: { page, pageSize, unreadOnly },
        method: 'get',
        noLoading: true
    });
};

// 鉴权下载地址（<img>/<video>/<audio> 直用，浏览器会带 Cookie 但不能带 JWT——
// 因此媒体一律走 blob 预取后再喂给标签，见 utils/chatMedia.js）
export const DownloadUrl = (fileId) => `/api/AppUpload/${fileId}`;
