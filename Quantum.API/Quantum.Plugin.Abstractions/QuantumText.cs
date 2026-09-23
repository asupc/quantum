namespace Quantum.Plugins;

/// <summary>
/// 消息富文本标记帮助类（2026-09-18 富文本批次）：生成 App 客户端可渲染的彩色文字/胶囊标签标记。
/// 颜色仅支持固定枚举小写 red/green/orange/blue/purple/gray；非法颜色客户端按原文字面显示，
/// 不影响消息送达。标记纯 ASCII，不受服务端 RemoveEmoji 剥离影响。
/// </summary>
public static class QuantumText
{
    /// <summary>胶囊标签：{{tag:颜色|文字}}——圆角底色+色点，适合「已监听/VIP/完成」类状态词。</summary>
    public static string Tag(string color, string text) => $"{{{{tag:{color}|{text}}}}}";

    /// <summary>彩色文字：{{颜色|文字}}——加粗着色，适合正文强调。</summary>
    public static string Color(string color, string text) => $"{{{{{color}|{text}}}}}";
}
