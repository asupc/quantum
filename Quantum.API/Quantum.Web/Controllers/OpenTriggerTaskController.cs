using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 外触内执
/// </summary>
[Route("api/[controller]")]
[CustomAuthorizationFilter]
// 配置的增删改查/启停全部管理员专用：配置即执行权（SrciptFile 指向 scripts/quantum 内任意脚本，
// 配合匿名 Trigger 端点可反复触发执行），与 Task 脚本链路同级敏感，普通 App 用户/Open 匿名令牌不得可达。
// 匿名触发端点（GET/POST {secret}）标有 [AllowAnonymous]，不受本过滤器影响。
[ManagerOnly]
public class OpenTriggerTaskController : BaseController
{
    OpenTriggerTaskService openTriggerTaskService;

    public OpenTriggerTaskController(OpenTriggerTaskService openTriggerTaskService)
    {
        this.openTriggerTaskService = openTriggerTaskService;
    }

    /// <summary>
    /// 触发执行
    /// </summary>
    /// <param name="secret">配置安全密钥</param>
    /// <returns></returns>
    [HttpGet("{secret}"), AllowAnonymous]
    public string Trigger([FromRoute] string secret)
    {
        var ip = GetUserIp();
        var data = HttpContext.Request.Query.ToList();

        Dictionary<string, string> dic = [];
        foreach (var item in data)
        {
            dic.Add(item.Key, item.Value.FirstOrDefault());
        }
        return openTriggerTaskService.Trigger(secret, "GET", ip, dic);
    }

    /// <summary>
    /// 触发执行
    /// </summary>
    /// <param name="secret">配置安全密钥</param>
    /// <param name="data">自定义数据</param>
    /// <returns></returns>
    [HttpPost("{secret}"), AllowAnonymous]
    public string Trigger([FromRoute] string secret, [FromBody] object data)
    {
        return openTriggerTaskService.Trigger(secret, "POST", GetUserIp(), data);
    }

    /// <summary>
    /// 添加或者更新外触内执配置
    /// </summary>
    [HttpPost]
    [ActionLogFilter("添加或者更新外触内执配置")]
    public Task<bool> AddAsync([FromBody] OpenTriggerTask openTriggerTask)
    {
        return openTriggerTaskService.AddAsync(openTriggerTask);
    }

    /// <summary>
    /// 删除外触内执配置（ids 逗号分隔，与 Task 批量删除约定一致）
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [ActionLogFilter("删除外触内执配置")]
    [HttpDelete]
    public Task<bool> DeleteAsync([FromQuery] string ids)
    {
        return openTriggerTaskService.DeleteAsync(ids);
    }

    /// <summary>
    /// 获取外触内执配置信息
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public Task<PageResult<OpenTriggerTask>> GetAsync([FromQuery] BaseQuery baseQuery)
    {
        return openTriggerTaskService.GetAsync(baseQuery);
    }


    /// <summary>
    /// 批量禁用外触内执指令
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [ActionLogFilter("批量禁用外触内执指令")]
    [HttpPut("disable")]
    public Task DisableAsync([FromBody] List<string> ids)
    {
        return openTriggerTaskService.DisableAsync(ids);
    }

    /// <summary>
    /// 批量启用外触内执指令
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [ActionLogFilter("批量启用外触内执指令")]
    [HttpPut("enable")]
    public Task EnableAsync([FromBody] List<string> ids)
    {
        return openTriggerTaskService.EnableAsync(ids);
    }
}
