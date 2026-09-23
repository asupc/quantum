using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 外触内执服务：配置的增删改查、触发请求的密钥/启用/白名单校验与执行入口。
/// 控制器只做参数绑定（含取调用方 IP 与请求参数）与结果返回，业务与数据访问全部收敛在本类。
/// </summary>
public class OpenTriggerTaskService
{
    readonly IQuantumDbContext _dbContext;

    public OpenTriggerTaskService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 触发执行：按密钥与请求方法定位配置并校验（不存在/未启用/IP 不在白名单均抛业务异常），
    /// 校验通过后 fire-and-forget 执行，立即返回"执行成功"。
    /// </summary>
    /// <param name="secret">配置安全密钥</param>
    /// <param name="httpMethod">GET/POST</param>
    /// <param name="ip">调用方IP</param>
    /// <param name="data">GET 为查询参数字典，POST 为请求体</param>
    public string Trigger(string secret, string httpMethod, string ip, object data)
    {
        var triggerTask = CacheManager.Get<OpenTriggerTask>().FirstOrDefault(n => n.Secret == secret && n.HttpMethod == httpMethod);
        if (triggerTask == null)
        {
            throw new BusinessException("未找到相关配置，或未启用");
        }
        if (!triggerTask.Enable)
        {
            throw new BusinessException("任务未启用");
        }
        if (!IsIpAllowed(triggerTask.Whitelist, ip))
        {
            throw new BusinessException($"当前IP:{ip}，不在白名单内");
        }
        Task.Run(() =>
        {
            Run(triggerTask, data, ip);
        });

        return "执行成功";
    }

    /// <summary>
    /// 白名单精确匹配：支持逗号/分号/空白分隔多个 IP。
    /// 原实现用字符串子串 Contains，1.2.3.4 可命中 11.2.3.44，白名单形同虚设。
    /// </summary>
    private static bool IsIpAllowed(string whitelist, string ip)
    {
        if (string.IsNullOrEmpty(whitelist))
        {
            return false;
        }
        var allowed = whitelist.Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Contains(ip, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 添加或者更新外触内执配置
    /// </summary>
    public async Task<bool> AddAsync(OpenTriggerTask openTriggerTask)
    {
        // 配置即执行权：SrciptFile 必须过脚本白名单（scripts/quantum 根内 + .js/.py），
        // 与任务保存同款校验，防止借外触配置把执行入口指向任意落盘脚本
        TaskService.ValidateScriptFileName(openTriggerTask?.SrciptFile);
        var task = await _dbContext.OpenTriggerTasks.SingleOrDefaultAsync(n => n.Id == openTriggerTask.Id);
        if (task == null)
        {
            if (await _dbContext.OpenTriggerTasks.AnyAsync(n => n.Secret == openTriggerTask.Secret))
            {
                throw new BusinessException("Secret信息重复，请修改后提交。");
            }
            openTriggerTask.Id = openTriggerTask.NewId();
            _dbContext.OpenTriggerTasks.Add(openTriggerTask);
            await _dbContext.SaveChangesAsync();
        }
        else
        {
            if (await _dbContext.OpenTriggerTasks.AnyAsync(n => n.Secret == openTriggerTask.Secret && n.Id != openTriggerTask.Id))
            {
                throw new BusinessException("Secret信息重复，请修改后提交。");
            }
            // 更新走「查库跟踪实体 + 套字段」：请求体实例与被跟踪实例同键，
            // 直接 Update 会抛 EF「另一实例已被跟踪」异常，导致更新功能整体不可用
            task.Name = openTriggerTask.Name;
            task.Secret = openTriggerTask.Secret;
            task.SrciptFile = openTriggerTask.SrciptFile;
            task.HttpMethod = openTriggerTask.HttpMethod;
            task.Envs = openTriggerTask.Envs;
            task.Enable = openTriggerTask.Enable;
            task.Whitelist = openTriggerTask.Whitelist;
            task.EnablePush = openTriggerTask.EnablePush;
            task.EnableProxy = openTriggerTask.EnableProxy;
            await _dbContext.UpdateAsync(task);
        }
        return true;
    }

    /// <summary>
    /// 删除外触内执配置
    /// </summary>
    public async Task<bool> DeleteAsync(string ids)
    {
        var idList = string.IsNullOrEmpty(ids) ? [] : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        await _dbContext.DeleteByIdsAsync<OpenTriggerTask>(idList);
        return true;
    }

    /// <summary>
    /// 获取外触内执配置信息
    /// </summary>
    public async Task<PageResult<OpenTriggerTask>> GetAsync(BaseQuery baseQuery)
    {
        return new PageResult<OpenTriggerTask>
        {
            Data = await _dbContext.OpenTriggerTasks.OrderByDescending(n => n.Enable).ThenBy(n => n.Id).Skip(baseQuery.Skip).Take(baseQuery.PageSize).ToListAsync(),
            TotalCount = await _dbContext.OpenTriggerTasks.CountAsync(),
            Page = baseQuery.PageIndex,
            PageSize = baseQuery.PageSize
        };
    }

    /// <summary>
    /// 批量禁用外触内执指令
    /// </summary>
    public async Task DisableAsync(List<string> ids)
    {
        await SetEnableAsync(ids, false);
    }

    /// <summary>
    /// 批量启用外触内执指令
    /// </summary>
    public async Task EnableAsync(List<string> ids)
    {
        await SetEnableAsync(ids, true);
    }

    /// <summary>
    /// §2-3：Enable/Disable 不再就地改 CacheManager 共享实例——改跟踪查询取上下文实例（同 Id 已跟踪时 EF 返回同一实例，避免二次 Attach 冲突）、改字段、SaveChanges，提交后 Refresh 失效缓存。
    /// </summary>
    private async Task SetEnableAsync(List<string> ids, bool enable)
    {
        if (ids == null || ids.Count == 0)
        {
            return;
        }
        var rows = await _dbContext.OpenTriggerTasks.Where(n => ids.Contains(n.Id)).ToListAsync();
        if (rows.Count == 0)
        {
            return;
        }
        foreach (var row in rows)
        {
            row.Enable = enable;
        }
        await _dbContext.SaveChangesAsync();
        CacheManager.Refresh<OpenTriggerTask>();
    }

    /// <summary>
    /// 执行任务（async void 会让未知异常直接打崩进程，改为 async Task + 顶层兜底）
    /// </summary>
    public async Task Run(OpenTriggerTask task, object data, string ip)
    {
        try
        {
            await RunCore(task, data, ip);
        }
        catch (Exception e)
        {
            Console.WriteLine($"外触内执任务[{task.Name}]执行异常：{e}");
        }
    }

    private async Task RunCore(OpenTriggerTask task, object data, string ip)
    {
        var jsFile = $"./scripts/quantum/{task.SrciptFile}";

        FileInfo file = new(jsFile);

        DateTime startTime = DateTime.Now;

        var logModel = new LogModel
        {
            CreateTime = startTime,
            Id = Guid.NewGuid().ToString(),
            LogType = LogType.外触内执,
            Operator = "System",
            Success = true,
            Title = "外触内置",
            UserIP = ip,
            DirectoryName = TaskExcuteService.LogDirNameFrom(task.SrciptFile),
            LogPath = $"{startTime:yyyyMMddHHmmssfff}.log"
        };

        // serverAddres/临时令牌随进程内直调消失；代理/推送开关改由 ctx.EnableProxy/ctx.EnablePush 表达
        List<EnvModel> envModels = [
                        new EnvModel
                        {
                            Name = "OpenTriggerTask-Datas",
                            Value = JsonConvert.SerializeObject(data)
                        },
                        new EnvModel
                        {
                            Name = "OpenTriggerTask-Tasks",
                            Value = JsonConvert.SerializeObject(task)
                        }];

        TaskCommandStep commandTask = new TaskCommandStep
        {
            CreateTime = startTime,
            Envs = envModels,
            HasChildTask = false,
            IsChildTask = false,
            // 外触无 WaitTime 配置，默认 24 小时强制结束（防挂死任务永久占用执行线程）
            ForceEndTime = DateTime.Now.AddMinutes(60 * 24),
            Task = new TaskModel
            {
                FileName = task.SrciptFile,
                Name = task.Name,
                Manager = false,
                EnableProxy = task.EnableProxy,
                EnablePush = task.EnablePush
            }
        };
        await commandTask.Run();
        LogServiceHelper.Logs.Enqueue(logModel);
    }
}
