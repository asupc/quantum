/**
 * 消息富文本标记解析（逐条对齐 App 端 core/common/RichText.kt 的 RichTextParser）。
 *
 * 语法：`{{颜色|文字}}` 彩色粗体文字、`{{tag:颜色|文字}}` 胶囊标签、
 * `{{link:文字|URL}}` 命名链接（只显示文字，点击跳 URL，URL 非 http(s) 开头按字面）；颜色固定六色小写枚举。
 * 规则（两端一致，单测见 tests/richText.spec.js）：
 *  - 未知颜色 / 残缺未闭合 / 跨行 / 空文本 一律按原文字面输出（脚本笔误直接可见，不静默吞掉）；
 *  - 文本段禁花括号与换行 → 天然拒绝嵌套（外层非法时内层合法标记仍会被识别）；
 *  - 单条消息标记上限 MAX_MARKS（彩色/胶囊/链接合计），超出部分按字面渲染，防超长消息渲染开销失控。
 */
export const MAX_MARKS = 50;

/** 颜色枚举 → 实际色值（与 App RichMessageText 的映射同值） */
export const RICH_COLORS = {
    red: '#F43F5E',
    green: '#10B981',
    orange: '#F59E0B',
    blue: '#0EA5E9',
    purple: '#6366F1',
    gray: '#94A3B8'
};

export const SPAN_PLAIN = 'plain';
export const SPAN_STYLED = 'styled';
export const SPAN_LINK = 'link';

// 文本段禁花括号与换行：拒绝嵌套与跨行；颜色段为小写字母，合法性由颜色表把关；
// 交替第二分支为命名链接，两段均禁花括号/竖线/换行（与 App tokenRegex 逐字对齐）
const TOKEN_REGEX = /\{\{(tag:)?([a-z]+)\|([^{}\n\r]+)\}\}|\{\{link:([^{}|\n\r]+)\|([^{}|\n\r]+)\}\}/g;
// 含合法标记的快速判定（旧消息免解析直接走纯文本渲染）
const ANY_MARK_REGEX = /\{\{(tag:)?(red|green|orange|blue|purple|gray)\|/;
const LINK_PREFIX = '{{link:';

/** 是否含合法标记 */
export function hasMarkup(raw) {
    const text = raw || '';
    return text.includes('{{') && (ANY_MARK_REGEX.test(text) || text.includes(LINK_PREFIX));
}

/**
 * 解析为片段序列
 * @param {string} raw 消息原文
 * @returns {Array<{type:'plain'|'styled'|'link', text:string, color?:string, pill?:boolean, url?:string}>}
 */
export function parseRich(raw) {
    const text = raw == null ? '' : String(raw);
    if (!hasMarkup(text)) {
        return [{ type: SPAN_PLAIN, text }];
    }
    const spans = [];
    let last = 0;
    let marks = 0;
    TOKEN_REGEX.lastIndex = 0;
    let match;
    while ((match = TOKEN_REGEX.exec(text)) !== null) {
        // 颜色不在枚举 / 超上限 / 链接 URL 非 http(s) 开头：本次命中按字面落入普通文本（不推进 last）
        if (match[4] !== undefined || match[5] !== undefined) {
            if (!/^https?:/i.test(match[5]) || marks >= MAX_MARKS) {
                continue;
            }
            if (match.index > last) {
                spans.push({ type: SPAN_PLAIN, text: text.slice(last, match.index) });
            }
            spans.push({ type: SPAN_LINK, text: match[4], url: match[5] });
            marks += 1;
            last = match.index + match[0].length;
            continue;
        }
        const isPill = !!match[1];
        const color = match[2];
        const body = match[3];
        if (!Object.prototype.hasOwnProperty.call(RICH_COLORS, color) || marks >= MAX_MARKS) {
            continue;
        }
        if (match.index > last) {
            spans.push({ type: SPAN_PLAIN, text: text.slice(last, match.index) });
        }
        spans.push({ type: SPAN_STYLED, text: body, color, pill: isPill });
        marks += 1;
        last = match.index + match[0].length;
    }
    if (last < text.length) {
        spans.push({ type: SPAN_PLAIN, text: text.slice(last) });
    }
    return spans.length ? spans : [{ type: SPAN_PLAIN, text }];
}

/** 长文折叠阈值（换行数，与 App FOLD_LINES 一致） */
export const FOLD_LINES = 10;

/** 是否需要折叠（按换行符计数，与 App 判定一致） */
export function needFold(raw) {
    const text = raw || '';
    let lines = 0;
    for (let i = 0; i < text.length; i++) {
        if (text[i] === '\n') {
            lines += 1;
        }
    }
    return lines > FOLD_LINES;
}
