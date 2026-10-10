using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;

namespace Quantum.Web.Controllers;

/// <summary>
/// 首次部署初始化向导（匿名）：引导创建登录账号、选择数据库、勾选自带脚本建任务。
/// 仅系统未初始化（NeedSetup）期间有效——complete/test-database 在初始化完成后一律拒绝，
/// 防止未授权改密/接管账号；已初始化后的账号变更请走登录后的 /api/SystemConfig/password。
/// </summary>
public class SetupController : BaseController
{
    readonly SetupService setupService;

    public SetupController(SetupService setupService)
    {
        this.setupService = setupService;
    }

    /// <summary>
    /// 初始化向导状态：NeedSetup=true 时附带当前库配置与自带脚本清单（向导三步的数据源）。
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public SetupStatus Status()
    {
        return setupService.GetStatus();
    }

    /// <summary>
    /// 数据库试连（向导第 2 步「测试连接」）：SQLite 校验文件名/目录可写，MySQL 实连握手。
    /// </summary>
    [HttpPost("test-database")]
    [AllowAnonymous]
    public SetupDatabaseTestResult TestDatabase([FromBody] SetupDatabaseRequest request)
    {
        return setupService.TestDatabase(request, GetUserIp());
    }

    /// <summary>
    /// 完成初始化：创建登录账号 + 保存数据库选择 + 按勾选建任务（一次性，之后本端点即关闭）。
    /// </summary>
    [HttpPost("complete")]
    [AllowAnonymous]
    public SetupCompleteResult Complete([FromBody] SetupCompleteRequest request)
    {
        return setupService.Complete(request);
    }
}
