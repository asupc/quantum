using System.Text;
using System.Text.Json;
using Quantum.Application.Channels;

namespace Quantum.API.Tests;

/// <summary>
/// 飞书长连接帧编解码回归。
///
/// 帧解析是本通道最容易出现**静默故障**的一环——解错了不抛异常、只是收不到事件。
/// 这组用例把编解码钉死：往返一致、字段定位正确、畸形输入被安全拒绝。
/// </summary>
public sealed class FeishuFrameCodecTests
{
    private static FeishuFrame SampleEvent() => new()
    {
        SeqId = 7,
        Method = FeishuFrame.MethodData,
        Service = 3,
        Headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "event",
            ["message_id"] = "om_abc",
            ["trace_id"] = "tr_1"
        },
        PayloadType = "event",
        Payload = Encoding.UTF8.GetBytes("""{"header":{"event_type":"im.message.receive_v1"},"event":{}}""")
    };

    [Fact]
    public void FrameCodec_RoundTrips_AllFields()
    {
        var original = SampleEvent();

        var decoded = FeishuFrameCodec.Decode(FeishuFrameCodec.Encode(original));

        Assert.Equal(original.SeqId, decoded.SeqId);
        Assert.Equal(original.Method, decoded.Method);
        Assert.Equal(original.Service, decoded.Service);
        Assert.Equal(original.PayloadType, decoded.PayloadType);
        Assert.Equal("event", decoded.Header("type"));
        Assert.Equal("om_abc", decoded.Header("message_id"));
        Assert.Equal("tr_1", decoded.Header("trace_id"));
        Assert.Equal(original.PayloadText, decoded.PayloadText);
    }

    [Fact]
    public void FrameCodec_MethodField_DistinguishesControlFromData()
    {
        var control = new FeishuFrame
        {
            SeqId = 1, Method = FeishuFrame.MethodControl,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "ping" },
            PayloadType = "ping"
        };
        var data = SampleEvent();

        Assert.Equal(FeishuFrame.MethodControl, FeishuFrameCodec.Decode(FeishuFrameCodec.Encode(control)).Method);
        Assert.Equal(FeishuFrame.MethodData, FeishuFrameCodec.Decode(FeishuFrameCodec.Encode(data)).Method);
    }

    [Fact]
    public void FrameCodec_HandlesLargeSeqIdAcrossVarintBoundaries()
    {
        foreach (var seq in new long[] { 0, 1, 127, 128, 16383, 16384, 2147483647, 4294967296L })
        {
            var frame = new FeishuFrame { SeqId = seq, Method = FeishuFrame.MethodData, Headers = [], PayloadType = "x" };
            Assert.Equal(seq, FeishuFrameCodec.Decode(FeishuFrameCodec.Encode(frame)).SeqId);
        }
    }

    [Fact]
    public void FrameCodec_HandlesUnicodeAndEmptyPayload()
    {
        var frame = new FeishuFrame
        {
            Method = FeishuFrame.MethodData,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "event" },
            Payload = Encoding.UTF8.GetBytes("任务「什么值得买」失败：温度过高\n第二行")
        };

        var decoded = FeishuFrameCodec.Decode(FeishuFrameCodec.Encode(frame));

        Assert.Equal("任务「什么值得买」失败：温度过高\n第二行", decoded.PayloadText);
        Assert.Contains("\n", decoded.PayloadText);
    }

    [Fact]
    public void FrameCodec_EmptyPayload_DecodesAsEmptyText()
    {
        var frame = new FeishuFrame
        {
            Method = FeishuFrame.MethodData,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "pong" }
        };

        Assert.Equal("", FeishuFrameCodec.Decode(FeishuFrameCodec.Encode(frame)).PayloadText);
    }

    [Fact]
    public void FrameCodec_UnknownFields_AreSkippedRatherThanFailing()
    {
        // 平台将来加字段不应导致整条连接失败
        var frame = new FeishuFrame
        {
            SeqId = 9, Method = FeishuFrame.MethodData,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "event" },
            Payload = Encoding.UTF8.GetBytes("{}")
        };
        var bytes = FeishuFrameCodec.Encode(frame);
        // 追加一个未知字段（field 99, varint 1）
        bytes = bytes.Concat(new byte[] { 0x98, 0x06, 0x01 }).ToArray();

        var decoded = FeishuFrameCodec.Decode(bytes);

        Assert.Equal(9, decoded.SeqId);
        Assert.Equal("event", decoded.Header("type"));
    }

    [Fact]
    public void FrameCodec_RejectsEmptyAndTruncatedInput()
    {
        Assert.Throws<ChannelProtocolException>(() => FeishuFrameCodec.Decode(Array.Empty<byte>()));
        Assert.Throws<ChannelProtocolException>(() => FeishuFrameCodec.Decode(null));
        // 声明长度超过实际剩余字节，必须在分配前拒绝
        Assert.Throws<ChannelProtocolException>(() => FeishuFrameCodec.Decode(new byte[] { 0x42, 0x7F, 0x01, 0x02 }));
    }
}

/// <summary>飞书受限富文本 → lark_md 的零降级映射回归。</summary>
public sealed class FeishuRichTextTests
{
    [Theory]
    [InlineData("{{red|紧急}}", "<font color='red'>紧急</font>")]
    [InlineData("{{green|正常}}", "<font color='green'>正常</font>")]
    [InlineData("{{orange|警告}}", "<font color='orange'>警告</font>")]
    [InlineData("{{blue|提示}}", "<font color='blue'>提示</font>")]
    [InlineData("{{purple|说明}}", "<font color='purple'>说明</font>")]
    public void EmphasisColors_MapExactlyToOfficialEnum(string input, string expected)
        => Assert.Equal(expected, FeishuRichText.ToLarkMd(input));

    [Fact]
    public void Gray_MapsToGrey_NotNeutral()
    {
        // 官方 column_set 示例确实使用 <font color='grey'>，不做降级
        Assert.Equal("<font color='grey'>已停用</font>", FeishuRichText.ToLarkMd("{{gray|已停用}}"));
    }

    [Fact]
    public void Tag_UsesNativeTextTagCapsule()
    {
        var rendered = FeishuRichText.ToLarkMd("{{tag:red|紧急}}");

        Assert.Equal("<text_tag color='red'>紧急</text_tag>", rendered);
    }

    [Fact]
    public void Tag_Gray_FallsBackToNeutral_BecauseTextTagEnumHasNoGrey()
    {
        // 唯一一处有意近似：text_tag 颜色枚举不含 grey
        Assert.Equal("<text_tag color='neutral'>备注</text_tag>", FeishuRichText.ToLarkMd("{{tag:gray|备注}}"));
    }

    [Fact]
    public void Link_MapsToMarkdownLink()
        => Assert.Equal("[查看详情](https://example.com/status)",
            FeishuRichText.ToLarkMd("{{link:查看详情|https://example.com/status}}"));

    [Theory]
    [InlineData("{{link:x|javascript:alert(1)}}")]   // 危险协议
    [InlineData("{{link:x|/relative}}")]             // 非绝对
    [InlineData("{{link:x|ftp://h/f}}")]             // 非 http(s)
    public void UnsafeLinks_AreNotRenderedAsLinks(string input)
    {
        var rendered = FeishuRichText.ToLarkMd(input);

        Assert.DoesNotContain("](", rendered);
    }

    [Fact]
    public void MixedContent_KeepsPlainTextAndNewlines()
    {
        var input = "第一行\n{{red|红}}\n第二行";

        var rendered = FeishuRichText.ToLarkMd(input);

        Assert.Contains("第一行", rendered);
        Assert.Contains("<font color='red'>红</font>", rendered);
        Assert.Contains("第二行", rendered);
    }

    [Fact]
    public void UnknownOrMalformedMarks_AreKeptLiterally()
    {
        Assert.Equal("{{未闭合", FeishuRichText.ToLarkMd("{{未闭合"));
        Assert.Equal("{{unknown|x}}", FeishuRichText.ToLarkMd("{{unknown|x}}"));
        Assert.Equal("{{red|}}", FeishuRichText.ToLarkMd("{{red|}}"));
    }

    [Fact]
    public void AngleBracketsInsideMarks_AreNotRendered()
    {
        // 避免把用户内容当 HTML 注入卡片
        var rendered = FeishuRichText.ToLarkMd("{{red|<script>x</script>}}");

        Assert.DoesNotContain("<script>", rendered);
    }

    [Fact]
    public void LiteralFallback_CannotForgeMarkdownLink()
    {
        // 未识别标记走字面回退：不转义 markdown 元字符时，正文里能自己拼出链接，
        // 绕过 {{link:…}} 的 https 主机白名单（lark_md 会把 [文字](地址) 渲染成可点链接）
        var rendered = FeishuRichText.ToLarkMd("{{unknown|[点我](https://evil.example)}}");

        Assert.DoesNotContain("](https://evil.example)", rendered);
        Assert.DoesNotContain("[点我]", rendered);
    }

    [Fact]
    public void EmphasisBody_CannotEscapeIntoLinkSyntax()
    {
        // 强调标记正文里夹带 ]( 会提前闭合 {{link:…}} 的链接语法，把剩余部分变成用户可控地址
        var rendered = FeishuRichText.ToLarkMd("{{link:x|https://ok.example}} {{red|a](https://evil.example)}}");

        // 合法链接保留，用户正文里的括号被实体化、无法再构成链接
        Assert.Contains("(https://ok.example)", rendered);
        Assert.DoesNotContain("a](https://evil.example)", rendered);
    }

    [Fact]
    public void PlainTextOutsideMarks_CannotForgeLinkOrFormatting()
    {
        // 只转义标记正文是不够的：标记之外的裸文本同样进入 lark_md，能自己拼出链接与 <font>/<at> 构造
        var rendered = FeishuRichText.ToLarkMd("plain [x](https://evil.example) plain");

        Assert.DoesNotContain("](https://evil.example)", rendered);
        Assert.DoesNotContain("[x]", rendered);
        Assert.Contains("plain", rendered);

        var raw = FeishuRichText.ToLarkMd("plain <font color='red'>red</font> <at id=all></at> plain");

        Assert.DoesNotContain("<font", raw);
        Assert.DoesNotContain("<at ", raw);
    }

    [Theory]
    [InlineData("https://a.b/<script>")]
    [InlineData("https://a.b/x]y[")]
    [InlineData("https://a.b/a b")]
    [InlineData("https://a.b/x)y")]
    [InlineData("https://a.b/x`y")]
    [InlineData("https://a.b/x\"y")]
    public void MarkerUrlWithMarkdownMetachars_IsRejected(string url)
    {
        // 地址被拼进 [文字](地址)：含 )/空格/尖括号/引号等可提前闭合或改写链接语法
        Assert.False(FeishuRichText.IsSafeHttps(url));
        Assert.DoesNotContain("](", FeishuRichText.ToLarkMd($"{{{{link:t|{url}}}}}"));
    }

    [Fact]
    public void MarkerUrl_OrdinaryUrl_StillRenders()
    {
        // 收紧不得误伤普通地址：~ 是合法 URL 字符，query 与百分号编码同样常见
        Assert.True(FeishuRichText.IsSafeHttps("https://a.b/c~d?e=1&f=%20"));
        Assert.Contains("](https://a.b/c~d?e=1&f=%20)",
            FeishuRichText.ToLarkMd("{{link:详情|https://a.b/c~d?e=1&f=%20}}"));
    }

    [Theory]
    [InlineData("https://a.b/&#41;&#91;evil&#93;&#40;https://evil.example&#41;")]
    [InlineData("https://a.b/&amp;#41;x")]
    [InlineData("https://a.b/&lt;script&gt;")]
    [InlineData("https://a.b/&quot;x")]
    public void MarkerUrl_EntityEncodedMetachars_AreRejected(string url)
    {
        // 地址原样拼进 [文字](地址)：若渲染方先做 HTML 实体解码，实体形态的括号即可再拼出链接
        Assert.False(FeishuRichText.IsSafeHttps(url));
    }

    [Fact]
    public void PlainText_KeepsOrdinaryPunctuationReadable()
    {
        // 转义只针对 markdown 元字符，普通标点与中文不应被改动
        var rendered = FeishuRichText.ToLarkMd("任务完成，用时 1.5 秒；详见 https://example.com/a?b=1");

        Assert.Contains("任务完成，用时 1.5 秒；详见 https://example.com/a?b=1", rendered);
    }

    [Fact]
    public void Card_IsValidJson20WithEscapedContent()
    {
        var card = FeishuRichText.BuildCard("任务失败", FeishuRichText.ToLarkMd("{{tag:red|紧急}} {{red|温度过高}}"), "red");

        using var doc = JsonDocument.Parse(FeishuRichText.SerializeContent(card));
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("schema").GetString());
        Assert.Equal("red", root.GetProperty("header").GetProperty("template").GetString());
        var markdown = root.GetProperty("body").GetProperty("elements")[0];
        Assert.Equal("markdown", markdown.GetProperty("tag").GetString());
        var content = markdown.GetProperty("content").GetString();
        Assert.Contains("<text_tag color='red'>紧急</text_tag>", content);
        Assert.Contains("<font color='red'>温度过高</font>", content);
    }

    [Fact]
    public void Card_UpdateMultiIsEnabled_SoLaterCardUpdateIsPossible()
    {
        using var doc = JsonDocument.Parse(FeishuRichText.SerializeContent(FeishuRichText.BuildCard("t", "b")));
        Assert.True(doc.RootElement.GetProperty("config").GetProperty("update_multi").GetBoolean());
    }
}

/// <summary>飞书入站事件解析：只收私聊文本，且不把自己发的消息当入站。</summary>
public sealed class FeishuEventTests
{
    private const string P2pText = """
        {"sender":{"sender_id":{"open_id":"ou_user"},"sender_type":"user"},
         "message":{"message_id":"om_1","chat_id":"oc_1","chat_type":"p2p","message_type":"text",
                    "content":"{\"text\":\"你好\"}"}}
        """;

    private const string GroupText = """
        {"sender":{"sender_id":{"open_id":"ou_user"},"sender_type":"user"},
         "message":{"message_id":"om_2","chat_id":"oc_2","chat_type":"group","message_type":"text",
                    "content":"{\"text\":\"群消息\"}"}}
        """;

    private const string NonText = """
        {"sender":{"sender_id":{"open_id":"ou_user"},"sender_type":"user"},
         "message":{"message_id":"om_3","chat_type":"p2p","message_type":"image",
                    "content":"{\"image_key\":\"k\"}"}}
        """;

    [Fact]
    public void PrivateTextMessage_IsAccepted()
    {
        using var doc = JsonDocument.Parse(P2pText);

        var incoming = FeishuChannelClient.ParseIncoming(doc.RootElement, "cli_app");

        Assert.True(incoming.IsText);
        Assert.Equal("ou_user", incoming.PeerId);
        Assert.Equal("你好", incoming.Text);
        Assert.Equal("om_1", incoming.MessageId);
    }

    [Fact]
    public void GroupMessage_IsRejected_ThisChannelIsPrivateOnly()
    {
        using var doc = JsonDocument.Parse(GroupText);

        Assert.False(FeishuChannelClient.ParseIncoming(doc.RootElement, "cli_app").IsText);
    }

    [Fact]
    public void NonTextMessage_IsRejected()
    {
        using var doc = JsonDocument.Parse(NonText);

        Assert.False(FeishuChannelClient.ParseIncoming(doc.RootElement, "cli_app").IsText);
    }

    [Fact]
    public void MissingChatType_IsRejected()
    {
        using var doc = JsonDocument.Parse("""
            {"sender":{"sender_id":{"open_id":"ou_user"}},"message":{"message_id":"om_4","message_type":"text","content":"{\"text\":\"x\"}"}}
            """);

        Assert.False(FeishuChannelClient.ParseIncoming(doc.RootElement, "cli_app").IsText);
    }
}
