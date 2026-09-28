using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quantum.Application.Channels;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 通道平台端点与请求头的协议回归护栏。
///
/// 背景：扫码对接长期失败，根因是把「公开的协议常量」当成了机密、并把两个端点接错了地方：
/// 1) 取调用凭证固定在 bots.qq.com，**不在** openapi 域名下——早期误拼到 api.bot.qq.com 导致取 token 必失败；
/// 2) Gateway 路径是 /gateway，不是 /gateway/bot；
/// 3) 微信取二维码是 GET 无请求体，不是 POST；
/// 4) iLink 请求头只应有 iLink-App-ClientVersion + 鉴权两件套，不该多发 iLink-App-Id；
/// 5) 飞书取长连接地址：请求体字段名是 AppID（大写 D），响应地址嵌在 data.URL 里。
/// 这些都是「看起来像小细节、改错却整条通道不通」的点，故以测试钉死。
/// </summary>
public sealed class ChannelEndpointTests
{
    // ── QQ：凭证域名 ────────────────────────────────────────────────
    [Fact]
    public void Qq_AccessToken_UsesBotsQqCom_NotOpenApiHost()
    {
        var uri = ChannelNetwork.QqTokenUri("/app/getAppAccessToken");

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("bots.qq.com", uri.Host);
        Assert.Equal("/app/getAppAccessToken", uri.AbsolutePath);
        // 关键断言：凭证接口绝不能落在 openapi 域名上（这正是历史故障点）
        Assert.NotEqual("api.bot.qq.com", uri.Host);
    }

    [Fact]
    public void Qq_AccessTokenHost_IsCoveredByTransportAllowList()
    {
        Assert.True(ChannelNetwork.IsQqHost("bots.qq.com"));
        Assert.True(ChannelNetwork.AllowedUri(ChannelNetwork.QqTokenUri("/app/getAppAccessToken")));
    }

    // ── QQ：openapi 端点与 Gateway 路径 ─────────────────────────────
    [Fact]
    public void Qq_OpenApi_DefaultsToProductionBase()
    {
        Assert.Equal("https://api.bot.qq.com/", ChannelNetwork.QqProdApiBase);
        Assert.Equal("https://api.bot.qq.com/gateway", ChannelNetwork.QqUri("/gateway").AbsoluteUri);
    }

    [Fact]
    public void Qq_Gateway_PathIsSlashGateway_NotGatewaySlashBot()
    {
        var path = ChannelNetwork.QqUri("/gateway").AbsolutePath;

        Assert.Equal("/gateway", path);
        Assert.NotEqual("/gateway/bot", path);
    }

    [Fact]
    public void Qq_SandboxBase_IsAcceptedAndStaysInsideQqDomain()
    {
        // 平台对新增机器人默认启用 IP 白名单，正式环境只放行白名单公网 IP；沙箱不受该限制。
        var uri = ChannelNetwork.QqUri("/gateway", "https://sandbox.api.sgroup.qq.com/");

        Assert.Equal("sandbox.api.sgroup.qq.com", uri.Host);
        Assert.Equal("/gateway", uri.AbsolutePath);
    }

    [Theory]
    [InlineData("http://api.bot.qq.com/")]   // 必须 https
    [InlineData("https://evil.example/")]    // 非 qq.com
    [InlineData("https://qq.com.attacker.example/")]
    [InlineData("https://user@api.bot.qq.com/")]
    public void QqUri_RejectsUntrustedBase(string base64)
    {
        Assert.Throws<BusinessException>(() => ChannelNetwork.QqUri("/gateway", base64));
    }

    [Fact]
    public void QqUri_BlankBase_FallsBackToProduction()
    {
        // 配置未填接入点时应回落正式环境，而不是拼出坏地址
        Assert.Equal("https://api.bot.qq.com/gateway", ChannelNetwork.QqUri("/gateway", null).AbsoluteUri);
    }

    [Fact]
    public void Qq_ResolveApiBase_SandboxBlank_UsesOfficialSandboxDefault()
    {
        // 沙箱留空必须默认官方沙箱接入点——普通用户不知道「沙箱配置」里的地址，不能逼他去找
        var baseUri = ChannelNetwork.ResolveQqApiBase("Sandbox", null);

        Assert.Equal("https://sandbox.api.sgroup.qq.com/", baseUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Production")]
    public void Qq_ResolveApiBase_ProductionBlank_UsesProductionDefault(string? environment)
    {
        Assert.Equal("https://api.bot.qq.com/", ChannelNetwork.ResolveQqApiBase(environment, null));
    }

    [Fact]
    public void Qq_ResolveApiBase_ExplicitBase_WinsOverEnvironmentDefault()
    {
        // 沙箱域名是否随 2026-08-10 域名统一迁移未实测，显式覆盖始终优先于内置默认
        Assert.Equal("https://sandbox.api.bot.qq.com/",
            ChannelNetwork.ResolveQqApiBase("Sandbox", "https://sandbox.api.bot.qq.com/"));
        Assert.Equal("https://api.bot.qq.com/",
            ChannelNetwork.ResolveQqApiBase("Sandbox", "https://api.bot.qq.com/"));
    }

    [Fact]
    public void Qq_ResolveApiBase_StillRejectsUntrustedBase()
    {
        Assert.Throws<BusinessException>(() => ChannelNetwork.ResolveQqApiBase("Sandbox", "https://evil.example/"));
    }

    // ── 微信：iLink 版本默认值与请求方法 ────────────────────────────
    [Fact]
    public void Weixin_DefaultClientVersion_MatchesOfficialProtocolSample()
    {
        // 官方样例中扫码状态接口固定 iLink-App-ClientVersion: 1
        Assert.Equal("1", WeixinQrLoginService.DefaultClientVersion);
    }

    [Fact]
    public void Weixin_DefaultChannelVersion_IsAWellFormedSemver()
    {
        var (_, channel) = WeixinQrLoginService.ResolveVersions(null, null);

        Assert.Matches(@"^[A-Za-z0-9._-]{1,32}$", channel);
    }

    [Fact]
    public void Weixin_ResolveVersions_NeverThrowsWhenNothingConfigured()
    {
        Environment.SetEnvironmentVariable("QUANTUM_CHANNEL_WEIXIN_CLIENT_VERSION", null);
        Environment.SetEnvironmentVariable("QUANTUM_CHANNEL_WEIXIN_CHANNEL_VERSION", null);

        // 回归核心：不再因「版本未核对」而拒绝——这正是扫码第一步被锁死的原因
        var result = WeixinQrLoginService.ResolveVersions(null, null);

        Assert.False(string.IsNullOrWhiteSpace(result.ClientVersion));
        Assert.False(string.IsNullOrWhiteSpace(result.ChannelVersion));
    }

    // ── 飞书：长连接 bootstrap 的字段名与响应层级 ────────────────────
    [Fact]
    public void Feishu_WssEndpointPath_IsUnderOpenFeishuCn()
    {
        Assert.Equal("https://open.feishu.cn/callback/ws/endpoint",
            ChannelNetwork.FeishuUri("/callback/ws/endpoint").AbsoluteUri);
    }

    [Fact]
    public void Feishu_WssEndpointRequest_UsesAppID_WithCapitalD()
    {
        var body = FeishuChannelClient.BuildEndpointRequest("cli_x", "secret");

        // 平台按大小写敏感匹配：写成 appId/AppId 一律 HTTP 400 + code 9499，长连接永远建不起来
        Assert.Equal("cli_x", body["AppID"]!.GetValue<string>());
        Assert.Equal("secret", body["AppSecret"]!.GetValue<string>());
        Assert.Null(body["appId"]);
        Assert.Null(body["AppId"]);
    }

    [Fact]
    public void Feishu_WssEndpointResponse_ReadsUrlAndConfigFromNestedData()
    {
        using var doc = JsonDocument.Parse("""
            {"code":0,"msg":"","data":{"URL":"wss://msg-frontier.feishu.cn/ws/v2?t=1",
             "ClientConfig":{"PingInterval":90,"ReconnectInterval":60}}}
            """);

        var (url, ping, reconnect) = FeishuChannelClient.ParseEndpointResponse(doc.RootElement);

        Assert.StartsWith("wss://", url);
        Assert.Equal(90, ping);
        Assert.Equal(60, reconnect);
    }

    [Fact]
    public void Feishu_WssEndpointResponse_RejectsTopLevelUrl()
    {
        // 历史故障形态：URL 读在顶层 → 即便取到地址也判成「缺地址」
        using var doc = JsonDocument.Parse("""{"code":0,"URL":"wss://msg-frontier.feishu.cn/ws/v2?t=1"}""");

        var error = Assert.Throws<ChannelProtocolException>(
            () => FeishuChannelClient.ParseEndpointResponse(doc.RootElement));

        Assert.Equal("FEISHU_WS_ENDPOINT_MISSING", error.ErrorCode);
    }

    [Fact]
    public void Feishu_WssEndpointResponse_KeepsPlatformCodeForDiagnosis()
    {
        using var doc = JsonDocument.Parse("""{"code":9499,"msg":"Bad Request","data":{}}""");

        var error = Assert.Throws<ChannelProtocolException>(
            () => FeishuChannelClient.ParseEndpointResponse(doc.RootElement));

        // 管理页据此显示可定位的错误码，而不是把所有异常压成 FEISHU_WSS_ERROR
        Assert.Equal("FEISHU_WS_ENDPOINT_9499", error.ErrorCode);
    }

    // ── 三平台共用：POST 请求体必须自带 Content-Length ───────────────
    [Fact]
    public void PostJson_RequestBody_CarriesContentLength_NotChunked()
    {
        // 微信 iLink 网关对分块传输直接回 HTTP 412 且响应体为空，长轮询因此永远取不到消息；
        // JsonContent 不预计算长度，故 PostAsync 一律走预缓冲的字节内容。
        var anonymous = ChannelNetwork.BuildJsonContent(
            new { get_updates_buf = "", base_info = new { channel_version = "1.0.3", bot_agent = "Quantum/1" } });
        var node = ChannelNetwork.BuildJsonContent(
            new JsonObject { ["AppID"] = "cli_x", ["AppSecret"] = "secret" });

        Assert.True(anonymous.Headers.ContentLength.HasValue);
        Assert.True(node.Headers.ContentLength.HasValue);
        Assert.Equal("application/json", anonymous.Headers.ContentType.MediaType);
        Assert.Equal("cli_x", JsonNode.Parse(Encoding.UTF8.GetString(ReadAll(node)))["AppID"].GetValue<string>());
    }

    private static byte[] ReadAll(HttpContent content)
    {
        using var ms = new MemoryStream();
        content.CopyToAsync(ms).GetAwaiter().GetResult();
        return ms.ToArray();
    }

    [Fact]
    public void Feishu_SendResponse_ReadsMessageIdFromNestedData()
    {
        using var nested = JsonDocument.Parse("""{"code":0,"msg":"success","data":{"message_id":"om_x100abc","chat_id":"oc_1"}}""");
        using var flat = JsonDocument.Parse("""{"code":0,"message_id":"om_flat"}""");
        using var none = JsonDocument.Parse("""{"code":0,"data":{"chat_id":"oc_1"}}""");

        // 回归核心：读不到 data.message_id 会把「已投递成功的卡片」判成失败并降级补发文本，用户收到两条重复消息
        Assert.Equal("om_x100abc", FeishuChannelClient.ExtractMessageId(nested.RootElement));
        Assert.Equal("om_flat", FeishuChannelClient.ExtractMessageId(flat.RootElement));
        Assert.Null(FeishuChannelClient.ExtractMessageId(none.RootElement));
    }

    // ── 鉴权头按平台分方案（飞书发送 400 的根因）────────────────────
    [Fact]
    public void AuthHeader_QqUsesQqBot_Scheme()
    {
        using var qq = new HttpRequestMessage();
        ChannelNetwork.AddHeaders(qq, "tok", version: null);

        // QQ 开放平台要的是 QQBot 方案，不能被"标准 Bearer"带跑
        Assert.Equal("QQBot", qq.Headers.Authorization.Scheme);
    }

    [Fact]
    public void AuthHeader_FeishuMustUseBearer_NotQqBot()
    {
        using var feishu = new HttpRequestMessage();
        ChannelNetwork.AddHeaders(feishu, "tok", version: null, authScheme: ChannelNetwork.BearerAuthScheme);

        // 回归核心：飞书与 QQ 同为「无 version」形态，共用默认方案会让每条发送恒 HTTP_400
        Assert.Equal("Bearer", feishu.Headers.Authorization.Scheme);
    }

    [Fact]
    public void AuthHeader_WeixinILinkStillBearerWithAuthType()
    {
        using var weixin = new HttpRequestMessage();
        ChannelNetwork.AddHeaders(weixin, "tok", version: "1");

        Assert.Equal("Bearer", weixin.Headers.Authorization.Scheme);
        Assert.Equal("ilink_bot_token", weixin.Headers.TryGetValues("AuthorizationType", out var v) ? string.Join("", v) : null);
    }

    // ── 微信：成功判定口径（getupdates/sendmessage 不带 ret）──────────
    [Theory]
    [InlineData("""{"msgs":[],"sync_buf":"CAEY","get_updates_buf":"CgkI"}""")]
    [InlineData("""{"msgs":[{"message_id":"1"}],"get_updates_buf":"CgkI"}""")]
    public void Weixin_PollAccepted_MissingRetMeansSuccess(string json)
    {
        // 回归核心：把「字段缺失」当失败会让通道在 HTTP 200 上永远转圈（实测曾报 WEIXIN_RET_INVALID）
        using var doc = JsonDocument.Parse(json);

        WeixinChannelWorker.EnsurePollAccepted(doc.RootElement);
    }

    [Theory]
    [InlineData("""{"ret":-14}""", "WEIXIN_SESSION_EXPIRED")]
    [InlineData("""{"errcode":-14}""", "WEIXIN_SESSION_EXPIRED")]
    [InlineData("""{"ret":-1}""", "WEIXIN_RET_-1")]
    [InlineData("""{"ret":0,"errcode":40001}""", "WEIXIN_RET_40001")]
    public void Weixin_PollAccepted_RejectsPlatformFailure(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);

        var error = Assert.Throws<ChannelProtocolException>(
            () => WeixinChannelWorker.EnsurePollAccepted(doc.RootElement));

        Assert.Equal(expected, error.ErrorCode);
    }
}
