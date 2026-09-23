using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Quantum.Entities.Result;
using Quantum.Web.Filters;
using Quantum.Utils;

namespace Quantum.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableCors("Any")]
[ResultFilter]
public class BaseController : ControllerBase
{
    protected readonly List<string> _allowExtends;
    protected readonly List<string> _excludeDicretory;
    public BaseController()
    {
        _allowExtends = new List<string>
        {
            ".js",
            ".ts",
            ".py",
            ".bat",
            ".sh",
            ".json"
        };
        _excludeDicretory = new List<string>
        {
            "node_modules",
            ".git"
        };
    }
    /// <summary>
    /// 当前令牌是否管理员（正向 "Manager" claim；任务列表过滤/Env 限权/日志分型按此收敛）。
    /// </summary>
    protected bool IsManager => HttpContext.IsManager();

    /// <summary>
    /// 非 ManagerOnly 语义的拒绝响应：本项目 HTTP 恒 200，与过滤器同款 body Code=401 表达。
    /// </summary>
    protected static ObjectResult Denied()
    {
        return new ObjectResult(new ResultModel
        {
            Code = 401,
            Message = "需要管理员权限"
        });
    }

    protected string GetUserId()
    {
        return HttpContext.GetUserId();
    }

    /// <summary>
    /// 当前登录设备 Id（JWT "DeviceId" 声明，App 令牌专有）。
    /// </summary>
    protected string GetUserDeviceId()
    {
        return HttpContext.GetUserDeviceId();
    }

    protected string GetUserIp()
    {
        // 与 HttpContextExtension.GetUserIp 同源：不直读 X-Forwarded-For（详见该处说明）
        return HttpContext.GetUserIp();
    }
}