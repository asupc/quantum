using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 快捷回复
/// </summary>
[CustomAuthorizationFilter]
public class CommandController : BaseController
{
    readonly CommandService _commandService;

    public CommandController(CommandService commandService)
    {
        _commandService = commandService;
    }

    /// <summary>
    /// 获取所有快捷回复
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    [HttpGet]
    public Task<PageResult<CommandModel>> Get([FromQuery] CommandQuery query)
    {
        return Task.FromResult(_commandService.GetPage(query));
    }


    /// <summary>
    /// 删除快捷回复
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [ActionLogFilter("删除快捷回复")]
    [HttpDelete]
    public Task<bool> DeleteAsync([FromQuery] string ids)
    {
        return _commandService.DeleteAsync(ids);
    }

    /// <summary>
    /// 添加快捷回复
    /// </summary>
    /// <param name="command"></param>
    /// <returns></returns>
    [ActionLogFilter("添加快捷回复")]
    [HttpPost]
    public Task<bool> AddAsync([FromBody] CommandModel command)
    {
        return _commandService.AddAsync(command);
    }

    /// <summary>
    /// 修改快捷回复
    /// </summary>
    /// <param name="command"></param>
    /// <returns></returns>
    [ActionLogFilter("修改快捷回复")]
    [HttpPut]
    public Task<bool> UpdateAsync([FromBody] CommandModel command)
    {
        return _commandService.UpdateAsync(command);
    }

    /// <summary>
    /// 禁用快捷回复
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [HttpPut("disable")]
    public Task<bool> DisableAsync([FromBody] List<string> ids)
    {
        return _commandService.DisableAsync(ids);
    }

    /// <summary>
    /// 启用快捷回复
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [HttpPut("enable")]
    public Task<bool> EnableAsync([FromBody] List<string> ids)
    {
        return _commandService.EnableAsync(ids);
    }
}
