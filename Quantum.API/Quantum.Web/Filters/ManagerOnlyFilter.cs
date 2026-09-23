using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Quantum.Entities.Result;
using Quantum.Utils;

namespace Quantum.Web.Filters;

/// <summary>
/// 管理员专用端点过滤器：在 CustomAuthorizationFilter（验签）之后执行，只认正向
/// "Manager"="true" claim——该 claim 仅由 LoginService（Web 管理端）与
/// AppAuthService（Manager=1 的 App 用户）签发。普通 App 用户、Open AppKey 匿名令牌、
/// 任务临时令牌（后两类无 Manager claim，且匿名/低信任来源）一律拒绝。
/// 本项目 HTTP 状态码恒为 200，拒绝同样以 body Code=401 表达（与 CustomAuthorizationFilter 一致）。
/// </summary>
public class ManagerOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.Any(em => em is AllowAnonymousAttribute))
        {
            return Task.CompletedTask;
        }
        if (context.HttpContext.User?.Claims == null
            || !string.Equals(context.HttpContext.User.FindFirst("Manager")?.Value, "true", StringComparison.Ordinal))
        {
            context.Result = new ObjectResult(new ResultModel
            {
                Code = 401,
                Message = "需要管理员权限"
            });
        }
        return Task.CompletedTask;
    }
}
