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
