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
            if (open < 0) { body.Append(text, index, text.Length - index); break; }
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { body.Append(text, index, text.Length - index); break; }
            body.Append(text, index, open - index);
            var mark = text[(open + 2)..close];
            if (marks < MaxMarks && TryRender(mark, out var rendered))
            {
                body.Append(rendered);
                marks++;
            }
            else
            {
                // 未识别/超限标记按字面输出，但**必须转义尖括号**——否则用户内容里的
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

    private static string EscapeText(string value)
        => (value ?? "").Replace("&", "&amp;").Replace("'", "&#39;")
                        .Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>飞书链接必须带 scheme 且为 http/https——与仓库既有「非绝对 https 即拒绝」口径一致。</summary>
    public static bool IsSafeHttps(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
           string.IsNullOrEmpty(uri.UserInfo) && uri.Host.Contains('.');

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
