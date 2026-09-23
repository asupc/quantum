using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 日志中心（分型按角色过滤）：任务日志(1)/指令触发(5) 全员可见；
/// 通知(2)/操作(3)/登录(4)/外触(6)/系统(7) 仅管理员——删除/清空/统计亦为管理员专用。
/// </summary>
[CustomAuthorizationFilter]
public class LogsController : BaseController
{
    /// <summary>
    /// 仅管理员可见的日志类型（含操作日志=3）
    /// </summary>
    public static readonly HashSet<LogType> ManagerOnlyLogTypes =
    [LogType.系统日志, LogType.通知消息, LogType.登录授权, LogType.外触内执, LogType.操作日志];

    /// <summary>
    /// 全员可见的日志类型（非管理员未指定类型时强制收敛到该集合）
    /// </summary>
    private static readonly List<LogType> PublicLogTypes = [LogType.任务日志, LogType.指令触发];

    readonly LogsService _logsService;

    public LogsController(LogsService logsService)
    {
        _logsService = logsService;
    }

    /// <summary>
    /// 获取日志信息（非管理员仅任务/指令触发两类）
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    [HttpGet]
    public async Task<object> Index([FromQuery] LogQuery query)
    {
        if (!IsManager)
        {
            if (query.LogType.HasValue && ManagerOnlyLogTypes.Contains(query.LogType.Value))
            {
                return Denied();
            }
            query.LogTypes = PublicLogTypes;
        }
        return await _logsService.GetPageAsync(query);
    }

    /// <summary>
    /// 批量删除日志（管理员专用）
    /// </summary>
    /// <returns></returns>
    [HttpDelete]
    [ManagerOnly]
    public Task<bool> DeleteLogs([FromQuery] string ids)
    {
        return _logsService.DeleteAsync(ids);
    }

    /// <summary>
    /// 清空全部日志（管理员专用）
    /// </summary>
    /// <returns></returns>
    [HttpDelete("clear")]
    [ManagerOnly]
    public Task<bool> ClearLogs()
    {
        return _logsService.ClearAsync();
    }

    /// <summary>
    /// 日志统计（按级别计数 + 近 N 天按日计数，管理员专用）
    /// </summary>
    [HttpGet("statistics")]
    [ManagerOnly]
    public Task<LogStatisticsDto> Statistics([FromQuery] int days = 7)
    {
        return _logsService.StatisticsAsync(days);
    }

    /// <summary>
    /// 获取详细日志（受限类型的详情对非管理员隐藏）
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpGet("details/{id}")]
    public async Task<object> GetDetails([FromRoute] string id)
    {
        if (!IsManager)
        {
            var meta = await _logsService.GetMetaAsync(id);
            if (meta != null && ManagerOnlyLogTypes.Contains(meta.LogType))
            {
                return Denied();
            }
        }
        return await _logsService.GetDetailsAsync(id);
    }
}
