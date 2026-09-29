using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Quantum.Entities.Result;
using Quantum.Utils;

namespace Quantum.Web.Filters;

/// <summary>
/// 登录用户端点过滤器：检查已验签的登录用途令牌，拒绝外部凭据。
/// 本项目 HTTP 状态码恒为 200，拒绝同样以 body Code=401 表达（与 CustomAuthorizationFilter 一致）。
/// </summary>
public class LoggedInUserAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.Any(em => em is AllowAnonymousAttribute))
        {
            return Task.CompletedTask;
        }
        if (!JwtTokenValidator.IsLoginPrincipal(context.HttpContext.User))
        {
            context.Result = new ObjectResult(new ResultModel
            {
                Code = 401,
                Message = "需要登录身份"
            });
        }
        return Task.CompletedTask;
    }
}
