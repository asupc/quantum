using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 日志中心：全部类型及删除、清空、统计只对登录账号开放。
/// </summary>
[CustomAuthorizationFilter]
public class LogsController : BaseController
{
    readonly LogsService _logsService;

    public LogsController(LogsService logsService)
    {
        _logsService = logsService;
    }

    /// <summary>
    /// 获取日志信息
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    [HttpGet]
    public async Task<object> Index([FromQuery] LogQuery query)
    {
        return await _logsService.GetPageAsync(query);
    }

    /// <summary>
    /// 批量删除日志
    /// </summary>
    /// <returns></returns>
    [HttpDelete]
    [LoggedInUser]
    public Task<bool> DeleteLogs([FromQuery] string ids)
    {
        return _logsService.DeleteAsync(ids);
    }

    /// <summary>
    /// 清空全部日志
    /// </summary>
    /// <returns></returns>
    [HttpDelete("clear")]
    [LoggedInUser]
    public Task<bool> ClearLogs()
    {
        return _logsService.ClearAsync();
    }

    /// <summary>
    /// 日志统计（按级别计数 + 近 N 天按日计数）
    /// </summary>
    [HttpGet("statistics")]
    [LoggedInUser]
    public Task<LogStatisticsDto> Statistics([FromQuery] int days = 7)
    {
        return _logsService.StatisticsAsync(days);
    }

    /// <summary>
    /// 获取详细日志
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpGet("details/{id}")]
    public async Task<object> GetDetails([FromRoute] string id)
    {
        return await _logsService.GetDetailsAsync(id);
    }
}
