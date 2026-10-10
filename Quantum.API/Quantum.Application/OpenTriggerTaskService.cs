using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        // 请求体防护必须在派发前做：RunCore 会把请求体 JsonConvert.SerializeObject 后塞进环境变量，
        // 而 Newtonsoft 写入侧没有深度上限（MaxDepth 只约束读取），深嵌套体会在后台线程递归爆栈——
        // StackOverflowException 不可捕获，直接打死进程。本端点匿名可达（仅 secret + IP 白名单）。
        ValidatePayload(data);
        // 用 async lambda 命中 Task.Run(Func<Task>) 重载：语句体 Action 重载会丢弃 Run 返回的 Task，
        // 异常既无人观察也静默消失；这里再兜一层日志，绝不让异常落到线程池未处理。
        _ = Task.Run(async () =>
        {
            try
            {
                await Run(triggerTask, data, ip);
            }
            catch (Exception e)
            {
                Console.WriteLine($"外触内执任务[{triggerTask.Name}]后台执行异常：{e}");
            }
        });

        return "执行成功";
    }

    /// <summary>
    /// 外触请求体嵌套深度上限（JSON 层级）：正常 webhook 载荷远低于此值。
    /// 注意计数口径是 JSON 层级而非 JToken 节点层：JObject 的 Children() 是 JProperty，
    /// 一个 JSON 层级会跨两个 JToken 节点，故按节点计会让有效上限减半。
    /// </summary>
    internal const int MaxPayloadDepth = 64;

    /// <summary>外触请求体节点总数上限：宽结构（上万个兄弟节点）虽不爆栈，但同样放大序列化与内存占用。</summary>
    internal const int MaxPayloadNodes = 10_000;

    /// <summary>
    /// 外触请求体序列化长度上限（字符）：节点数拦不住「单个巨型叶子」，实测 20MB 单叶载荷
    /// （节点数仅 2）能被完整序列化进环境变量，只有 Kestrel 的 30MB 请求体默认值兜底——
    /// 那是基础设施默认值，不是本端点的防护。这里显式设限，与环境变量/日志的体积预期一致。
    /// </summary>
    internal const int MaxPayloadChars = 256 * 1024;

    /// <summary>
    /// 请求体防护（匿名端点，必须早于序列化）：迭代遍历 JToken 树统计**容器嵌套层数**与节点数，
    /// 越界即拒绝；并核对序列化长度。用显式栈而非递归——本方法的职责就是防爆栈，自身更不能递归。
    /// 非 JToken 入参（GET 查询参数字典由控制器自建、深度固定）无需遍历。
    /// </summary>
    internal static void ValidatePayload(object data)
    {
        if (data is not JToken root)
        {
            return;
        }
        var nodes = 0;
        var stack = new Stack<(JToken Token, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (token, depth) = stack.Pop();
            nodes++;
            if (depth > MaxPayloadDepth)
            {
                throw new BusinessException($"请求体嵌套过深（超过 {MaxPayloadDepth} 层），已拒绝执行！");
            }
            if (nodes > MaxPayloadNodes)
            {
                throw new BusinessException($"请求体节点数过多（超过 {MaxPayloadNodes} 个），已拒绝执行！");
            }
            foreach (var child in token.Children())
            {
                // 只按「容器嵌套」计层（与 JSON 层级口径一致）：属性节点（JProperty）与标量叶子
                // 不单独计一层，否则 {"a":{"a":…}} 这类对象「N 层 JSON」会算成 N+1 层（叶子那层），
                // 上限被凭空削掉一层；而属性节点不排除则直接翻倍
                var childDepth = child is JProperty || child is not JContainer ? depth : depth + 1;
                stack.Push((child, childDepth));
            }
        }
        // 深度与节点数已受限，此处序列化不会爆栈；长度用于拦住巨型叶子的体积放大
        var serialized = JsonConvert.SerializeObject(data);
        if (serialized.Length > MaxPayloadChars)
        {
            throw new BusinessException($"请求体过大（{serialized.Length} 字符，上限 {MaxPayloadChars} 字符），已拒绝执行！");
        }
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
            // 落库后必须失效缓存：Trigger 只读 CacheManager，不刷新的话新建配置在下一次
            // 任意刷新（如启停）之前形同不存在——实测「保存成功」后立即触发仍报「未找到相关配置」
            CacheManager.Refresh<OpenTriggerTask>();
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
            // 与新建同源：更新后的 Secret/Enable/Whitelist 也是靠缓存生效的，不刷新则改动要等下次偶然刷新
            CacheManager.Refresh<OpenTriggerTask>();
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
        // 删除同样要让 Trigger 立刻看不到：缓存里残留的行会让已删配置继续可触发
        CacheManager.Refresh<OpenTriggerTask>();
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
                EnableProxy = task.EnableProxy,
                EnablePush = task.EnablePush
            }
        };
        // 外触走运行记录链路（G2 入口矩阵）：真实终态 + t_log 同事务落库；本入口不参与自动重试与告警
        await TaskRunRecorder.RunStepAsync(commandTask, TaskTriggerSource.OpenTrigger, ip, LogType.外触内执);
    }
}
