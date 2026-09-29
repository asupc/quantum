using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using log4net;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;
using Quantum.Utils;

namespace Quantum.Web.Controllers;

/// <summary>
/// 脚本指令
/// </summary>
[CustomAuthorizationFilter]
public class TaskController : BaseController
{
    private static readonly ILog Log = LogManager.GetLogger("NETCoreRepository", typeof(TaskController));

    readonly TaskService _taskService;
    readonly IServiceScopeFactory _scopeFactory;
    public TaskController(TaskService taskService, IServiceScopeFactory scopeFactory)
    {
        this._taskService = taskService;
        this._scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 获取所有任务
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    [HttpGet]
    public Task<PageResult<TaskModel>> Index([FromQuery] TaskQuery query)
    {
        return _taskService.GetPageAsync(query);
    }

    /// <summary>
    /// 获取任务详情
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpGet("{id}")]
    public async Task<object> GetByIdAsync([FromRoute] string id)
    {
        var task = await _taskService.GetByIdAsync(id);
        if (task == null)
        {
            throw new BusinessException("任务不存在");
        }
        return task;
    }

    /// <summary>
    /// 执行脚本任务。
    /// 任务→通知接线：手动执行成功受理后广播任务通知（category=task，点按直达任务日志）。
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [HttpPost("exec-task")]
    [ActionLogFilter("执行量子脚本任务")]
    public bool ExecTask([FromBody] List<string> ids)
    {
        _taskService.ExecTask(ids);
        _ = Task.Run(async () =>
        {
            // fire-and-forget 在请求作用域释放后仍在跑：自建 scope 拿新 TaskService，勿捕获已被 Dispose 的 scoped 实例；
            // 整体 try/catch 兜住异常，避免后台 Task 的失败成为无人观察的 UnobservedTaskException
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var taskService = scope.ServiceProvider.GetRequiredService<TaskService>();
                foreach (var id in ids)
                {
                    var task = await taskService.GetByIdAsync(id);
                    if (task != null)
                    {
                        await Application.AppPushDispatcher.SendNotificationAsync(
                            "任务通知", $"任务「{task.Name}」已提交执行，执行结束将推送结果。",
                            "task", $"quantum://task/{task.Id}/log");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("任务执行通知后台投递失败", e);
            }
        });
        return true;
    }

    /// <summary>
    /// 执行脚本任务并返回每项的 RunId（一期 G2 新增端点）。
    /// 语义与 exec-task 一致（受理即返回），差别只在这里回的是**运行记录 Id**：
    /// 客户端可凭 RunId 直达执行详情/日志，不再靠「接口返回 true」推断脚本跑成功。
    /// </summary>
    [HttpPost("execute-runs")]
    [ActionLogFilter("执行量子脚本任务")]
    public async Task<List<TaskExecuteReceipt>> ExecuteRunsAsync([FromBody] List<string> ids)
    {
        var receipts = await _taskService.AcceptAndRunAsync(ids, TaskTriggerSource.Manual,
            triggerRef: GetUserId());
        return receipts.Select(n => new TaskExecuteReceipt { TaskId = n.TaskId, RunId = n.RunId }).ToList();
    }

    /// <summary>
    /// 获取脚本字符串（仅登录账号）。
    /// </summary>
    /// <param name="fileName">文件名</param>
    /// <returns></returns>
    [HttpGet("scripts")]
    [LoggedInUser]
    public Task<string> ScriptsAsync([FromQuery] string fileName)
    {
        return _taskService.GetScriptAsync(fileName);
    }

    /// <summary>
    /// 更新脚本（保存流水线：门禁+编译；失败时内容不落盘，返回 blocked/warnings/errors 三类诊断）
    /// </summary>
    [HttpPut("scripts")]
    [LoggedInUser]
    [ActionLogFilter("更新量子脚本")]
    public Task<ScriptBuildService.ScriptSaveResult> UpdateScriptsAsync([FromBody] SaveQLFile file)
    {
        return _taskService.UpdateScriptAsync(file);
    }

    /// <summary>
    /// 删除脚本
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    [HttpDelete("delete-scripts")]
    [LoggedInUser]
    [ActionLogFilter("删除脚本")]
    public bool DeleteScripts([FromQuery] string fileName)
    {
        return _taskService.DeleteScript(fileName);
    }

    /// <summary>
    /// 添加任务（仅登录账号）。
    /// </summary>
    [HttpPost]
    [LoggedInUser]
    [ActionLogFilter("添加任务")]
    public Task<bool> AddAsync([FromBody] TaskSaveModel saveModel)
    {
        return _taskService.AddAsync(saveModel);
    }

    /// <summary>
    /// 更新任务
    /// </summary>
    [HttpPut]
    [ActionLogFilter("更新任务")]
    public Task<bool> UpdateAsync([FromBody] TaskSaveModel saveModel)
    {
        return _taskService.UpdateAsync(saveModel);
    }

    /// <summary>
    /// 删除量子任务
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [ActionLogFilter("删除脚本指令")]
    [HttpDelete]
    public Task<bool> DeleteAsync([FromQuery] string ids)
    {
        return _taskService.DeleteAsync(ids);
    }

    /// <summary>
    /// 批量禁用脚本指令
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [ActionLogFilter("批量禁用脚本指令")]
    [HttpPut("disable")]
    public Task<bool> DisableAsync([FromBody] List<string> ids)
    {
        return _taskService.DisableAsync(ids);
    }

    /// <summary>
    /// 批量启用脚本指令
    /// </summary>
    [ActionLogFilter("批量启用脚本指令")]
    [HttpPut("enable")]
    public Task<bool> EnableAsync([FromBody] List<string> ids)
    {
        return _taskService.EnableAsync(ids);
    }


    /// <summary>
    /// 导出任务
    /// </summary>
    [HttpGet("export")]
    [ActionLogFilter("导出脚本指令")]
    public async Task<IActionResult> ExportAsync([FromQuery] TaskQuery query)
    {
        return File(await _taskService.ExportAsync(query), "text/xml", "tasks.json");
    }

    /// <summary>
    /// 导入任务（仅登录账号）。
    /// </summary>
    /// <returns></returns>
    [HttpPost("Import")]
    [LoggedInUser]
    public async Task<int> ImportAsync()
    {
        var form = await Request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        if (file == null)
        {
            throw new BusinessException("未选择导入文件");
        }
        return await _taskService.ImportAsync(file);
    }

    /// <summary>
    /// 手动结束多步骤任务
    /// </summary>
    /// <param name="threadId">指令步骤句柄（脚本环境变量 StepCommandTaskThreadId，非任务 Id）</param>
    /// <returns></returns>
    [HttpPost("finish/{threadId}")]
    public bool Finish(string threadId)
    {
        return _taskService.Finish(threadId);
    }

    /// <summary>
    /// 多步骤任务回到上一步
    /// </summary>
    /// <param name="threadId">指令步骤句柄（脚本环境变量 StepCommandTaskThreadId，非任务 Id）</param>
    /// <returns></returns>
    [HttpPost("redo/{threadId}")]
    public string Redo(string threadId)
    {
        return _taskService.Redo(threadId);
    }

    /// <summary>
    /// 为当前任务添加一个环境变量
    /// </summary>
    [HttpPost("add-env/{threadId}")]
    public string AddEnv([FromRoute] string threadId, [FromBody] EnvModel env)
    {
        return _taskService.AddEnv(threadId, env);
    }


    /// <summary>
    /// 获取所有的脚本文件
    /// </summary>
    [HttpGet("script-list")]
    public List<ScriptsFile> Scripts()
    {
        return _taskService.GetScriptList();
    }

    private static List<string> SplitIds(string ids)
    {
        return string.IsNullOrEmpty(ids) ? [] : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
