using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Quantum.Application;
using Quantum.Entities.Model;
using Quantum.Entities.Result;
using Quantum.Utils;

namespace Quantum.Web.Filters;

/// <summary>
/// 外部推送专用鉴权（G-Push）：只解析 <c>Authorization: PushKey {id}.{secret}</c>。
///
/// 与其他鉴权面的隔离：
/// - 本过滤器**不读 JWT、不产出 ClaimsPrincipal**，因此 PushKey 拿不到任何身份/角色（更不可能 Manager=true），
///   也无法访问挂 [CustomAuthorizationFilter]/[LoggedInUser] 的旧端点；
/// - 反向同理：旧 Open AppKey、普通用户令牌、Manager JWT 都过不了本过滤器（它们没有 PushKey 头）；
/// - 挂本过滤器的控制器不得再挂 [AllowAnonymous]，也不参与默认认证方案。
/// 失败统一 HTTP 200 + 信封 Code=401，且不区分「Id 不存在/密钥错/已吊销/已过期」，避免被用来探测凭据。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class PushKeyAuthAttribute : Attribute, IAsyncAuthorizationFilter
{
    /// <summary>校验通过后凭据实体在 HttpContext.Items 里的键</summary>
    public const string CredentialItemKey = "ExternalPushCredential";

    /// <summary>同一来源 IP 在窗口内允许的凭据校验失败次数（超过后窗口内直接拒绝，不再打库）</summary>
    internal static int MaxFailuresPerWindow = 20;

    internal static TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, (int Count, DateTime Until)> Failures = new();

    /// <summary>测试专用：清空 IP 失败计数。</summary>
    internal static void ResetFailuresForTest() => Failures.Clear();

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var ip = http.GetUserIp();

        // 无效凭据按来源 IP 限流：KnownProxies/ForwardedHeaders 未核对前 GetUserIp 不轻信 XFF
        if (Throttled(ip))
        {
            context.Result = Unauthorized();
            return;
        }

        var service = http.RequestServices.GetService(typeof(ExternalPushCredentialService))
            as ExternalPushCredentialService;
        var header = http.Request.Headers["Authorization"].ToString();
        var credential = service == null ? null : await service.VerifyAsync(header);
        if (credential == null)
        {
            RecordFailure(ip);
            context.Result = Unauthorized();
            return;
        }

        Failures.TryRemove(ip, out _);
        http.Items[CredentialItemKey] = credential;
        // 不设置 User：本端点没有「用户」概念，任何按 User 判定的下游逻辑都不该被外部凭据触发
        await Task.CompletedTask;
    }

    private static bool Throttled(string ip)
        => Failures.TryGetValue(ip ?? "-", out var entry) && entry.Until > DateTime.UtcNow && entry.Count >= MaxFailuresPerWindow;

    private static void RecordFailure(string ip)
    {
        if (Failures.Count > 5000)
        {
            Failures.Clear();
        }

        var key = ip ?? "-";
        var now = DateTime.UtcNow;
        Failures.AddOrUpdate(key,
            _ => (1, now.Add(FailureWindow)),
            (_, old) => old.Until <= now ? (1, now.Add(FailureWindow)) : (old.Count + 1, old.Until));
    }

    private static ObjectResult Unauthorized() => new(new ResultModel
    {
        Code = 401,
        Message = "推送凭据无效"
    })
    {
        StatusCode = 200
    };
}
