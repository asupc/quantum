using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.Result;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 枚举接口
/// </summary>
[Route("api/[controller]")]
[ResultFilter]
[CustomAuthorizationFilter]
public class EnumController : ControllerBase
{
    readonly EnumService enumService;

    public EnumController(EnumService enumService)
    {
        this.enumService = enumService;
    }

    /// <summary>
    /// 获取所有枚举信息
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public Dictionary<string, List<EnumKeyValue>> Enums()
    {
        return enumService.Enums();
    }
}
