/**
 * 会话展示格式化（对齐 App 端 ChatScreen / ConversationListScreen 的规则）。
 * 服务端时间串为 `yyyy-MM-dd HH:mm:ss`，解析失败时一律不显示（与 App 同）。
 */
import dayjs from 'dayjs';
import { parseRich } from '@/utils/richText';

/** 时间分隔插入阈值（分钟，与 App TIME_LABEL_GAP_MINUTES 一致） */
export const TIME_LABEL_GAP_MINUTES = 5;

const SERVER_TIME_FORMAT = 'YYYY-MM-DD HH:mm:ss';

export function parseServerTime(value) {
    if (!value) {
        return null;
    }
    const parsed = dayjs(value, SERVER_TIME_FORMAT);
    if (parsed.isValid()) {
        return parsed;
    }
    // 兼容 ISO/带 T 的时间串（不同来源历史数据）
    const fallback = dayjs(value);
    return fallback.isValid() ? fallback : null;
}

/**
 * 时间分隔文案：当天 HH:mm / 昨天 HH:mm / 同年 MM-DD HH:mm / 跨年 YYYY-MM-DD HH:mm
 * @returns {string} 解析失败返回空串（调用方不渲染分隔行）
 */
export function formatTimeLabel(value, now = dayjs()) {
    const time = parseServerTime(value);
    if (!time) {
        return '';
    }
    const hhmm = time.format('HH:mm');
    if (time.isSame(now, 'day')) {
        return hhmm;
    }
    if (time.isSame(now.subtract(1, 'day'), 'day')) {
        return `昨天 ${hhmm}`;
    }
    if (time.isSame(now, 'year')) {
        return time.format('MM-DD HH:mm');
    }
    return time.format('YYYY-MM-DD HH:mm');
}

/** 悬浮提示用的完整时间 */
export function formatFullTime(value) {
    const time = parseServerTime(value);
    return time ? time.format(SERVER_TIME_FORMAT) : '';
}

/** 相邻消息是否该插时间分隔：首条必插；间隔 > 5 分钟插；无法解析时间则不插 */
export function shouldInsertTimeSeparator(prevTime, currentTime) {
    const current = parseServerTime(currentTime);
    if (!current) {
        return false;
    }
    const prev = parseServerTime(prevTime);
    if (!prev) {
        return true;
    }
    return current.diff(prev, 'minute') > TIME_LABEL_GAP_MINUTES;
}

/** 富文本标记剥离为纯文本（保留标记内的文字，只去掉语法，列表预览/搜索摘要用） */
export function stripMarkup(raw) {
    return parseRich(raw)
        .map((span) => span.text)
        .join('')
        .trim();
}

/**
 * 会话列表预览文案，规则与 App 一致：
 * notify → 取首行的「【标题】」标题（无括号则整行）；image→[图片]、video→[视频]、audio→[音频]、file→[文件]；
 * 其余取正文首行。Web 增强：正文先剥离富文本标记（App 直接显示含 {{...}} 的原文）。
 */
export function sessionPreview(message) {
    if (!message) {
        return '';
    }
    const type = message.ContentType || 'text';
    if (type === 'image') {
        return '[图片]';
    }
    if (type === 'video') {
        return '[视频]';
    }
    if (type === 'audio') {
        return '[音频]';
    }
    if (type === 'file') {
        return '[文件]';
    }
    const firstLine = (message.Content || '').split('\n')[0] || '';
    if (type === 'notify') {
        // 与 App 同：剥掉前导「【」再取「】」前的内容，取不到则回退首行
        const title = firstLine.replace(/^【/, '').split('】')[0].trim();
        return title || firstLine.trim();
    }
    return stripMarkup(firstLine);
}

/** 时长格式化 m:ss / h:mm:ss；无时长显示 --:--（与 App 播放器一致） */
export function formatDuration(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) {
        return '--:--';
    }
    const total = Math.floor(seconds);
    const s = total % 60;
    const m = Math.floor(total / 60) % 60;
    const h = Math.floor(total / 3600);
    const pad = (n) => String(n).padStart(2, '0');
    return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

/** 文件大小（上传结果/附件气泡展示） */
export function formatBytes(size) {
    if (!Number.isFinite(size) || size <= 0) {
        return '';
    }
    if (size < 1024) {
        return `${size} B`;
    }
    if (size < 1024 * 1024) {
        return `${(size / 1024).toFixed(1)} KB`;
    }
    return `${(size / 1024 / 1024).toFixed(1)} MB`;
}

/**
 * 会话键 → 显示标题（默认会话 / 任务名 / 回退会话键原文）。
 * 会话键可能是任务 Id（命中 taskMap 显示任务名）或任务配置的会话名（本身就是可读文本），
 * 映射不到任务时直接显示会话键原文（原「任务 前8位」回退仅对任务 Id 有意义）。
 */
export function sessionTitle(sessionKey, taskMap = {}) {
    if (!sessionKey) {
        return '默认会话';
    }
    return taskMap[sessionKey] || sessionKey;
}
