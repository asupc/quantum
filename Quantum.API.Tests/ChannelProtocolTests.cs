using System.Net;
using System.Text.Json;
using Quantum.Application;
using Quantum.Application.Channels;
using Quantum.Web.Controllers;
using Quantum.Web.Filters;

namespace Quantum.API.Tests;

/// <summary>外部报文字段、安全网络目标与 Manager-only 配置边界离线回归。</summary>
public sealed class ChannelProtocolTests
{
    [Fact]
    public void QqGatewaySequence_UsesInt64Cursor()
    {
        using var frame = JsonDocument.Parse("""{"op":0,"s":2147483648,"t":"READY"}""");
        Assert.Equal(2147483648L, QqChannelClient.Long(frame.RootElement, "s"));
    }

    [Theory]
    [InlineData("7200", true, 7200)]
    [InlineData("7200", false, 7200)]
    public void QqTokenResponse_AcceptsOfficialStringExpiryAndLegacyNumber(string rawTtl, bool quoted, int expected)
    {
        using var response = JsonDocument.Parse("""{"access_token":"SAMPLE","expires_in":_TTL_}"""
            .Replace("_TTL_", quoted ? JsonSerializer.Serialize(rawTtl) : rawTtl));
        var (access, ttl) = QqChannelClient.ParseTokenResponse(response.RootElement);
        Assert.Equal("SAMPLE", access);
        Assert.Equal(expected, ttl);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("0", false)]
    [InlineData("invalid", true)]
    [InlineData("null", false)]
    [InlineData("2147483648", true)]
    public void QqTokenResponse_RejectsInvalidExpiryWithoutReportingCredentialsInvalid(string rawTtl, bool quoted)
    {
        using var response = JsonDocument.Parse("""{"access_token":"SAMPLE","expires_in":_TTL_}"""
            .Replace("_TTL_", quoted ? JsonSerializer.Serialize(rawTtl) : rawTtl));
        var error = Assert.Throws<ChannelProtocolException>(() => QqChannelClient.ParseTokenResponse(response.RootElement));
        Assert.Equal("QQ_AUTH_RESPONSE_INVALID", error.ErrorCode);
    }

    [Fact]
    public void QqTokenResponse_RejectsActualPlatformAuthFailure()
    {
        using var response = JsonDocument.Parse("""{"code":401,"message":"denied"}""");
        var error = Assert.Throws<ChannelProtocolException>(() => QqChannelClient.ParseTokenResponse(response.RootElement));
        Assert.Equal("QQ_AUTH_INVALID", error.ErrorCode);
    }

    [Fact]
    public void QqMessageIndex_ParsesStringArray_WithoutAcceptingDuplicateKeys()
    {
        using var eventBody = JsonDocument.Parse("""["auth_token=DO_NOT_LOG","msg_idx=REFIDX_one=="]""");
        Assert.Equal("REFIDX_one==", QqChannelClient.GetMessageIndex(eventBody.RootElement));
        using var ambiguous = JsonDocument.Parse("""["msg_idx=one","msg_idx=two"]""");
        Assert.Null(QqChannelClient.GetMessageIndex(ambiguous.RootElement));
    }

    [Theory]
    [InlineData("123", "123")]
    [InlineData("\"123\"", "123")]
    public void WeixinMessageId_AcceptsNumericAndStringWireRepresentations(string rawId, string expected)
    {
        using var body = JsonDocument.Parse("""{"from_user_id":"peer","to_user_id":"bot","message_type":1,"message_id":_ID_,"context_token":"ctx","item_list":[{"type":1,"text_item":{"text":"你好"}}]}""".Replace("_ID_", rawId));
        var result = WeixinChannelWorker.ParseIncoming(body.RootElement, "bot");
        Assert.Equal(expected, result.MessageId);
        Assert.True(result.IsText);
        Assert.Equal("ctx", result.ContextToken);
    }

    [Fact]
    public async Task ChannelReplyRoute_FlowsOnlyWithinTheCommandExecution()
    {
        Assert.Null(ChannelReplyContext.Current);
        using (ChannelReplyContext.Use("route-a"))
        {
            var message = new Quantum.Entities.DTOs.MessageProccessDTO { ChannelReplyRouteId = ChannelReplyContext.Current };
            Assert.Equal("route-a", message.Clone().ChannelReplyRouteId);
            Assert.Equal("route-a", await Task.Run(() => ChannelReplyContext.Current));
            using (ChannelReplyContext.Use("route-b"))
                Assert.Equal("route-b", ChannelReplyContext.Current);
            Assert.Equal("route-a", ChannelReplyContext.Current);
        }
        Assert.Null(ChannelReplyContext.Current);
    }

    [Fact]
    public void WeixinPrivateText_CarryingGroupId_IsStillAccepted()
    {
        // 真机实测（2026-09-28 拒收指纹 NotText:group）：微信 1:1 私聊报文也带 group_id，
        // 把它当群聊标识会让机器人永远不回；私聊判据只看「发给本机器人 + message_type 1/2 + 有文本」
        using var body = JsonDocument.Parse("""{"from_user_id":"peer","to_user_id":"bot","group_id":"session-1","message_type":2,"message_id":106,"context_token":"ctx","item_list":[{"type":1,"text_item":{"text":"你好"}}]}""");

        var incoming = WeixinChannelWorker.ParseIncoming(body.RootElement, "bot");

        Assert.True(incoming.IsText);
        Assert.Null(incoming.RejectHint);
    }

    [Theory]
    [InlineData("FeishuBot", 24 * 60)]   // 按 open_id 主动推送，没有被动窗口；放宽以免长任务的结果被静默丢弃
    [InlineData("WeixinBot", 5)]         // context_token 真实有效期未实测 → 保守
    [InlineData("QQBot", 5)]             // 官方被动回复窗口 + 单消息四条上限 → 保守
    public void ReplyWindow_IsScopedPerPlatform(string platform, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), ChannelManagementService.ReplyWindow(platform));
    }

    [Fact]
    public void ChannelFallback_AudioWithPublicUrl_BecomesTappableTextLink()
    {
        // 「从哪儿触发就回哪儿」：音乐搜索这类音频输出（MessageType=音频）此前被"只放行纯文本"整批跳过
        var text = SendMessageHelper.BuildChannelFallback(new Quantum.Entities.DTOs.MessageProccessDTO
        {
            MessageType = Quantum.Entities.Model.MessageType.音频,
            message = "https://m801.music.126.net/20260928/a.mp3"
        }, "audio");

        Assert.Equal("[音频] https://m801.music.126.net/20260928/a.mp3", text);
    }

    [Theory]
    [InlineData("image", "D:\\quantum\\out\\p.jpg")]
    [InlineData("video", "http://192.168.1.10/v.mp4")]
    [InlineData("audio", "http://localhost:8080/a.mp3")]
    [InlineData("audio", "ftp://m801.music.126.net/a.mp3")]
    public void ChannelFallback_InternalPathsAndHosts_NeverLeaveTheMachine(string contentType, string content)
    {
        var message = new Quantum.Entities.DTOs.MessageProccessDTO
        {
            MessageType = contentType switch
            {
                "image" => Quantum.Entities.Model.MessageType.图片,
                "video" => Quantum.Entities.Model.MessageType.视频,
                _ => Quantum.Entities.Model.MessageType.音频
            },
            message = content
        };

        var text = SendMessageHelper.BuildChannelFallback(message, contentType);

        // 安全红线：本地路径、内网/回环地址与非 http(s) 方案一律不得推给公网平台
        Assert.DoesNotContain(content, text);
        Assert.Contains("结果已在 App 生成", text);
    }

    [Fact]
    public void GroupOrWrongBot_IsNeverClassifiedAsPrivateText()
    {
        using var body = JsonDocument.Parse("""{"from_user_id":"peer","to_user_id":"other-bot","group_id":"group","message_type":1,"message_id":100,"context_token":"ctx","item_list":[{"type":1,"text_item":{"text":"执行脚本"}}]}""");
        var incoming = WeixinChannelWorker.ParseIncoming(body.RootElement, "bot");

        Assert.False(incoming.IsText);
        // 判据不通过也要能定位：指纹必须点出是 to_user_id 还是 group_id，不能只剩 UnsupportedContent
        Assert.Contains("toNotBot", incoming.RejectHint);
        Assert.Contains("group", incoming.RejectHint);
        Assert.DoesNotContain("执行脚本", incoming.RejectHint);
    }

    [Fact]
    public void UnexpectedMessageType_IsReportedInHint()
    {
        using var body = JsonDocument.Parse("""{"from_user_id":"peer","to_user_id":"bot","message_type":7,"message_id":104,"context_token":"ctx","item_list":[{"type":1,"text_item":{"text":"你好"}}]}""");

        var incoming = WeixinChannelWorker.ParseIncoming(body.RootElement, "bot");

        Assert.False(incoming.IsText);
        Assert.Contains("mtype=7", incoming.RejectHint);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void WeixinPrivateText_AcceptsBothMessageTypeForms(int messageType)
    {
        // 出站 1:1 文本用的是 message_type=2（见 ChannelDeliveryWorker），入站判定必须同样接受，
        // 否则连接与轮询全正常、每条真实私聊却被判成 UnsupportedContent
        using var body = JsonDocument.Parse("""
            {"from_user_id":"peer","to_user_id":"bot","message_type":_MT_,"message_id":101,
             "context_token":"ctx","item_list":[{"type":1,"text_item":{"text":"你好"}}]}
            """.Replace("_MT_", messageType.ToString()));

        Assert.True(WeixinChannelWorker.ParseIncoming(body.RootElement, "bot").IsText);
    }

    [Fact]
    public void WeixinPrivateText_AcceptsTextItemAmongSiblings()
    {
        using var body = JsonDocument.Parse("""
            {"from_user_id":"peer","to_user_id":"bot","message_type":2,"message_id":102,"context_token":"ctx",
             "item_list":[{"type":3,"image_item":{"url":"do-not-log"}},{"type":1,"text_item":{"text":" 菜单 "}}]}
            """);

        var incoming = WeixinChannelWorker.ParseIncoming(body.RootElement, "bot");

        Assert.True(incoming.IsText);
        Assert.Equal("菜单", incoming.Text);
    }

    [Fact]
    public void WeixinNonText_RejectsButCarriesStructureHintWithoutContent()
    {
        using var body = JsonDocument.Parse("""
            {"from_user_id":"peer","to_user_id":"bot","message_type":2,"message_id":103,"context_token":"ctx",
             "item_list":[{"type":3,"image_item":{"url":"secret-url"}},{"type":49,"quote_item":{"text":"secret-quote"}}]}
            """);

        var incoming = WeixinChannelWorker.ParseIncoming(body.RootElement, "bot");

        Assert.False(incoming.IsText);
        Assert.Contains("NotText", incoming.RejectHint);
        Assert.Contains("types=[3|49]", incoming.RejectHint);
        Assert.DoesNotContain("secret", incoming.RejectHint);   // 指纹只许含结构，绝不带正文/链接
    }

    [Fact]
    public void NetworkRejectsPrivateIpsAndUntrustedHosts()
    {
        foreach (var text in new[] { "127.0.0.1", "10.0.0.8", "172.17.0.3", "192.168.0.1", "169.254.1.1", "::1", "fc00::1", "2001:db8::1", "2001::1", "2002:0a00:0001::1" })
            Assert.False(ChannelNetwork.IsPublicIp(IPAddress.Parse(text)));
        Assert.True(ChannelNetwork.IsPublicIp(IPAddress.Parse("1.1.1.1")));
        Assert.False(ChannelNetwork.IsWeixinHost("weixin.qq.com.attacker.example"));
        Assert.Throws<Quantum.Utils.BusinessException>(() => ChannelNetwork.WeixinUri("getupdates", "http://localhost/"));
    }

    [Fact]
    public void ManagementController_RequiresPositiveManagerClaimFilter()
    {
        Assert.True(Attribute.IsDefined(typeof(ChannelController), typeof(LoggedInUserAttribute)));
        Assert.True(Attribute.IsDefined(typeof(ChannelController), typeof(CustomAuthorizationFilter)));
    }
}
