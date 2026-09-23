using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;


/// <summary>
/// 环境变量（扁平化单类型；单管理员体系下整体管理员专属，Open/任务临时令牌不可访问）
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class EnvController : BaseController
{
    private readonly EnvService _envService;

    public EnvController(EnvService envService)
    {
        _envService = envService;
    }

    /// <summary>
    /// 环境变量列表数据
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    [HttpGet]
    public PageResult<EnvModel> Index([FromQuery] EnvQuery query)
    {
        return _envService.GetPage(query);
    }

    /// <summary>
    /// 导出环境变量（与列表同筛选条件、全量；每行一个变量值）
    /// </summary>
    [HttpGet("export")]
    [ActionLogFilter("导出环境变量")]
    public IActionResult Export([FromQuery] EnvQuery query)
    {
        return File(_envService.ExportValues(query), "text/plain", "envs.txt");
    }

    /// <summary>
    /// 删除指定的环境变量(按值匹配)
    /// </summary>
    /// <returns></returns>
    [HttpDelete("delete-envs")]
    public Task<bool> DeleteEnvs([FromQuery] string values)
    {
        return _envService.DeleteByValuesAsync(values);
    }

    /// <summary>
    /// 批量保存环境变量（领域动作：按名称合并，存在即更新、不存在即新增）
    /// </summary>
    /// <param name="envs"></param>
    /// <returns></returns>
    [HttpPost]
    public Task<List<string>> Save([FromBody] List<EnvModelPostModel> envs)
    {
        return _envService.Save(envs);
    }

    /// <summary>
    /// 通过环境变量id 删除
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [HttpDelete("deletes")]
    public Task<bool> Delete([FromQuery] string ids)
    {
        return _envService.DeleteByIdsAsync(ids);
    }

    /// <summary>
    /// 查询环境变量
    /// </summary>
    /// <returns></returns>
    [HttpGet("Query")]
    public List<EnvModel> Query([FromQuery] string name, [FromQuery] string key)
    {
        return _envService.Query(name, key);
    }

    /// <summary>
    /// 禁用环境变量（按值，授权访问或localhost访问）
    /// </summary>
    [HttpPut("disable-values")]
    public Task<bool> DisableValues([FromQuery] string values)
    {
        return _envService.DisableValuesAsync(values);
    }

    /// <summary>
    /// 启用环境变量（按值，授权访问或localhost访问）
    /// </summary>
    [HttpPut("enable-values")]
    public Task<bool> EnableValues([FromQuery] string values)
    {
        return _envService.EnableValuesAsync(values);
    }

    /// <summary>
    /// 禁用环境变量(id)（授权访问或localhost访问）
    /// </summary>
    [HttpPut("disable")]
    public Task<bool> Disable([FromQuery] string ids)
    {
        return _envService.DisableAsync(ids);
    }

    /// <summary>
    /// 启用环境变量(id)（授权访问或localhost访问）
    /// </summary>
    [HttpPut("enable")]
    public Task<bool> Enable([FromQuery] string ids)
    {
        return _envService.EnableAsync(ids);
    }
}
