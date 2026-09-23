using log4net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Quantum.Entities.Result;
using Quantum.Utils;

namespace Quantum.Web.Filters;

public class CustomAuthorizationFilter : Attribute, IAsyncAuthorizationFilter
{

    private readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(CustomAuthorizationFilter));

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.Any(em => em is AllowAnonymousAttribute))
        {
            return Task.CompletedTask;
        }

        var auth = context.HttpContext.Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(auth))
        {
            context.Result = new ObjectResult(new ResultModel
            {
                Code = 401,
                Message = "身份认证失败-01"
            });
            return Task.CompletedTask;
        }

        var token = auth.ToString().Replace("Bearer ", "");

        if (JwtTokenValidator.Validate(token) == null)
        {
            context.Result = new ObjectResult(new ResultModel
            {
                Code = 401,
                Message = "Token验证失败"
            });
        }
        return Task.CompletedTask;
    }
}
