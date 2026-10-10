using System.Text;
using System.Text.Json.Nodes;

namespace Quantum.Application.Channels;

/// <summary>
/// 仓库受限富文本（<c>{{red|文字}}</c> / <c>{{tag:颜色|文字}}</c> / <c>{{link:文字|https://…}}</c>）
/// → 飞书卡片 <c>lark_md</c> 的**零降级**映射。
///
/// 依据官方「卡片 JSON 2.0 结构」文档：6 个强调色与官方枚举逐一精确对应；<c>grey</c> 在官方
/// column_set 示例中实际用于 &lt;font color&gt;；<c>&lt;text_tag&gt;</c> 是飞书原生胶囊标签。
/// 因此除灰色标签（text_tag 枚举无 grey，用 neutral）外不存在任何有意的语义偏差。
///
/// 解析契约由 <c>Quantum.Plugins.QuantumText</c> 等三端共享，**本类只做渲染侧转换，不改变校验口径**。
/// </summary>
public static class FeishuRichText
{
    /// <summary>正文强调色 → lark_md 颜色，全部精确对应官方枚举。</summary>
    private static readonly Dictionary<string, string> Emphasis = new(StringComparer.Ordinal)
    {
        ["red"] = "red", ["green"] = "green", ["orange"] = "orange",
        ["blue"] = "blue", ["purple"] = "purple", ["gray"] = "grey"
    };

    /// <summary>胶囊标签颜色 → text_tag 颜色。灰色标签在 text_tag 枚举中无 grey，退到 neutral（唯一一处近似）。</summary>
    private static readonly Dictionary<string, string> TagColors = new(StringComparer.Ordinal)
    {
        ["red"] = "red", ["green"] = "green", ["orange"] = "orange",
        ["blue"] = "blue", ["purple"] = "purple", ["gray"] = "neutral"
    };

    /// <summary>卡片内单条消息最多渲染多少个标记，防止正文膨胀顶破 30KB 卡片体积上限。</summary>
    public const int MaxMarks = 50;

    public static string ToLarkMd(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var body = new StringBuilder(text.Length + 64);
        var index = 0;
        var marks = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                // 标记之间的普通文本同样必须转义：只转义标记正文时，用户文本可自己拼出
                // [文字](地址) 链接或 <font>/<at> 构造（实测 {{unknown|…}} 之外的裸文本原样下发）
                body.Append(EscapeText(text[index..]));
                break;
            }
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                body.Append(EscapeText(text[index..]));
                break;
            }
            body.Append(EscapeText(text[index..open]));
            var mark = text[(open + 2)..close];
            if (marks < MaxMarks && TryRender(mark, out var rendered))
            {
                body.Append(rendered);
                marks++;
            }
            else
            {
                // 未识别/超限标记按字面输出，但**必须转义**——否则用户内容里的
                // <script> 之类会原样进入卡片正文被当作 HTML 解析
                body.Append("{{").Append(EscapeText(mark)).Append("}}");
            }
            index = close + 2;
        }
        return body.ToString();
    }

    private static bool TryRender(string mark, out string rendered)
    {
        rendered = null;
        var pipe = mark.IndexOf('|', StringComparison.Ordinal);
        if (pipe <= 0) return false;
        var label = mark[..pipe].Trim();
        var body = mark[(pipe + 1)..].Trim();

        if (label.StartsWith("tag:", StringComparison.Ordinal))
        {
            var key = label[4..].Trim().ToLowerInvariant();
            if (body.Length == 0 || !TagColors.TryGetValue(key, out var color)) return false;
            if (body.Contains('<') || body.Contains('>')) return false;
            rendered = $"<text_tag color='{color}'>{EscapeText(body)}</text_tag>";
            return true;
        }

        // 语法为 {{link:文字|https://…}}：文字在 label 的 "link:" 之后，地址在竖线之后
        if (label.StartsWith("link:", StringComparison.Ordinal))
        {
            var text = label[5..].Trim();
            if (text.Length == 0 || body.Length == 0 || !IsSafeHttps(body)) return false;
            if (text.Contains('<') || text.Contains('>')) return false;
            rendered = $"[{EscapeText(text)}]({body})";
            return true;
        }

        if (body.Length == 0 || !Emphasis.TryGetValue(label, out var emphasis)) return false;
        if (body.Contains('<') || body.Contains('>')) return false;
        rendered = $"<font color='{emphasis}'>{EscapeText(body)}</font>";
        return true;
    }

    /// <summary>
    /// 用户文本 → lark_md 安全文本：HTML 敏感字符实体化（防被当标签解析），markdown 元字符同样实体化——
    /// 否则用户文本能自己拼出链接/强调语法，例如未识别标记走字面回退时的
    /// <c>{{unknown|[点我](https://evil)}}</c>（绕过 {{link:…}} 的 https 白名单），
    /// 或标记文字里夹带 <c>](</c> 提前闭合链接。渲染语法本身（标签尖括号、链接圆括号）由调用处拼接，不经本方法。
    /// </summary>
    private static string EscapeText(string value)
    {
        var text = value ?? "";
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '\'': builder.Append("&#39;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                // markdown 元字符：实体化后仍渲染为字面字符，但不参与链接/强调语法解析
                case '[': builder.Append("&#91;"); break;
                case ']': builder.Append("&#93;"); break;
                case '(': builder.Append("&#40;"); break;
                case ')': builder.Append("&#41;"); break;
                case '*': builder.Append("&#42;"); break;
                case '_': builder.Append("&#95;"); break;
                case '~': builder.Append("&#126;"); break;
                case '`': builder.Append("&#96;"); break;
                default: builder.Append(ch); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 飞书链接必须带 scheme 且为 http/https——与仓库既有「非绝对 https 即拒绝」口径一致。
    /// 额外拒绝 markdown 敏感字符：链接地址被拼进 <c>[文字](地址)</c>，含 <c>)</c>/空格/尖括号/引号
    /// 即可提前闭合或改写链接语法（实测 <c>https://a.b/&lt;script&gt;</c> 这类地址会被原样注入目标位置）。
    /// 合法地址如需这些字符应自行百分号编码。
    /// </summary>
    public static bool IsSafeHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.Host.Contains('.'))
        {
            return false;
        }
        foreach (var ch in url)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '(' or ')' or '[' or ']' or '<' or '>' or '"' or '\'' or '`' or '\\')
            {
                return false;
            }
        }
        // 实体形态的括号同样拒绝：地址是原样拼进 [文字](地址) 的，若飞书渲染前做了 HTML 实体解码，
        // https://a.b/&#41;&#91;x&#93;&#40;https://evil 就能在目标位置再拼出一个链接。
        // 合法地址遇这些字符应百分号编码（%29/%5B…），故只拒实体前缀、放行查询串里的裸 &。
        return url.IndexOf("&#", StringComparison.Ordinal) < 0
            && !url.Contains("&amp;", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("&lt;", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("&gt;", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("&quot;", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构造发送用卡片。<paramref name="body"/> 为已渲染的 lark_md；<paramref name="headerTemplate"/>
    /// 按通知语义选色（失败红 / 成功绿 / 信息蓝 / 警告橙）。
    /// </summary>
    public static JsonObject BuildCard(string title, string body, string headerTemplate = "blue",
        string subtitle = null, IEnumerable<JsonObject> actions = null)
    {
        var elements = new JsonArray();
        if (!string.IsNullOrEmpty(body))
            elements.Add(new JsonObject
            {
                ["tag"] = "markdown",
                ["content"] = body,
                ["text_align"] = "left",
                ["text_size"] = "normal"
            });
        if (actions is not null)
        {
            var list = new JsonArray();
            foreach (var action in actions) list.Add(action);
            if (list.Count > 0) elements.Add(new JsonObject { ["tag"] = "action", ["actions"] = list });
        }

        var header = new JsonObject
        {
            ["title"] = new JsonObject { ["tag"] = "plain_text", ["content"] = title ?? "通知" },
            ["template"] = headerTemplate
        };
        if (!string.IsNullOrEmpty(subtitle))
            header["subtitle"] = new JsonObject { ["tag"] = "plain_text", ["content"] = subtitle };

        return new JsonObject
        {
            ["schema"] = "2.0",
            ["config"] = new JsonObject { ["update_multi"] = true, ["streaming_mode"] = false },
            ["header"] = header,
            ["body"] = new JsonObject { ["elements"] = elements }
        };
    }

    /// <summary>发送消息：<c>content</c> 必须是 JSON 序列化后的**字符串**，不是对象。</summary>
    public static string SerializeContent(JsonObject card) => card.ToJsonString();
}
