using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;


public class OpenController : BaseController
{
    readonly OpenAuthService openAuthService;

    public OpenController(OpenAuthService openAuthService)
    {
        this.openAuthService = openAuthService;
    }

    /// <summary>
    /// 通过AppKey获取授权Token（Body 传参，推荐）：AppKey 不进 URL，避免被代理/访问日志留存
    /// </summary>
    [HttpPost("auth")]
    [AllowAnonymous]
    public string AuthByBody([FromBody] OpenAuthRequest request)
    {
        return openAuthService.Auth(request?.AppKey ?? string.Empty, GetUserIp());
    }

    /// <summary>
    /// 通过AppKey获取授权Token，该Token有效期为10分钟（URL 传参旧通道，兼容存量对接方，建议迁移 POST /auth）
    /// </summary>
    /// <param name="AppKey"></param>
    /// <returns></returns>
    [HttpGet("{AppKey}")]
    [AllowAnonymous]
    [ActionLogFilter("AppKey授权")]
    public string Auth([FromRoute] string AppKey)
    {
        return openAuthService.Auth(AppKey, GetUserIp());
    }
}

/// <summary>
/// AppKey 授权入参（POST /api/Open/auth）
/// </summary>
public class OpenAuthRequest
{
    public string AppKey { get; set; }
}
