using Quantum.Application.Channels;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 微信 iLink 协议版本字段的取值口径：入参 → 环境变量 → 内置默认值，**留空不得拒绝**。
///
/// 背景：这两个字段（channel_version 即腾讯官方渠道插件版本号、iLink-App-ClientVersion）都是公开的协议
/// 元数据，不是密钥也不是身份凭据。早期实现把它们当成「必须人工核对的机密」、缺失即抛错，
/// 等于把扫码流程第一步自己锁死（管理页填不出值就完全无法开始扫码）。现改为默认可用、需要时可覆盖，
/// 同时保留「显式传了脏值要拒绝」的格式校验。
///
/// 用例只在自身作用域内改动进程环境变量并在 finally 还原。
/// </summary>
public sealed class ChannelWeixinVersionTests
{
    private const string ClientVar = "QUANTUM_CHANNEL_WEIXIN_CLIENT_VERSION";
    private const string ChannelVar = "QUANTUM_CHANNEL_WEIXIN_CHANNEL_VERSION";

    private static void ClearEnv()
    {
        Environment.SetEnvironmentVariable(ClientVar, null);
        Environment.SetEnvironmentVariable(ChannelVar, null);
    }

    [Fact]
    public void ResolveVersions_NothingConfigured_UsesBuiltInDefaults()
    {
        ClearEnv();

        var (client, channel) = WeixinQrLoginService.ResolveVersions(null, null);

        Assert.Equal(WeixinQrLoginService.DefaultClientVersion, client);
        Assert.Equal(WeixinQrLoginService.DefaultChannelVersion, channel);
    }

    [Fact]
    public void ResolveVersions_BlankAndWhitespace_AlsoUseDefaults()
    {
        ClearEnv();

        var (client, channel) = WeixinQrLoginService.ResolveVersions("  ", "");

        Assert.Equal(WeixinQrLoginService.DefaultClientVersion, client);
        Assert.Equal(WeixinQrLoginService.DefaultChannelVersion, channel);
    }

    [Fact]
    public void ResolveVersions_BlankFields_FallBackToEnvironment()
    {
        Environment.SetEnvironmentVariable(ClientVar, "1450000");
        Environment.SetEnvironmentVariable(ChannelVar, "3.0.0");
        try
        {
            var (client, channel) = WeixinQrLoginService.ResolveVersions(null, "  ");
            Assert.Equal("1450000", client);
            Assert.Equal("3.0.0", channel);
        }
        finally { ClearEnv(); }
    }

    [Fact]
    public void ResolveVersions_ManualValue_WinsOverEnvironment()
    {
        Environment.SetEnvironmentVariable(ClientVar, "1450000");
        Environment.SetEnvironmentVariable(ChannelVar, "3.0.0");
        try
        {
            var (client, channel) = WeixinQrLoginService.ResolveVersions("1600000", "4.1.2");
            Assert.Equal("1600000", client);
            Assert.Equal("4.1.2", channel);
        }
        finally { ClearEnv(); }
    }

    [Fact]
    public void ResolveVersions_RejectsNonDecimalClientVersion_EvenFromEnvironment()
    {
        Environment.SetEnvironmentVariable(ClientVar, "v1.4.5");
        Environment.SetEnvironmentVariable(ChannelVar, "3.0.0");
        try
        {
            Assert.Throws<BusinessException>(() => WeixinQrLoginService.ResolveVersions(null, null));
        }
        finally { ClearEnv(); }
    }

    [Theory]
    [InlineData("bad;version")]
    [InlineData("has space")]
    [InlineData("换中文")]
    public void ResolveVersions_RejectsMalformedChannelVersion(string channel)
    {
        ClearEnv();

        Assert.Throws<BusinessException>(() => WeixinQrLoginService.ResolveVersions(null, channel));
    }

    [Fact]
    public void ResolveVersions_RejectsZeroClientVersion()
    {
        ClearEnv();

        Assert.Throws<BusinessException>(() => WeixinQrLoginService.ResolveVersions("0", null));
    }

    /// <summary>旧入口名保留兼容，语义与新解析一致（留空走默认值，不再抛错）。</summary>
    [Fact]
    public void ValidateVersions_BackwardCompatibleEntryPoint_UsesDefaults()
    {
        ClearEnv();

        var (client, channel) = WeixinQrLoginService.ValidateVersions(null, null);

        Assert.Equal(WeixinQrLoginService.DefaultClientVersion, client);
        Assert.Equal(WeixinQrLoginService.DefaultChannelVersion, channel);
    }
}
