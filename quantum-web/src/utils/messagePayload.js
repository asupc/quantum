/**
 * 消息结构化载荷解析（对齐 App core/common/MessagePayload.kt 的 MessagePayloadParser）。
 * 契约：{"taskId":"来源任务Id","poster":"封面地址","options":[{"key","label","reply","color","desc"}]}（camelCase）。
 * taskId 为服务端注入的产生该消息的真实任务 Id（点选选项代发时透传做精确路由，见 docs/会话分组归并方案.md §2.4）。
 * 载荷损坏/为空一律返回 null，调用方降级为普通消息渲染——不允许抛异常。
 */
import { RICH_COLORS } from '@/utils/richText';

const MAX_OPTIONS = 20;

/**
 * @param {string} raw Payload 原始 JSON 字符串
 * @returns {{taskId:string, poster:string, options:Array<{key:string,label:string,reply:string,color:string,desc:string}>}|null}
 *          taskId 与 poster/options 一样按需存在：仅 taskId（无海报无选项）也返回非 null，供点选取值
 */
export function parsePayload(raw) {
    if (!raw || typeof raw !== 'string') {
        return null;
    }
    let data;
    try {
        data = JSON.parse(raw);
    } catch (e) {
        return null;
    }
    if (!data || typeof data !== 'object' || Array.isArray(data)) {
        return null;
    }
    const taskId = typeof data.taskId === 'string' && data.taskId.trim() ? data.taskId.trim() : '';
    const poster = typeof data.poster === 'string' ? data.poster.trim() : '';
    const options = [];
    if (Array.isArray(data.options)) {
        for (const item of data.options.slice(0, MAX_OPTIONS)) {
            if (!item || typeof item !== 'object') {
                continue;
            }
            const label = typeof item.label === 'string' ? item.label : '';
            if (!label) {
                continue;
            }
            const key = typeof item.key === 'string' && item.key ? item.key : label;
            options.push({
                key,
                label,
                // 服务端已把缺省 reply 归一为 key，这里再兜一层
                reply: typeof item.reply === 'string' && item.reply ? item.reply : key,
                color: typeof item.color === 'string' && RICH_COLORS[item.color] ? item.color : '',
                desc: typeof item.desc === 'string' ? item.desc : ''
            });
        }
    }
    if (!taskId && !poster && !options.length) {
        return null;
    }
    return { taskId, poster, options };
}

/** 选项色值：缺省/未知用次要文字色（与 App optionColor 一致） */
export function optionColor(color) {
    return RICH_COLORS[color] || '#94A3B8';
}
