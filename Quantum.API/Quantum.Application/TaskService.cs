using Microsoft.EntityFrameworkCore;
using log4net;
using Newtonsoft.Json;
using Quartz;
using Quantum.Utils;
using System.Text.RegularExpressions;
using Quantum.Entities.Model;
using Quantum.Entities.DTOs;
using Quantum.Data;

namespace Quantum.Application;

/// <summary>
/// 量子脚本任务服务：任务 CRUD/启停/导入导出、脚本文件读写与脚本目录遍历、
/// 多步骤任务的运行时控制（结束/回退/加环境变量），以及任务执行（ExecTask/ExecTaskAsync）。
/// 控制器只做参数绑定与结果返回，业务与数据访问全部收敛在本类。
/// </summary>
public class TaskService
{
    private static readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(TaskService));

    readonly IQuantumDbContext _dbContext;
    readonly AppMessageService _appMessageService;
    readonly ScriptVersionService _scriptVersionService;

    /// <summary>会话名长度上限（与迁移列型 varchar(100) 对齐）。</summary>
    internal const int MaxSessionNameLength = 100;

    // 脚本树仅展示可执行/可编辑的 .cs 源码任务（执行白名单=ScriptBuildService.ScriptFileExtension）
    readonly List<string> _allowExtends =
    [
        ".cs"
    ];

    readonly List<string> _excludeDicretory = new()
    {
        "node_modules",
        ".git",
        // AI 试运行的影子文件目录（2026-09-20）：只在该次试运行期间存在，不展示也不允许绑定
        AgentTestRunService.StagingDir
    };

    public TaskService(IQuantumDbContext dbContext, AppMessageService appMessageService, ScriptVersionService scriptVersionService)
    {
        _dbContext = dbContext;
        _appMessageService = appMessageService;
        _scriptVersionService = scriptVersionService;
    }

    /// <summary>
    /// 批量执行任务（fire-and-forget：立即返回，后台执行）
    /// </summary>
    public void ExecTask(List<string> ids)
    {
        // fire-and-forget 保留（调用方多处依赖立即返回），但显式观察后台异常，避免成为无人处理的 UnobservedTaskException
        _ = ExecTaskAsync(ids).ContinueWith(
            t => _log.Error("批量执行任务后台异常", t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// 批量执行任务并等待全部脚本结束。
    /// 定时触发（TaskJob）走本方法以便停机时 WaitForJobsToComplete 等到脚本完成；
    /// 每个任务并行执行、单任务异常只记日志不影响其他任务。
    /// </summary>
    public Task ExecTaskAsync(List<string> ids, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            var tasks = CacheManager.Get<TaskModel>().Where(n => ids.Contains(n.Id));
            await Task.WhenAll(tasks.Select(taskInfo => RunSingleTaskAsync(taskInfo, ct)));
        });
    }

    private static async Task RunSingleTaskAsync(TaskModel taskInfo, CancellationToken ct = default)
    {
        var dateTime = DateTime.Now;
        var log = new LogModel
        {
            CreateTime = DateTime.Now,
            LogType = LogType.任务日志,
            Operator = "System",
            Remark = "执行脚本任务",
            Success = true,
            Title = taskInfo.Name,
            // 与 TaskExcuteService 写入侧同款净化：日志目录名剔除路径分隔符，保证记录与落盘目录一致可读
            DirectoryName = TaskExcuteService.LogDirNameFrom(taskInfo.FileName),
            LogPath = $"{dateTime:yyyyMMddHHmmssfff}.log"
        };
        try
        {
            //环境变量（扁平化：全部启用中的变量统一注入；代理/推送开关与临时令牌概念随进程内直调消失，
            //改由 ctx.EnableProxy/ctx.EnablePush 表达）
            var envs = CacheManager.Get<EnvModel>().Where(n => n.Enable).ToList();
            envs.Add(new EnvModel
            {
                Name = "IsSystem",
                Value = "true"
            });
            var temp = new TaskCommandStep
            {
                Task = taskInfo,
                CreateTime = dateTime,
                // WaitTime=0 的缺省强制结束窗 60min（原 24h：取消只是协作信号，不检查 ct 的挂死脚本
                // 会占满一天；显式配置过 WaitTime 的任务不受影响）
                ForceEndTime = DateTime.Now.AddMinutes(taskInfo.WaitTime == 0 ? 60 : taskInfo.WaitTime),
                HasChildTask = false,
                UpdateTime = DateTime.Now,
                Envs = envs
            };

            LogServiceHelper.Logs.Enqueue(log);
            await temp.Run(ct);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{taskInfo.Name}]自动执行时出现异常：[{e.Message}]");
            // 任务失败通知（A5.4 接线，受众=管理员；App 离线走厂商推送/通知中心兜底）
            NotifyManagersAsync(taskInfo.Id, taskInfo.Name, $"任务「{taskInfo.Name}」执行失败：{e.Message}");
            // AI 自动分析（2026-09-20 接线）：仅当全局设置打开「失败自动分析」时才会真正发起，否则空转返回
            AgentAutoAnalyze.OnTaskFailure(taskInfo.Id, taskInfo.Name, taskInfo.FileName, e.Message);
        }
    }

    /// <summary>
    /// 任务→通知（category=task）：fire-and-forget，不阻塞执行链路。
    /// 任务模型无归属用户字段：失败/定时口径广播给全部设备；手动执行同样广播。
    /// </summary>
    private static void NotifyManagersAsync(string taskId, string taskName, string content)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await AppPushDispatcher.SendNotificationAsync("任务通知", content, "task", $"quantum://task/{taskId}/log", sessionKey: taskId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"任务通知发送失败（{taskName}）：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 分页获取脚本指令（含任务子步骤）
    /// </summary>
    /// <param name="query">查询条件</param>
    /// <param name="excludeManager">服务端权限过滤：非管理员令牌不返回 Manager 任务（列表+总数同步收敛）</param>
    public async Task<PageResult<TaskModel>> GetPageAsync(TaskQuery query, bool excludeManager = false)
    {
        if (!string.IsNullOrEmpty(query.Key))
        {
            query.Key = query.Key.ToLower();
        }

        var tasks = _dbContext.Tasks.AsNoTracking().Where(n =>
            (!excludeManager || !n.Manager)
            && (query.Enable == null || query.Enable.Value == n.Enable)
        && (string.IsNullOrEmpty(query.Key) || n.Name.ToLower().Contains(query.Key)
        || (!string.IsNullOrEmpty(n.Command) && n.Command.ToLower().Contains(query.Key))
        || (!string.IsNullOrEmpty(n.FileName) && n.FileName.ToLower().Contains(query.Key))));


        var resultTasks = await tasks.OrderByDescending(n => n.CreateTime).ThenBy(n => n.Id).Skip(query.Skip).Take(query.PageSize).ToListAsync();

        var subTasks = CacheManager.Get<TaskSubModel>().Where(n => resultTasks.Select(m => m.Id).Contains(n.TaskId)).ToList();

        foreach (var task in resultTasks)
        {
            task.TaskSubs = subTasks.Where(n => n.TaskId == task.Id).OrderBy(n => n.Sort).ToList();
        }
        return new PageResult<TaskModel>
        {
            Data = resultTasks,
            TotalCount = await tasks.CountAsync(),
            Page = query.PageIndex,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 校验调用者可操作全部目标任务：非管理员令牌不得触碰 Manager 任务
    /// （列表已被服务端过滤，此处补齐按 id 直呼的执行/编辑/删除入口）
    /// </summary>
    public void EnsureAccessible(List<string> ids, bool includeManager)
    {
        if (includeManager || ids == null || ids.Count == 0)
        {
            return;
        }
        if (CacheManager.Get<TaskModel>().Any(n => ids.Contains(n.Id) && n.Manager))
        {
            throw new BusinessException("包含无权操作的任务！");
        }
    }

    /// <summary>
    /// 按ID获取任务详情
    /// </summary>
    public Task<TaskModel> GetByIdAsync(string id)
    {
        return _dbContext.GetByIdAsync<TaskModel>(id);
    }

    /// <summary>
    /// 读取脚本文件内容
    /// </summary>
    /// <param name="fileName">文件名</param>
    public async Task<string> GetScriptAsync(string fileName)
    {
        var scriptFile = SafeFile.Resolve("./scripts/quantum", fileName);
        if (scriptFile == null || !System.IO.File.Exists(scriptFile))
        {
            throw new BusinessException("脚本文件不存在，请检查！");
        }
        using StreamReader streamReader = new(scriptFile);
        var d = await streamReader.ReadToEndAsync();
        return d.TrimEnd('\r', '\n');
    }

    /// <summary>
    /// 更新脚本文件（保存流水线：门禁+编译任一不过即拒绝落盘，编辑页凭返回的三类诊断保留内容重试）
    /// </summary>
    public async Task<ScriptBuildService.ScriptSaveResult> UpdateScriptAsync(SaveQLFile file)
    {
        var scriptFile = SafeFile.Resolve("./scripts/quantum", file.path);
        if (scriptFile == null)
        {
            throw new BusinessException("脚本路径非法，请检查！");
        }
        var build = ScriptBuildService.Build(file.content, scriptFile);
        if (!build.Success)
        {
            return new ScriptBuildService.ScriptSaveResult
            {
                Success = false,
                Blocked = build.Blocked,
                Warnings = build.Warnings,
                Errors = build.Errors
            };
        }
        await using (StreamWriter streamReader = new(scriptFile, false))
        {
            await streamReader.WriteLineAsync(file.content);
            await streamReader.FlushAsync();
        }
        // 版本留痕（旁路：写库异常不影响保存结果）
        await _scriptVersionService.RecordQuietlyAsync(file.path, file.content, ScriptVersionSource.ManualEdit);
        return new ScriptBuildService.ScriptSaveResult { Success = true, Warnings = build.Warnings };
    }

    /// <summary>
    /// 删除脚本文件
    /// </summary>
    public bool DeleteScript(string fileName)
    {
        var path = SafeFile.Resolve("./scripts/quantum", fileName);
        if (path != null && System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }
        // 删除脚本同步清编译缓存：否则该脚本的产物字节与已加载 collectible ALC 永驻进程
        //（此前删除只删文件，缓存唯一清理点是「同名新成功版本」，再无任何路径触达被删条目）
        ScriptBuildService.Remove(fileName);
        return true;
    }

    /// <summary>
    /// 添加任务
    /// </summary>
    public async Task<bool> AddAsync(TaskSaveModel saveModel)
    {
        ValidateCron(saveModel?.Cron);
        await ValidateSessionNameAsync(saveModel?.SessionName);
        if (!string.IsNullOrEmpty(saveModel.Id) && await _dbContext.Tasks.AnyAsync(n => n.Id == saveModel.Id))
        {
            throw new BusinessException("任务已存在，请刷新列表后重新新增！");
        }
        var task = new TaskModel { Id = saveModel.Id };
        ApplyTo(task, saveModel);
        task.Id = Guid.NewGuid().ToString().Replace("-", "");
        task.CreateTime = DateTime.Now;
        PrepareSubs(task);
        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        await _dbContext.AddAsync(task);
        if (task.TaskSubs.Any())
        {
            await _dbContext.AddRangeAsync(task.TaskSubs);
        }
        await tx.CommitAsync();
        await task.CreateQuartzJob();
        return true;
    }

    /// <summary>
    /// 更新任务（isManager=false 时 Manager 标记保持库中原值，防止非管理员借编辑提权）
    /// </summary>
    public async Task<bool> UpdateAsync(TaskSaveModel saveModel, bool isManager = true)
    {
        ValidateCron(saveModel?.Cron);
        await ValidateSessionNameAsync(saveModel?.SessionName);
        var t = await _dbContext.GetByIdAsync<TaskModel>(saveModel.Id);
        if (t == null)
        {
            throw new BusinessException("任务不存在，可能已被删除，请刷新列表！");
        }
        await t.DeleteQuartzJob();
        var managerFlagBefore = t.Manager;
        // 改名迁移钩子（§2.5）：ApplyTo 前按库中原值算旧出站键、ApplyTo 后算新出站键，
        // 满足迁移条件时在下方事务内调用 RenameSession 搬迁历史消息与会话行
        var oldSessionKey = TaskExcuteService.ResolveSessionKey(t.Id, t.SessionName);
        ApplyTo(t, saveModel);
        var newSessionKey = TaskExcuteService.ResolveSessionKey(t.Id, t.SessionName);
        if (!isManager)
        {
            t.Manager = managerFlagBefore;
        }
        PrepareSubs(t);
        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        // 会话名改名迁移（§2.5，全部条件满足才执行）：
        // a) 新旧出站键不同；b) 旧键下存在消息行；
        // c) P1 守卫——无其他在册任务解析到旧键（消息表无来源任务列，共享会话整段搬迁会搬错
        //    其他任务的数据；共享会话改名跳过迁移，历史留在原会话，语义与「新建会话」一致）
        if (oldSessionKey != newSessionKey)
        {
            var oldKeyHasMessages = await _dbContext.ChatMessages.AsNoTracking()
                .AnyAsync(n => n.SessionKey == oldSessionKey);
            var otherTasks = await _dbContext.Tasks.AsNoTracking()
                .Where(n => n.Id != t.Id)
                .Select(n => new { n.Id, n.SessionName })
                .ToListAsync();
            var oldKeyIsShared = otherTasks.Any(n => TaskExcuteService.ResolveSessionKey(n.Id, n.SessionName) == oldSessionKey);
            if (oldKeyHasMessages && !oldKeyIsShared)
            {
                await _appMessageService.RenameSession(oldSessionKey, newSessionKey);
            }
        }
        await _dbContext.UpdateAsync(t);
        await _dbContext.DeleteByIdsAsync<TaskSubModel>(CacheManager.Get<TaskSubModel>().Where(n => n.TaskId == t.Id).Select(n => n.Id));
        if (t.TaskSubs.Any())
        {
            await _dbContext.AddRangeAsync(t.TaskSubs);
        }
        await tx.CommitAsync();
        await t.CreateQuartzJob();
        return true;
    }

    private void ValidateCron(string cron)
    {
        if (!string.IsNullOrEmpty(cron) && !CronExpression.TryParse(cron, out _))
        {
            throw new BusinessException("您填写了定时执行表达式，但该值无法通过验证，请修改后保存。");
        }
    }

    /// <summary>
    /// 会话名归一：Trim 后空返回 null（不归组），其余返回 Trim 结果（落库/比对统一走本方法）。
    /// </summary>
    internal static string NormalizeSessionName(string sessionName)
    {
        var trimmed = sessionName?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// 会话名校验（新增/更新/导入三处共用，查库为准）：
    /// 1. Trim 后空 → 存 null（不归组）；
    /// 2. 长度 ≤ 100（对齐迁移列型 varchar(100)）；
    /// 3. 不得与「校验时点上将存在于库中的任何任务 Id」相同（防入站会话路由串线，防御性校验；含自身 Id，简单化处理）。
    /// </summary>
    /// <param name="sessionName">待校验会话名（原样传入，内部归一）</param>
    /// <param name="excludeIds">从校验 Id 集中剔除的任务 Id（导入场景 = 将被同文件名替换删除的任务；新增/更新不剔）</param>
    internal async Task ValidateSessionNameAsync(string sessionName, IEnumerable<string> excludeIds = null)
    {
        var name = NormalizeSessionName(sessionName);
        if (name == null)
        {
            return;
        }
        if (name.Length > MaxSessionNameLength)
        {
            throw new BusinessException($"会话名长度不能超过 {MaxSessionNameLength} 个字符！");
        }
        // §2-18：只判定「是否存在 Id==name 的任务」（Id 是主键，命中至多一行），改服务端 AnyAsync 走主键 seek；
        // 旧实现每次把全表 Id 拉进内存再 Contains，导入逐条调用即 O(N²)
        if (await _dbContext.Tasks.AsNoTracking().AnyAsync(n => n.Id == name))
        {
            var excluded = excludeIds as ICollection<string> ?? excludeIds?.ToList();
            if (excluded == null || !excluded.Contains(name))
            {
                throw new BusinessException("会话名不能与现存任务的 Id 相同（防会话路由串线），请更换会话名！");
            }
        }
    }

    /// <summary>
    /// 任务执行脚本文件名校验：必须落在 ./scripts/quantum 根内（防 ../ 穿越）、
    /// 且扩展名为 .cs 源码任务（ScriptBuildService.ScriptFileExtension，.js/.py 已移除）。
    /// 执行侧（TaskExcuteService）另有 SafeFile + 门禁/编译二次校验兜底。
    /// OpenTriggerTask（SrciptFile）导入同用本校验——一切「配置即执行」的入口统一白名单。
    /// </summary>
    public static void ValidateScriptFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new BusinessException("任务脚本文件名不能为空！");
        }
        if (fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new BusinessException("任务脚本文件名不允许包含路径穿越字符！");
        }
        if (fileName.Replace('\\', '/').TrimStart('/')
            .StartsWith(AgentTestRunService.StagingDir + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException("该目录是 AI 试运行的影子文件区，不允许作为任务脚本！");
        }
        if (SafeFile.Resolve("./scripts/quantum", fileName) == null)
        {
            throw new BusinessException("任务脚本文件名非法：必须位于 scripts/quantum 目录内！");
        }
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext != ScriptBuildService.ScriptFileExtension)
        {
            throw new BusinessException($"不支持的任务脚本类型：{ext}（仅支持 {ScriptBuildService.ScriptFileExtension} 源码任务，.js/.py 已停止支持）");
        }
    }

    /// <summary>
    /// 把入参模型的可编辑字段套到实体上（CreateTime/TaskStatus/TaskThreadId 等服务端管控字段不接收自请求体）
    /// </summary>
    private static void ApplyTo(TaskModel t, TaskSaveModel m)
    {
        ValidateScriptFileName(m?.FileName);
        t.Name = m.Name;
        t.Command = m.Command;
        t.CommandEnv = m.CommandEnv;
        t.Cron = m.Cron;
        t.FileName = m.FileName;
        t.TextToPicture = m.TextToPicture;
        t.EnableRegex = m.EnableRegex;
        t.Enable = m.Enable;
        t.DayLimit = m.DayLimit;
        t.EnablePush = m.EnablePush;
        t.PushGroup = m.PushGroup;
        t.Revocation = m.Revocation;
        t.Manager = m.Manager;
        t.WaitTime = m.WaitTime;
        t.TaskStartNotify = m.TaskStartNotify;
        t.TaskEndNotify = m.TaskEndNotify;
        t.Remark = m.Remark;
        t.CommunicationTypes = m.CommunicationTypes;
        t.EnableProxy = m.EnableProxy;
        // 会话名归一存储：Trim 后空存 null（不归组），出站会话键解析见 TaskExcuteService.ResolveSessionKey
        t.SessionName = NormalizeSessionName(m.SessionName);
        t.TaskSubs = (m.TaskSubs ?? []).Select(s => new TaskSubModel
        {
            Name = s.Name,
            EnableRegex = s.EnableRegex,
            Command = s.Command,
            Sort = s.Sort,
            CommandEnv = s.CommandEnv,
            Revocation = s.Revocation,
            WaitTime = s.WaitTime,
            Remark = s.Remark
        }).ToList();
    }

    private void PrepareSubs(TaskModel task)
    {
        task.TaskSubs ??= [];
        task.TaskSubs = task.TaskSubs
            .Where(n => !string.IsNullOrWhiteSpace(n.Command) && !string.IsNullOrWhiteSpace(n.Name) && !string.IsNullOrWhiteSpace(n.CommandEnv))
            .ToList();
        foreach (var item in task.TaskSubs)
        {
            item.TaskId = task.Id;
        }
    }

    /// <summary>
    /// 删除量子任务
    /// </summary>
    /// <param name="ids">逗号分隔的任务ID</param>
    public async Task<bool> DeleteAsync(string ids)
    {
        var idList = SplitIds(ids);
        if (idList.Count == 0)
        {
            return true;
        }
        // §2-12：待删任务与调度清理按 ids 直查 DB，不再依赖缓存——缓存陈旧会让旧实现漏掉 DeleteQuartzJob（作业残留）
        // 且子表按缓存 Id 删（子任务行残留）。子表改按 TaskId 服务端直删。
        var tasks = await _dbContext.Tasks.AsNoTracking().Where(n => idList.Contains(n.Id)).ToListAsync();
        foreach (var task in tasks)
        {
            await task.DeleteQuartzJob();
        }
        await _dbContext.TaskSubs.Where(n => idList.Contains(n.TaskId)).ExecuteDeleteAsync();
        await _dbContext.DeleteByIdsAsync<TaskModel>(idList);
        CacheManager.Refresh<TaskSubModel>();
        return true;
    }

    private static List<string> SplitIds(string ids)
    {
        return string.IsNullOrEmpty(ids) ? [] : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>
    /// 批量禁用脚本指令
    /// </summary>
    public async Task<bool> DisableAsync(List<string> ids)
    {
        // §2-3：不再就地改 CacheManager 共享实例，改跟踪查询取上下文实例（同 Id 已跟踪时返回同一实例，避免 Update 二次 Attach 冲突）→改→SaveChanges→Refresh
        if (ids == null || ids.Count == 0)
        {
            return true;
        }
        var tasks = await _dbContext.Tasks.Where(n => ids.Contains(n.Id)).ToListAsync();
        if (tasks.Count == 0)
        {
            return true;
        }
        foreach (var task in tasks)
        {
            task.Enable = false;
            await task.DeleteQuartzJob();
        }
        await _dbContext.SaveChangesAsync();
        CacheManager.Refresh<TaskModel>();
        return true;
    }

    /// <summary>
    /// 批量启用脚本指令
    /// </summary>
    public async Task<bool> EnableAsync(List<string> ids)
    {
        if (ids == null || ids.Count == 0)
        {
            return true;
        }
        var tasks = await _dbContext.Tasks.Where(n => ids.Contains(n.Id)).ToListAsync();
        if (tasks.Count == 0)
        {
            return true;
        }
        foreach (var task in tasks)
        {
            task.Enable = true;
            await task.DeleteQuartzJob();
            await task.CreateQuartzJob();
        }
        await _dbContext.SaveChangesAsync();
        CacheManager.Refresh<TaskModel>();
        return true;
    }

    /// <summary>
    /// 导出脚本指令内容（文件名与响应类型由控制器决定）
    /// </summary>
    public async Task<byte[]> ExportAsync(TaskQuery query, bool excludeManager = false)
    {
        using MemoryStream mem = new();
        query.PageIndex = 1;
        query.PageSize = 999999;
        var result = await GetPageAsync(query, excludeManager);
        var info = JsonConvert.SerializeObject(result.Data.Select(s => new
        {
            s.Name,
            s.FileName,
            s.Command,
            s.Enable,
            s.CommandEnv,
            s.CommunicationTypes,
            s.EnableRegex,
            s.DayLimit,
            s.TaskEndNotify,
            s.EnableProxy,
            s.EnablePush,
            s.Remark,
            s.Revocation,
            s.Manager,
            s.TextToPicture,
            // 显式白名单投影：新增模型字段不会自动带出，会话名须显式加入才随导出
            s.SessionName,
            TaskSubs = s.TaskSubs.Select(t => new
            {
                t.Name,
                t.Command,
                t.CommandEnv,
                t.EnableRegex,
                t.Remark,
                t.Revocation,
                t.Sort
            })
        }));
        StreamWriter sw = new(mem);
        await sw.WriteLineAsync(info);
        sw.Close();
        return mem.ToArray();
    }

    /// <summary>
    /// 导入脚本指令（上传的 json 文件），返回导入成功的数量。
    /// 全量预检 + 单事务：任一脚本名非法即整体拒绝（未动库）；删除旧任务与写入新任务同事务，
    /// 中途失败不再出现"旧任务已删、新任务未写入"的静默数据丢失。
    /// </summary>
    public async Task<int> ImportAsync(IFormFile file)
    {
        string content = "";
        using (StreamReader streamReader = new(file.OpenReadStream()))
        {
            content = await streamReader.ReadToEndAsync();
        }
        List<TaskModel> tasks;
        try
        {
            tasks = JsonConvert.DeserializeObject<List<TaskModel>>(content);
        }
        catch
        {
            throw new BusinessException("解析任务信息失败！");
        }
        // 预检：脚本名全部合法 + 会话名不与将保留任务的 Id 冲突才允许动库（原实现先删后校验，单条非法即丢旧任务）
        var dbTasks = CacheManager.Get<TaskModel>().Where(n => tasks.Select(n => n.FileName).Contains(n.FileName)).ToList();
        foreach (var task in tasks ?? [])
        {
            ValidateScriptFileName(task?.FileName);
            // 导入场景 Id 集口径：现存任务 Id 减去将被同文件名替换删除的任务 Id
            //（导入重生成任务 Id 为 GUID，新 Id 无冲突面）
            await ValidateSessionNameAsync(task?.SessionName, dbTasks.Select(t => t.Id));
        }

        await using (var tx = await _dbContext.Database.BeginTransactionAsync())
        {
            await _dbContext.DeleteByIdsAsync<TaskSubModel>(CacheManager.Get<TaskSubModel>().Where(n => dbTasks.Select(t => t.Id).Contains(n.TaskId)).Select(n => n.Id));
            await _dbContext.DeleteByIdsAsync<TaskModel>(dbTasks.Select(t => t.Id));
            // §2-18：批量跟踪 Add 后单次 SaveChanges（旧实现每条 AddAsync/AddRangeAsync 各触发一次 SaveChanges+全表缓存 Refresh，导入 N 条即 N 次落库+刷缓存）
            var subs = new List<TaskSubModel>();
            foreach (var task in tasks)
            {
                task.CreateTime = DateTime.Now;
                task.Id = task.NewId();
                _dbContext.Tasks.Add(task);
                if (task.TaskSubs != null && task.TaskSubs.Any())
                {
                    foreach (var sub in task.TaskSubs)
                    {
                        sub.TaskId = task.Id;
                    }
                    subs.AddRange(task.TaskSubs);
                }
            }
            if (subs.Count > 0)
            {
                _dbContext.TaskSubs.AddRange(subs);
            }
            await _dbContext.SaveChangesAsync();
            await tx.CommitAsync();
        }
        CacheManager.Refresh<TaskModel>();
        CacheManager.Refresh<TaskSubModel>();

        // 调度同步与库分离：提交成功后再同步 Quartz（RAM 调度器不参与 DB 事务；
        // 此步失败仅影响调度，不影响数据，重启由 StartupTaskHostedService 重建）
        int error = 0;
        foreach (var item in dbTasks)
        {
            try
            {
                await item.DeleteQuartzJob();
            }
            catch (Exception e)
            {
                Console.WriteLine($"导入删除旧任务调度[{item.Name}]失败：{e.Message}");
            }
        }
        foreach (var task in tasks)
        {
            try
            {
                await task.CreateQuartzJob();
            }
            catch (Exception e)
            {
                error++;
                Console.WriteLine($"导入任务[{task.Name}]调度创建失败：{e.Message}");
            }
        }
        return tasks.Count - error;
    }

    /// <summary>
    /// §9.2：多步骤指令步骤句柄鉴权——路由入参是 threadId（脚本侧环境变量 StepCommandTaskThreadId），
    /// 字典键唯一命中即单一步骤；鉴权从「拿入参走 EnsureAccessible」挪到命中之后，
    /// 用 step 实际所属任务的 Manager 标志判定（非 Manager 令牌命中他人 Manager 任务 → 401）。
    /// </summary>
    private static void EnsureStepAccessible(TaskCommandStep step, bool isManager)
    {
        if (isManager || step?.Task == null)
        {
            return;
        }
        if (CacheManager.Get<TaskModel>().Any(n => n.Id == step.Task.Id && n.Manager))
        {
            throw new UnauthorizedBusinessException("包含无权操作的任务！");
        }
    }

    /// <summary>
    /// 手动结束多步骤任务
    /// </summary>
    /// <param name="threadId">指令步骤句柄（StepCommandTaskThreadId，非任务 Id）</param>
    public bool Finish(string threadId, bool isManager)
    {
        if (!MemoryObjectCache.TaskCommandSteps.TryGetValue(threadId, out var t))
        {
            // §9.2：查不中不再静默 return true，明确业务错误语义
            throw new BusinessException("该指令步骤不存在或已结束");
        }
        EnsureStepAccessible(t, isManager);
        MemoryObjectCache.TaskCommandSteps.TryRemove(threadId, out _);
        return true;
    }

    /// <summary>
    /// 多步骤任务回到上一步
    /// </summary>
    /// <param name="threadId">指令步骤句柄（StepCommandTaskThreadId，非任务 Id）</param>
    public string Redo(string threadId, bool isManager)
    {
        if (!MemoryObjectCache.TaskCommandSteps.TryGetValue(threadId, out var t) || t == null)
        {
            throw new BusinessException("该指令步骤不存在或已结束");
        }
        EnsureStepAccessible(t, isManager);
        if (string.IsNullOrEmpty(t.CurrentSubTaskId))
        {
            throw new BusinessException("当前任务不在子任务状态下，无法退回");
        }
        var subTasks = CacheManager.Get<TaskSubModel>().Where(n => n.TaskId == t.Task.Id).OrderBy(n => n.Sort);
        var currentSubTask = subTasks.FirstOrDefault(n => n.Id == t.CurrentSubTaskId);
        if (currentSubTask == null)
        {
            // 任务在多步执行期间被编辑会整批重建子步骤（Id 全变），按 Id 找不到原步骤时引导重来而不是 NRE
            throw new BusinessException($"任务步骤已被修改（原步骤不存在），请重新发起任务！");
        }
#if DEBUG
        Console.WriteLine($"移除当前步骤的环境变量[{currentSubTask.CommandEnv}]");
#endif
        // §1-4：不再就地 RemoveAll 共享 List（执行线程正读同一 List，并发增删会撕裂/抛异常）——构建新 List 整体换引用，
        // 读者持旧引用天然拿到一致快照
        t.Envs = t.Envs.Where(n => n.Name != currentSubTask.CommandEnv).ToList();
        t.CurrentSubTaskId = subTasks.FirstOrDefault(n => n.Sort < currentSubTask.Sort)?.Id;
        t.NextSubTaskId = currentSubTask.Id;
        t.HasChildTask = true;
        return $"将任务回退到步骤：【{currentSubTask.Name}】，子任务ID：【{t.NextSubTaskId}】";
    }

    /// <summary>
    /// 为当前任务添加一个环境变量
    /// </summary>
    public string AddEnv(string threadId, EnvModel env, bool isManager)
    {
        // 编译正则表达式
        Regex regex = new(@"^[a-zA-Z][a-zA-Z0-9_]{1,64}$");

        // 验证用户名格式
        if (!regex.IsMatch(env.Name))
        {
            throw new BusinessException("环境变量名称异常：只能包含数字和字母下划线长度2-64位");
        }
        if (!MemoryObjectCache.TaskCommandSteps.TryGetValue(threadId, out var t) || t == null)
        {
            throw new BusinessException("该指令步骤不存在或已结束");
        }
        EnsureStepAccessible(t, isManager);
        // §1-4：构建新 List（剔除同名 + 追加新项）整体换引用，避免与执行线程读取并发冲突
        var snapshot = t.Envs;
        var kept = snapshot.Where(n => n.Name != env.Name).ToList();
        var removed = snapshot.Count - kept.Count;
        kept.Add(env);
        t.Envs = kept;
        return $"当前任务新增环境变量成功，移除同名环境变量：【{removed}】个";
    }

    /// <summary>
    /// 获取所有的脚本文件
    /// </summary>
    public List<ScriptsFile> GetScriptList()
    {
        string rootDirectory = Directory.GetCurrentDirectory();
        var root = Path.Combine(rootDirectory, "scripts", "quantum");
        // 脚本目录缺失（全新部署/被清理）时返回空列表，而非让整个脚本编辑页 500
        if (!Directory.Exists(root))
        {
            return [];
        }
        return TraverseDirectory(root, root).children;
    }

    private ScriptsFile TraverseDirectory(string directoryPath, string rootPath)
    {
        ScriptsFile currentDir = new()
        {
            title = new DirectoryInfo(directoryPath).Name,
            children = new List<ScriptsFile>()
        };
        if (!Directory.Exists(directoryPath))
        {
            return currentDir;
        }
        string[] directories = Directory.GetDirectories(directoryPath);
        foreach (string directory in directories)
        {
            if (_excludeDicretory.Count(n => directory.Contains(n)) > 0)
            {
                continue;
            }
            currentDir.children.Add(TraverseDirectory(directory, rootPath));
        }

        currentDir.children.AddRange(
            Directory.GetFiles(directoryPath).Select(n => new FileInfo(n)).Where(n => _allowExtends.Contains(n.Extension)).Select(s => new ScriptsFile
            {
                contextmenu = true,
                title = s.Name,
                path = s.FullName.Replace(rootPath, "").Replace("\\", "/").TrimStart('/')
            }).ToList());
        return currentDir;
    }
}
