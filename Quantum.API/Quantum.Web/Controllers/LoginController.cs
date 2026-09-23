using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.Config;
using Quantum.Utils;

namespace Quantum.Web.Controllers;

public class LoginController : BaseController
{
    readonly LoginService loginService;
    readonly QrLoginService qrLoginService;

    public LoginController(LoginService loginService, QrLoginService qrLoginService)
    {
        this.loginService = loginService;
        this.qrLoginService = qrLoginService;
    }

    /// <summary>
    /// 用户登录
    /// </summary>
    [AllowAnonymous]
    [HttpPost]
    //[ActionLogFilter("用户登录")]
    public string Login([FromBody] Setting login)
    {
        return loginService.Login(login, GetUserIp());
    }

    /// <summary>
    /// 创建扫码登录票据（匿名，Web 登录页展示二维码；票据 2 分钟有效）
    /// </summary>
    /// <returns></returns>
    [HttpPost("qr-create")]
    [AllowAnonymous]
    public object QrCreate()
    {
        var (ticket, expireAt) = qrLoginService.Create();
        return new { Ticket = ticket, ExpireAt = expireAt };
    }

    /// <summary>
    /// 轮询扫码状态（匿名）：status 0=待扫码 1=已授权（Token 一次性返回） 2=过期
    /// </summary>
    /// <returns></returns>
    [HttpGet("qr-status")]
    [AllowAnonymous]
    public object QrStatus([FromQuery] string ticket)
    {
        var (status, token) = qrLoginService.Poll(ticket);
        return new { Status = (int)status, Token = token };
    }

    /// <summary>
    /// 获取量子版本号
    /// </summary>
    /// <returns></returns>
    [HttpGet("Version")]
    [AllowAnonymous]
    public Tuple<string, DateTime> Version()
    {
        return new Tuple<string, DateTime>(Extends.Version, Extends.VersionTime);
    }
}
