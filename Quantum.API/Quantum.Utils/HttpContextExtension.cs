using Microsoft.AspNetCore.Http;

namespace Quantum.Utils;

public static class HttpContextExtension
{
    public static string GetUserIp(this HttpContext context)
    {
        // X-Forwarded-For 不再直读：仅当直连方在 KnownProxies 配置内时，
        // ForwardedHeaders 中间件已把 RemoteIpAddress 改写为真实客户端 IP；
        // 未配置代理（公网直连）时 RemoteIpAddress 即对端真实地址，伪造 XFF 无效
        return context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    public static string GetUserId(this HttpContext context)
    {
        var t = context.User.FindFirst("Name");
        if (t != null)
        {
            return t.Value;
        }
        return "";
    }

    /// <summary>
    /// 当前登录设备 Id（JWT "DeviceId" 声明，App 登录时签发；Web 管理端令牌该声明为空）。
    /// </summary>
    public static string GetUserDeviceId(this HttpContext context)
    {
        var t = context.User.FindFirst("DeviceId");
        if (t != null)
        {
            return t.Value;
        }
        return "";
    }

    /// <summary>
    /// 当前令牌是否管理员（正向 "Manager"="true" claim；Open/任务临时令牌无该 claim 恒为 false）。
    /// 服务端任务列表过滤、Env 限权等按调用者身份收敛的场景使用，与 [ManagerOnly] 同一事实源。
    /// </summary>
    public static bool IsManager(this HttpContext context)
    {
        return string.Equals(context.User?.FindFirst("Manager")?.Value, "true", StringComparison.Ordinal);
    }

    /// <summary>
    /// 当前令牌是否 Open AppKey 换取的匿名式令牌（"Name" 恒为固定标记、无用户/设备身份）。
    /// 供「不开放给第三方匿名调用」的端点判定：它只验签的过滤器挡不住，必须正向识别主体。
    /// </summary>
    public static bool IsOpenAppToken(this HttpContext context)
    {
        return string.Equals(context.User?.FindFirst("Name")?.Value, OpenAppTokenName, StringComparison.Ordinal);
    }

    /// <summary>Open AppKey 令牌的固定主体名（与 OpenAuthService.AppKeyTokenName 同值；Utils 不引用 Application，故在此侧留常量并注释同源）。</summary>
    public const string OpenAppTokenName = "______App-Key-Auth";
}
