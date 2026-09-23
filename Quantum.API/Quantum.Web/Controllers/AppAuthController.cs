using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// App（安卓客户端）认证接口：login / refresh / logout。
/// 响应走统一 {Code, Message, Data} 包装；登录与刷新匿名（刷新令牌即凭证）。
/// </summary>
[Route("api/AppAuth")]
public class AppAuthController : BaseController
{
    private readonly AppAuthService _appAuthService;

    public AppAuthController(AppAuthService appAuthService)
    {
        _appAuthService = appAuthService;
    }

    /// <summary>
    /// App 登录：账号密码 + 设备信息，返回 accessToken（2小时）与 refreshToken（滑动窗口，默认 365 天，见 Setting.RefreshTokenDays）
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<object> Login([FromBody] AppLoginRequest request)
    {
        var (accessToken, refreshToken, refreshExpiresAt) = await _appAuthService.LoginAsync(
            request.UserName, request.Password, request.DeviceId, request.DeviceName, request.Platform, GetUserIp());
        return new
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            RefreshExpiresAt = refreshExpiresAt
        };
    }

    /// <summary>
    /// 刷新令牌：携带未过期的 refreshToken 换取新令牌对（旧 refreshToken 立即作废）
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<object> Refresh([FromBody] AppRefreshRequest request)
    {
        var (accessToken, refreshToken, refreshExpiresAt) = await _appAuthService.RefreshAsync(
            request.RefreshToken, request.DeviceId);
        return new
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            RefreshExpiresAt = refreshExpiresAt
        };
    }

    /// <summary>
    /// 登出：吊销 refreshToken
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public Task<bool> Logout([FromBody] AppRefreshRequest request)
    {
        return _appAuthService.LogoutAsync(request.RefreshToken);
    }
}

/// <summary>
/// App 登录入参
/// </summary>
public class AppLoginRequest
{
    public string UserName { get; set; }

    public string Password { get; set; }

    /// <summary>
    /// 客户端设备唯一标识（首次安装生成）
    /// </summary>
    public string DeviceId { get; set; }

    /// <summary>
    /// 设备型号（可选）
    /// </summary>
    public string DeviceName { get; set; }

    /// <summary>
    /// 平台（android/ios/harmony，可选）
    /// </summary>
    public string Platform { get; set; }
}

/// <summary>
/// App 刷新/登出入参
/// </summary>
public class AppRefreshRequest
{
    public string RefreshToken { get; set; }

    public string DeviceId { get; set; }
}
