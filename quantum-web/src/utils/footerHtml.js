/**
 * 登录页页脚 HTML 白名单净化（对抗性审计 F1：匿名 footer 端点上的存储型 XSS）。
 *
 * 背景：Setting.Footer 由管理端自由填写（后台文案「支持html标签」），经匿名端点
 * GET /api/SystemConfig/footer 回吐给**未认证**的登录页。此前登录页直接 `v-html="Footer"`，
 * 配置一旦被污染（或未来任何导入/恢复路径带入脏值）即在登录页执行脚本——登录令牌存于
 * localStorage，等同账号接管，且前端没有任何 CSP 兜底。
 *
 * 口径（白名单**重建**：输出全部由「固定标签名 + 已校验属性值 + 转义文本」拼装，
 * 源码里的引号/尖括号布局永不进入输出——未闭合引号只能造成排版失真，造不出属性逃逸）：
 *  - 保留 b/strong/i/em/u/br/p/a/span/font；其余标签只丢标签、保留其可读文字；
 *  - 内容不是可展示文本（script/style/iframe/object/svg/math/template/noscript/form…）
 *    的元素连内容一并丢弃；
 *  - 属性按标签分类白名单：a[href]（仅字面 http/https，强制 rel="noopener noreferrer"）、
 *    span[style]（仅 color/background-color/font-size 且值须过严格正则）、font[color]；
 *    其余属性（on*、src、srcdoc、style 中白名单外的声明…）一律丢弃；
 *  - 属性值先按**原始值**过白名单（URL 必须字面以 http(s):// 开头，实体写法如
 *    `&#106;avascript:` / `javascript&colon;` 全部落空），输出时再把 `&` 转义成 `&amp;`：
 *    「靠浏览器实体解码凑出协议」的写法双端失效；
 *  - 文本段只转义尖括号：文本上下文的字符引用不会被重新解析成标签（tokenizer 已定案为文本），
 *    保留 `&` 让管理端可继续用 `&nbsp;` 之类的实体排版；
 *  - 纯字符串实现、零 DOM 依赖：node 环境可直接单测（tests/footerSanitize.spec.js）。
 */

/** 允许保留的标签（br 为 void，其余对称开闭；p 无任何属性，仅为保留段落换行排版） */
export const FOOTER_ALLOWED_TAGS = new Set(['b', 'strong', 'i', 'em', 'u', 'br', 'p', 'a', 'span', 'font']);

/**
 * 连内容一并丢弃的元素：内容不是可展示文本（raw-text/可嵌套执行环境/表单与文档结构元素）。
 * 注意不要收 void 元素（img/meta/link/embed/input 等）——它们没有内容，
 * 收进来会让「找不到闭合标签」的分支把其后整段页脚吞掉（只丢标签本身就已安全）。
 */
const FOOTER_DROP_CONTENT_TAGS = new Set([
    'script', 'style', 'iframe', 'object', 'svg', 'math', 'template', 'noscript',
    'xmp', 'plaintext', 'noembed', 'noframes', 'applet', 'form', 'select', 'textarea',
    'head', 'title', 'frameset'
]);

/** 标签起始（名字按 HTML 规则取 ASCII 字母开头；`<3`、`< script >` 这类歧义写法不会复活成标签） */
const TAG_START = /^<\s*(\/?)\s*([a-zA-Z][a-zA-Z0-9-]*)/;
/** 单属性：名字 + 可选 `=值`（双引号/单引号/裸值三态；裸值排除引号与尖括号） */
const ATTR_PATTERN = /([a-zA-Z_:][-a-zA-Z0-9_:.]*)(?:\s*=\s*("([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?/g;
/** URL 值：字面 http(s) 前缀 + 不允许空白/尖括号/引号/反斜杠（长度有界，防超长配置拖慢内联判定） */
const URL_VALUE = /^https?:\/\/[^\s<>"'`\\]{1,2048}$/i;
/** 颜色值：十六进制 / rgb() / rgba() / hsl() / 具名色（具名色只允许纯字母，括号与数字形态进不来） */
const COLOR_VALUE = /^(#[0-9a-f]{3,8}|rgb\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*\)|rgba\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*(?:0|1|0?\.\d+)\s*\)|hsl\(\s*\d{1,3}\s*,\s*\d{1,3}%\s*,\s*\d{1,3}%\s*\)|[a-z]{3,20})$/i;
/** 字号值：数字 + 单位（expression()/url() 等形态一律不过） */
const SIZE_VALUE = /^\d{1,3}(?:\.\d+)?(px|em|rem|%|pt)$/i;

/** style 声明白名单（Map 取值，避免 `constructor` 等键命中原型链） */
const STYLE_PROPS = new Map([
    ['color', COLOR_VALUE],
    ['background-color', COLOR_VALUE],
    ['font-size', SIZE_VALUE]
]);

/** 文本段转义：只处理尖括号（`<` 才是标签起始；`&`/引号在文本上下文是普通字符） */
function escapeText(text) {
    return String(text).replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** 属性值转义：`&` 必须转义（否则 `javascript&colon;` 会被浏览器解码凑出协议），引号同理 */
function escapeAttr(value) {
    return String(value).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** 跳过一个「连内容一并丢弃」元素：按同名标签深度配对，找不到闭合则丢到输入末尾（fail-closed） */
function skipElement(html, name, from) {
    const re = new RegExp(`<\\/?${name}\\b[^>]*>`, 'gi');
    re.lastIndex = from;
    let depth = 1;
    let match;
    while ((match = re.exec(html)) !== null) {
        depth += match[0][1] === '/' ? -1 : 1;
        if (depth <= 0) {
            return re.lastIndex;
        }
    }
    return html.length;
}

/** style 声明逐条过滤：白名单外的属性、未过值校验的声明整条丢弃，结果以 `prop:value` 重拼 */
function sanitizeStyleDeclarations(value) {
    const kept = [];
    for (const declaration of String(value).split(';')) {
        const colon = declaration.indexOf(':');
        if (colon <= 0) {
            continue;
        }
        const prop = declaration.slice(0, colon).trim().toLowerCase();
        const rawValue = declaration.slice(colon + 1).trim();
        const checker = STYLE_PROPS.get(prop);
        if (!checker || !checker.test(rawValue)) {
            continue;
        }
        kept.push(`${prop}:${rawValue}`);
    }
    return kept.join(';');
}

/** 单个属性是否属于该标签的白名单，并按对应校验器验值（不合格返回空串 = 丢弃） */
function normalizeAttribute(tagName, attrName, value) {
    if (tagName === 'a' && attrName === 'href') {
        return URL_VALUE.test(value) ? value : '';
    }
    if (tagName === 'span' && attrName === 'style') {
        return sanitizeStyleDeclarations(value);
    }
    if (tagName === 'font' && attrName === 'color') {
        return COLOR_VALUE.test(value) ? value : '';
    }
    return '';
}

/** 解析开标签的属性文本，返回按白名单归一化后的属性（保序 Map） */
function parseAttributes(tagName, attrText) {
    const attrs = new Map();
    // 先去掉自闭合斜杠（<br/>），避免它被当成属性名的一部分
    const text = String(attrText).replace(/\/\s*$/, '');
    ATTR_PATTERN.lastIndex = 0;
    let match;
    while ((match = ATTR_PATTERN.exec(text)) !== null) {
        const attrName = match[1].toLowerCase();
        // 三态值取其一；无值属性（autofocus、裸写的 onfocus）直接丢弃
        const value = match[3] !== undefined ? match[3] : match[4] !== undefined ? match[4] : match[5];
        if (value === undefined) {
            continue;
        }
        const normalized = normalizeAttribute(tagName, attrName, value);
        if (normalized) {
            attrs.set(attrName === 'style' ? 'style' : attrName, normalized);
        }
    }
    return attrs;
}

/** 用固定标签名 + 已校验属性重拼开标签（a 带 href 时强制 rel="noopener noreferrer"） */
function buildOpenTag(tagName, attrText) {
    if (tagName === 'br') {
        return '<br>';
    }
    const attrs = parseAttributes(tagName, attrText);
    let html = `<${tagName}`;
    const href = attrs.get('href');
    if (href) {
        html += ` href="${escapeAttr(href)}" rel="noopener noreferrer"`;
    }
    const style = attrs.get('style');
    if (style) {
        html += ` style="${escapeAttr(style)}"`;
    }
    const color = attrs.get('color');
    if (color) {
        html += ` color="${escapeAttr(color)}"`;
    }
    return `${html}>`;
}

/**
 * 页脚 HTML 净化入口
 * @param {unknown} raw 服务端 GetFooter 返回值（脏类型一律收敛为空串，不显示 [object Object]）
 * @returns {string} 可安全交给 v-html 的 HTML 片段
 */
export function sanitizeFooterHtml(raw) {
    if (typeof raw !== 'string' || raw === '') {
        return '';
    }
    const html = raw;
    const len = html.length;
    let out = '';
    let i = 0;
    while (i < len) {
        const lt = html.indexOf('<', i);
        if (lt < 0) {
            out += escapeText(html.slice(i));
            break;
        }
        if (lt > i) {
            out += escapeText(html.slice(i, lt));
        }
        i = lt;
        // 注释 / 声明 / 处理指令整体丢弃（条件注释是历史 XSS 载体）
        if (html.startsWith('<!--', i)) {
            const end = html.indexOf('-->', i + 4);
            i = end < 0 ? len : end + 3;
            continue;
        }
        if (html.startsWith('<!', i) || html.startsWith('<?', i)) {
            const end = html.indexOf('>', i + 2);
            i = end < 0 ? len : end + 1;
            continue;
        }
        const tagMatch = TAG_START.exec(html.slice(i));
        if (!tagMatch) {
            // 不是标签起始（如 "<3"）：按文本输出并前进一字符，不把后续内容当标签吞掉
            out += '&lt;';
            i += 1;
            continue;
        }
        const closing = tagMatch[1] === '/';
        const name = tagMatch[2].toLowerCase();
        const gt = html.indexOf('>', i);
        if (gt < 0) {
            // 未闭合的 `<...`（截断配置）：整段按文本输出
            out += escapeText(html.slice(i));
            break;
        }
        const attrText = html.slice(i + tagMatch[0].length, gt);
        if (FOOTER_DROP_CONTENT_TAGS.has(name)) {
            i = closing ? gt + 1 : skipElement(html, name, gt + 1);
            continue;
        }
        if (closing) {
            if (FOOTER_ALLOWED_TAGS.has(name) && name !== 'br') {
                out += `</${name}>`;
            }
            i = gt + 1;
            continue;
        }
        if (!FOOTER_ALLOWED_TAGS.has(name)) {
            // 白名单外标签只丢标签本身、保留可读文字（div/h1 等排版标签的文本不丢）
            i = gt + 1;
            continue;
        }
        out += buildOpenTag(name, attrText);
        i = gt + 1;
    }
    return out;
}
