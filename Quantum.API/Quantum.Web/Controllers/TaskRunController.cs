using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;
using Quantum.Utils;

namespace Quantum.Web.Controllers;

/// <summary>
/// 任务执行历史与失败策略（一期 G2/G3）。
/// 权限口径：列表与详情按任务正向 Manager claim 过滤（非 Manager 拿不到 Manager 任务的执行记录）；
/// 策略写入与手动重新执行是管理动作，一律 [ManagerOnly]。Open/匿名令牌不开放运行历史。
/// </summary>
[CustomAuthorizationFilter]
[RealPrincipal]
public class TaskRunController : BaseController
{
    readonly TaskRunService _runService;
    readonly TaskService _taskService;

    public TaskRunController(TaskRunService runService, TaskService taskService)
    {
        _runService = runService;
        _taskService = taskService;
    }

    /// <summary>执行历史分页（排序 UTC+Id 倒序，分页上限 1~100）</summary>
    [HttpGet]
    public async Task<PageResult<TaskRunRow>> Index([FromQuery] TaskRunQuery query)
    {
        var (rows, total) = await _runService.GetPageAsync(query.TaskId, query.Status, query.Page,
            query.PageSize, IsManager, query.Days);
        return new PageResult<TaskRunRow>
        {
            Data = rows.Select(TaskRunRow.From).ToList(),
            TotalCount = total,
            Page = Math.Max(1, query.Page),
            PageSize = Math.Clamp(query.PageSize, 1, TaskRunService.MaxPageSize)
        };
    }

    /// <summary>
    /// 单条执行详情：含同一根执行的尝试时间轴与日志可达性。
    /// 越权与不存在一律返回「执行记录不存在」，不区分（避免用详情接口探测 RunId）。
    /// </summary>
    [HttpGet("{runId}")]
    public async Task<TaskRunDetail> Detail([FromRoute] string runId)
    {
        var run = await _runService.GetAsync(runId, IsManager);
        if (run == null)
        {
            throw new BusinessException("执行记录不存在");
        }

        var chain = await _runService.GetChainAsync(run.RootRunId, IsManager);
        return new TaskRunDetail
        {
            Run = TaskRunRow.From(run),
            Attempts = chain.Select(TaskRunRow.From).ToList(),
            LogId = run.LogId,
            LogAvailable = await _runService.HasLogAsync(run.LogId)
        };
    }

    /// <summary>
    /// 手动重新执行（管理员）：产生**新的**根执行 Id，不复用旧执行链、也不计入旧链的重试次数；
    /// 留操作日志。被拒绝的脚本仍可手动重跑（例如已修好脚本后补跑一次）。
    /// </summary>
    [HttpPost("{runId}/retry")]
    [ManagerOnly]
    [ActionLogFilter("手动重新执行任务")]
    public async Task<TaskExecuteReceipt> RetryAsync([FromRoute] string runId)
    {
        var run = await _runService.GetAsync(runId, true);
        if (run == null)
        {
            throw new BusinessException("执行记录不存在");
        }

        if (string.IsNullOrEmpty(run.TaskId))
        {
            throw new BusinessException("该执行没有对应的持久任务，无法重新执行");
        }

        var task = await _taskService.GetByIdAsync(run.TaskId);
        if (task == null)
        {
            throw new BusinessException("任务已删除，无法重新执行");
        }

        var receipts = await _taskService.AcceptAndRunAsync([run.TaskId], TaskTriggerSource.Manual,
            $"manual-retry:{run.Id}");
        var receipt = receipts.FirstOrDefault();
        if (receipt == null)
        {
            throw new BusinessException("受理失败，请确认任务状态后重试");
        }

        return new TaskExecuteReceipt { TaskId = receipt.TaskId, RunId = receipt.RunId };
    }

    /// <summary>读失败策略（无配置即返回全默认值：重试 0 次、策略未启用）</summary>
    [HttpGet("policy/{taskId}")]
    public async Task<TaskFailurePolicyDto> GetPolicy([FromRoute] string taskId)
    {
        var policy = await _runService.GetPolicyAsync(taskId);
        return new TaskFailurePolicyDto
        {
            TaskId = taskId,
            RetryCount = policy.RetryCount,
            BackoffSeconds = policy.BackoffSeconds,
            AlertAfterConsecutiveFailures = policy.AlertAfterConsecutiveFailures,
            SendRecovery = policy.SendRecovery,
            CooldownMinutes = policy.CooldownMinutes,
            Enabled = policy.Enabled
        };
    }

    /// <summary>
    /// 写失败策略（管理员）。所有限值在此处服务端二次 clamp，不信任 UI。
    /// 注意：策略里的告警/路由细节不对非 Manager 开放；非 Manager 只能读自己可见任务的执行记录。
    /// </summary>
    [HttpPut("policy/{taskId}")]
    [ManagerOnly]
    [ActionLogFilter("更新任务失败策略")]
    public async Task<TaskFailurePolicyDto> SavePolicy([FromRoute] string taskId,
        [FromBody] TaskFailurePolicyDto dto)
    {
        if (dto == null)
        {
            throw new BusinessException("缺少策略内容");
        }

        var task = await _taskService.GetByIdAsync(taskId);
        if (task == null)
        {
            throw new BusinessException("任务不存在");
        }

        var saved = await _runService.SavePolicyAsync(taskId, new TaskFailurePolicyModel
        {
            RetryCount = dto.RetryCount,
            BackoffSeconds = dto.BackoffSeconds,
            AlertAfterConsecutiveFailures = dto.AlertAfterConsecutiveFailures,
            SendRecovery = dto.SendRecovery,
            CooldownMinutes = dto.CooldownMinutes,
            Enabled = dto.Enabled
        }, GetUserId());

        return new TaskFailurePolicyDto
        {
            TaskId = taskId,
            RetryCount = saved.RetryCount,
            BackoffSeconds = saved.BackoffSeconds,
            AlertAfterConsecutiveFailures = saved.AlertAfterConsecutiveFailures,
            SendRecovery = saved.SendRecovery,
            CooldownMinutes = saved.CooldownMinutes,
            Enabled = saved.Enabled
        };
    }
}
