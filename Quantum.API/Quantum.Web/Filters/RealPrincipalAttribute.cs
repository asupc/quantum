using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Quantum.Entities.Result;
using Quantum.Utils;

namespace Quantum.Web.Filters;

/// <summary>
/// 要求调用者是**真实登录主体**（Web 管理令牌或 App 登录令牌）。
///
/// 为什么需要它：<see cref="CustomAuthorizationFilter"/> 只验签不认身份，而 Open AppKey 换取的
/// 10 分钟令牌同样是合法签名（"Name" 为固定标记、无用户/设备身份），会原样穿透普通受控端点。
/// 因此「不对第三方匿名开放」的端点必须正向判定主体，而不是靠"没加 ManagerOnly 就谁都行"。
///
/// 用于执行记录/运行详情这类会暴露任务名、脚本路径快照与失败摘要的读接口（一期 G2 契约：
/// Open/匿名不开放运行历史）。它不替代权限分层——Manager 与否仍由 <c>IsManager</c> claim 决定。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RealPrincipalAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var name = http.User?.FindFirst("Name")?.Value;

        // 无主体、或主体是 Open AppKey 令牌 → 视为未认证（HTTP 恒 200，信封 Code=401）
        if (string.IsNullOrEmpty(name) || http.IsOpenAppToken())
        {
            context.Result = new ObjectResult(new ResultModel
            {
                Code = 401,
                Message = "需要登录主体身份"
            })
            {
                StatusCode = 200
            };
        }

        return Task.CompletedTask;
    }
}
