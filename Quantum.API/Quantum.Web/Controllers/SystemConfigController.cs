using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Entities.Result;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 系统设置（管理员专用；footer/install 匿名端点不受影响）
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class SystemConfigController : BaseController
{
    readonly SystemConfigService systemConfigService;

    public SystemConfigController(SystemConfigService systemConfigService)
    {
        this.systemConfigService = systemConfigService;
    }

    /// <summary>
    /// 获取系统设置（登录后才可访问；敏感字段统一脱敏返回）
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public Setting GetSetting()
    {
        return systemConfigService.GetSetting();
    }

    /// <summary>
    /// 修改登录密码
    /// </summary>
    [HttpPost("password")]
    [ActionLogFilter("修改登录密码")]
    public async Task<bool> UpdatePassword([FromBody] UpdatePasswordRequest request)
    {
        return await systemConfigService.UpdatePassword(request);
    }

    /// <summary>
    /// 修改系统设置
    /// </summary>
    [HttpPut]
    [ActionLogFilter("修改系统设置")]
    public ResultModel<bool> Update([FromBody] Setting setting)
    {
        // §5-8：HTTP 恒 200 信封不变（Code=200/Data=true，前端成功路径不受影响），
        // 仅在启动期三件套（可信代理/跨域白名单/Swagger）变更时把提示放进 Message，供读取信封的客户端展示。
        var ok = systemConfigService.Update(setting, out var restartRequired);
        return new ResultModel<bool>
        {
            Code = 200,
            Data = ok,
            Message = restartRequired
                ? "保存成功，可信代理/跨域白名单/Swagger 属启动期配置，需重启服务后生效"
                : "Success"
        };
    }

    /// <summary>
    /// 获取自定义页脚信息
    /// </summary>
    /// <returns></returns>
    [HttpGet("footer"), AllowAnonymous]
    public string GetFooter()
    {
        return systemConfigService.GetFooter();
    }

    /// <summary>
    /// 初始化系统
    /// </summary>
    /// <param name="setting"></param>
    /// <returns></returns>
    [HttpPost("install"), AllowAnonymous]
    public bool Install([FromBody] Setting setting)
    {
        return systemConfigService.Install(setting);
    }

    /// <summary>
    /// 收缩数据库（SQLite VACUUM；MySQL 优化高写入表）
    /// </summary>
    [HttpPost("database-shrink")]
    [ActionLogFilter("收缩数据库")]
    public async Task<bool> DatabaseShrink()
    {
        return await systemConfigService.DatabaseShrink();
    }
}
