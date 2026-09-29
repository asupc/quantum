using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Quantum.Entities.Result;
using Quantum.Utils;

namespace Quantum.Web.Filters;

/// <summary>
/// 要求调用者是当前账号的真实登录主体。
///
/// 为什么需要它：<see cref="CustomAuthorizationFilter"/> 只验签不认身份，而 Open AppKey 换取的
/// 10 分钟令牌同样是合法签名（"Name" 为固定标记），因此须正向判定登录用途。
///
/// 用于执行记录/运行详情这类会暴露任务名、脚本路径快照与失败摘要的读接口。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RealPrincipalAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (!JwtTokenValidator.IsLoginPrincipal(http.User))
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
