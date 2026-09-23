using log4net;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Quantum.Data;
using Quantum.Entities;
using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Quantum.Application;

public class MessageProcess
{
    private readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(MessageProcess));

    private Setting _setting;
    private List<SystemCommand> SystemCommands;

    /// <summary>
    /// 收到消息处理
    /// </summary>
    public async Task MessageAsync(MessageProccessDTO message)
    {
        message.MessageType = MessageType.文本;

        if (string.IsNullOrEmpty(message.user_id) || string.IsNullOrEmpty(message.message))
        {
            return;
        }
        _setting = SystemConfigHelper.GetSetting();
        if (!string.IsNullOrEmpty(_setting.BlackQQ) && _setting.BlackQQ.ToLower().Split(",").Any(n => n == message.user_id.ToLower()))
        {
            Console.WriteLine($"[{message.user_id}]黑名单用户");
            return;
        }

        SystemCommands = SystemCommandHelper.Get();
        // 单管理员体系：触发方恒为管理员（App 提交/通知接口均需 Manager 令牌），管理员系统指令直接放行
        // 唯一主体键：管理员账号名（脚本侧 user_id 变量、多步骤会话归属均用它）
        var adminKey = _setting.UserName ?? "admin";

        try
        {
            message.message = message.message.Trim();
            if (await SystemCommand(message))
            {
                return;
            }
            var commands = CacheManager.Get<CommandModel>().Where(n => n.Enable && (n.CommunicationType == null || n.CommunicationType == message.CommunicationType));
            foreach (var item in commands)
            {
                if (commandReg(item.Key, message.message, item.EnableRegex))
                {
                    LogServiceHelper.Logs.Enqueue(new LogModel
                    {
                        CreateTime = DateTime.Now,
                        LogType = LogType.指令触发,
                        Operator = $"{message.user_name}({message.user_id})",
                        Remark = message.message,
                        Success = true,
                        Title = "快捷回复：" + item.Key
                    });

                    if (message.message.StartsWith("推送 "))
                    {
                        #region 管理员推送快捷回复
                        var push = new MessageProccessDTO
                        {
                            user_id = adminKey,
                            CommunicationType = CommunicationType.App
                        };
                        push.SendMessage(item.Message);
                        return;
                        #endregion
                    }
                    else
                    {
                        message.MessageType = item.MessageType;
                        message.SendMessage(item.Message);
                    }
                }
            }


            MemoryObjectCache.SweepExpiredSteps();

            #region 手动退出多步骤任务

            if (message.message.ToLower() == "q")
            {
                var count = MemoryObjectCache.TaskCommandSteps.Count(n => n.Value.UserId == adminKey);
                if (count > 0)
                {
                    foreach (var (key, _) in MemoryObjectCache.TaskCommandSteps.Where(n => n.Value.UserId == adminKey).ToList())
                    {
                        MemoryObjectCache.TaskCommandSteps.TryRemove(key, out _);
                    }
                    message.SendMessage("已退出所有指令任务。");
                }
                return;
            }

            #endregion

            var tasks = CacheManager.Get<TaskModel>().Where(n => n.Enable).ToList();

            var currentTask = MemoryObjectCache.TaskCommandSteps.FirstOrDefault(n => n.Value.UserId == adminKey && n.Value.HasChildTask).Value;
            List<TaskCommandStep> taskCommandSteps = new();
            if (currentTask != null)
            {
                var subTasks = CacheManager.Get<TaskSubModel>().Where(n => n.TaskId == currentTask.Task.Id);
                var subTask = subTasks.FirstOrDefault(n => n.Id == currentTask.NextSubTaskId);
                if (subTask == null)
                {
                    // 任务在多步执行期间被编辑会整批重建子步骤（Id 全变）：按 Id 找不到时清掉坏会话并提示重来，
                    // 不能在判空前解引用 subTask.Name/Command（原顺序 NRE 会把用户消息静默吞掉且会话永不恢复）
                    MemoryObjectCache.TaskCommandSteps.TryRemove(currentTask.ThreadId, out _);
                    _log.Info($"当前用户[{message.user_name}（{message.user_id}）]多步骤任务【{currentTask.Task.Name}】的等待步骤已被修改，会话已清理。");
                    message.SendMessage("任务步骤已被修改，请重新发起任务。");
                    return;
                }
                _log.Info($"当前用户[{message.user_name}（{message.user_id}）]多步骤任务【{currentTask.Task.Name}】，等待步骤：【{subTask.Name}】，触发指令：【{subTask.Command}】");
                if (commandReg(subTask.Command, message.message, subTask.EnableRegex))
                {
                    currentTask.IsChildTask = true;
                    currentTask.CurrentSubTaskId = subTask.Id;

                    currentTask.Envs.Add(new EnvModel
                    {
                        Value = message.message,
                        Name = subTask.CommandEnv
                    });
                    var next = subTasks.Where(n => n.Sort > subTask.Sort).OrderBy(n => n.Sort).FirstOrDefault();
                    if (next != null)
                    {
                        currentTask.NextSubTaskId = next.Id;
                    }
                    else
                    {
                        currentTask.HasChildTask = false;
                    }
                    currentTask.SubTaskName = subTask.Name;
                    currentTask.CreateTime = DateTime.Now;
                    currentTask.UpdateTime = DateTime.Now;
                    currentTask.ForceEndTime = DateTime.Now.AddMinutes(subTask.WaitTime == 0 ? 3 : subTask.WaitTime);
                    taskCommandSteps.Add(currentTask);
                }
                //return;
            }
            else
            {
                foreach (var task in SelectTaskCandidates(tasks, message, commandReg))
                {
                    if (!commandReg(task.Command, message.message, task.EnableRegex))
                    {
                        continue;
                    }

                    if (currentTask != null && task.Id == currentTask.Task.Id)
                    {
                        message.SendMessage($"当前已存在{task.Name}任务，请按照提示操作，或者回复字母“Q”退出所有指令。");
                        return;
                    }

                    var subTasks = CacheManager.Get<TaskSubModel>().Where(n => n.TaskId == task.Id).ToList();

                    //环境变量（扁平化：全部启用中的变量统一注入）
                    var commonEnvs = CacheManager.Get<EnvModel>().Where(n => n.Enable).ToList();
                    commonEnvs.Add(new EnvModel
                    {
                        Name = "MessageId",
                        Value = message.message_id
                    });
                    commonEnvs.Add(new EnvModel
                    {
                        Name = string.IsNullOrEmpty(task.CommandEnv) ? "command" : task.CommandEnv,
                        Value = message.message
                    });
                    commonEnvs.Add(new EnvModel
                    {
                        Name = "CommunicationUserName",
                        Value = message.user_name
                    });
                    commonEnvs.Add(new EnvModel
                    {
                        Name = "CommunicationUserId",
                        Value = message.user_id
                    });
                    commonEnvs.Add(new EnvModel
                    {
                        Name = "user_id",
                        Value = adminKey
                    });
                    commonEnvs.Add(new EnvModel
                    {
                        Name = "CommunicationType",
                        Value = ((int)message.CommunicationType).ToString()
                    });
                    commonEnvs.Add(new EnvModel
                    {
                        Name = "system_ismanager",
                        // 单管理员：恒为管理员（保留变量名以兼容存量脚本）
                        Value = "true"
                    });
                    if (task.PushGroup)
                    {
                        commonEnvs.Add(new EnvModel
                        {
                            Name = "group_id",
                            Value = message.group_id
                        });
                    }
                    // 代理/推送开关随进程内直调改由 ctx.EnableProxy/ctx.EnablePush 表达，不再注入环境变量
                    var temp = new TaskCommandStep
                    {
                        Task = task,
                        CreateTime = DateTime.Now,
                        ForceEndTime = DateTime.Now.AddMinutes(task.WaitTime == 0 ? 60 * 24 : task.WaitTime),
                        HasChildTask = subTasks.Any(),
                        NextSubTaskId = subTasks.OrderBy(m => m.Sort).FirstOrDefault()?.Id,
                        UserId = adminKey,
                        UpdateTime = DateTime.Now,
                        Envs = commonEnvs.DeepClone(),
                        ThreadId = Guid.NewGuid().ToString().Replace("-", "").ToUpper()
                    };
                    taskCommandSteps.Add(temp);
                    if (temp.HasChildTask)
                    {
                        temp.Envs.Add(new EnvModel
                        {
                            Name = "StepCommandTaskThreadId",
                            Value = temp.ThreadId
                        });
                        MemoryObjectCache.TaskCommandSteps[temp.ThreadId] = temp;
                    }
                }
            }
            var taskList = new List<Task>();
            // 跨会话触发消息迁移（§4.2，2026-09-18 批次）：命中的任务会话与来源会话不一致时，
            // 用户气泡行迁到目标会话 + 广播 message_moved（客户端搬本地行并在停留来源会话时自动切窗口）。
            // message.SessionKey 先改——后续 Start/EndNotify 的克隆跟随落任务会话（D2：用户已被切走，留原会话看不见）；
            // 快捷回复克隆发生在此之前的 commands 循环里，天然留原会话不跟随。失败降级：行留原会话、任务照常执行
            try
            {
                var moveTarget = ResolveMoveTarget(taskCommandSteps, message.SessionKey);
                if (moveTarget != null)
                {
                    var originKey = message.SessionKey;
                    message.SessionKey = moveTarget;
                    await MessageMover(message.message_id, originKey, moveTarget);
                }
            }
            catch (Exception e)
            {
                _log.Error("跨会话触发消息迁移失败（降级为留原会话）", e);
            }
            foreach (var item in taskCommandSteps)
            {
                LogServiceHelper.Logs.Enqueue(new LogModel
                {
                    CreateTime = DateTime.Now,
                    LogType = LogType.指令触发,
                    Operator = $"{message.user_name}({message.user_id})",
                    Remark = message.message,
                    Success = true,
                    Title = item.Task.Name + (string.IsNullOrEmpty(item.SubTaskName) ? "" : "-" + item.SubTaskName),
                    DirectoryName = TaskExcuteService.LogDirNameFrom(item.Task.FileName),
                    LogPath = $"{item.CreateTime:yyyyMMddHHmmssfff}.log"
                });
                taskList.Add(Task.Run(async () =>
                {
                    if (!string.IsNullOrEmpty(item.Task.TaskStartNotify) && (!item.IsChildTask))
                    {
                        message.SendMessage(item.Task.TaskStartNotify, item.Task.TextToPicture);
                    }
                    await item.Run();
                    if (!string.IsNullOrEmpty(item.Task.TaskEndNotify) && !item.HasChildTask)
                    {
                        message.SendMessage(item.Task.TaskEndNotify, item.Task.TextToPicture);
                    }
                }));
            }
            await Task.WhenAll(taskList);
        }
        catch (Exception e)
        {
            _log.Error(e);
        }
    }

    /// <summary>
    /// 跨会话消息迁移接缝（测试可替换，仿 SendMessageHelper.Sender）：默认走 AppPushDispatcher 静态门面——
    /// MessageProcess 经 root provider 以 AddTransient 解析，不能构造注入 scoped 的 AppPushService。
    /// </summary>
    internal static Func<string, string, string, Task> MessageMover = AppPushDispatcher.MoveUserMessageAsync;

    /// <summary>
    /// 跨会话迁移目标判定（internal static 供单测，与 SelectTaskCandidates 同套路，§4.2）：
    /// 取全部命中任务步骤的出站会话键（ResolveSessionKey 口径）去重——
    /// **恰一个且 ≠ 来源会话**（归一化：null/空白 = 默认会话）→ 返回该键；否则 null（不迁移）。
    /// 多任务命中但同会话键（合并会话多触发）无歧义照迁；多命中且会话键不同（D1）与零命中均不迁。
    /// </summary>
    internal static string ResolveMoveTarget(List<TaskCommandStep> steps, string originKey)
    {
        var keys = (steps ?? [])
            .Select(n => TaskExcuteService.ResolveSessionKey(n.Task))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();
        if (keys.Count != 1)
        {
            return null;
        }
        var target = keys[0];
        var origin = string.IsNullOrWhiteSpace(originKey) ? null : originKey.Trim();
        return target == origin ? null : target;
    }

    /// <summary>
    /// 任务触发候选筛选（internal 供单测）。三级路由（2026-09-18 会话分组归并批次，P4 版）：
    /// ① 点选精确路由——TargetTaskId 非空、命中 commTasks 中启用任务、且指令匹配该任务 → 只返回它；
    ///    任务不存在/禁用/通讯类型不符/指令不匹配 → 视为失效落入 ②，不直接否决
    ///    （选项 reply 是任意文本，可能本就指向会话内另一个指令匹配的任务）；
    /// ② 会话任务集路由——SessionKey 非空时取组（Id == SessionKey 或 ResolveSessionKey(任务) == SessionKey）：
    ///    组内指令匹配唯一命中 → 只返回它；多命中 → 仅返回组内命中集、不外溢全局
    ///    （P4 决策：合并会话内手打只在组内多触发，不再误触会话外任务，纯数字双触发不回归）；
    ///    零命中或组不存在（含已删任务的会话键）→ 回落全局；
    /// ③ 无会话键 → 全局 commTasks（与旧版行为一致，由上层做指令匹配）。
    /// </summary>
    internal static List<TaskModel> SelectTaskCandidates(
        IReadOnlyList<TaskModel> enabledTasks, MessageProccessDTO message, Func<string, string, bool, bool> matcher)
    {
        var commTasks = enabledTasks
            .Where(n => string.IsNullOrEmpty(n.CommunicationTypes) || n.CommunicationTypes.Contains(((int)message.CommunicationType).ToString()))
            .ToList();

        // ① 点选精确路由
        if (!string.IsNullOrWhiteSpace(message.TargetTaskId))
        {
            var target = commTasks.FirstOrDefault(n => n.Id == message.TargetTaskId);
            if (target != null && matcher(target.Command, message.message, target.EnableRegex))
            {
                return [target];
            }
        }

        // ② 会话任务集路由
        if (!string.IsNullOrWhiteSpace(message.SessionKey))
        {
            var sessionKey = message.SessionKey.Trim();
            var group = commTasks.Where(n => n.Id == sessionKey
                || TaskExcuteService.ResolveSessionKey(n.Id, n.SessionName) == sessionKey).ToList();
            if (group.Count > 0)
            {
                var hits = group.Where(n => matcher(n.Command, message.message, n.EnableRegex)).ToList();
                if (hits.Count == 1)
                {
                    return [hits[0]];
                }
                // 多命中：仅返回组内命中集，不外溢全局（P4）
                if (hits.Count > 1)
                {
                    return hits;
                }
                // 组内零命中 → 回落全局（会话不是指令隔离区，与现状语义一致）
            }
            // 组不存在（含已删任务的会话键）→ 回落全局
        }

        // ③ 无会话键 → 全局候选（由上层做指令匹配）
        return commTasks;
    }

    /// <summary>
    /// 指令正则缓存（§1-6）：脚本会被反复改写（指令文本即键），无界字典是慢性增长，
    /// 条数超上限即整体清空重建（重建成本仅编译一次正则，远低于引入 LRU 的复杂度）。
    /// 非法正则不入缓存——否则一次写坏的指令会长期毒化该键。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();
    private const int RegexCacheLimit = 256;

    public bool commandReg(string command, string message, bool enableRegex)
    {
        if (string.IsNullOrEmpty(command) || string.IsNullOrEmpty(message))
        {
            return false;
        }
        //指令全匹配，且匹配，管理和推送 前缀
        var words = message.Split(" ");
        if (command.ToLower() == message.ToLower() || (words.Length > 1 && words.Contains(command)))
        {
            return true;
        }
        if (!enableRegex)
        {
            return false;
        }
        if (!RegexCache.TryGetValue(command, out var regex))
        {
            try
            {
                regex = new Regex(command, RegexOptions.None, TimeSpan.FromSeconds(2));
            }
            catch (Exception e)
            {
                // 非法正则只废当前这一条指令（记日志后按不匹配处理、由上层循环继续下一条），
                // 不再让异常冒泡到 MessageAsync 的总 try 而丢弃整条消息
                _log.Warn($"指令正则【{command}】非法，已跳过该条指令：{e.Message}");
                return false;
            }
            if (RegexCache.Count >= RegexCacheLimit)
            {
                RegexCache.Clear();
            }
            RegexCache[command] = regex;
        }
        try
        {
            return regex.IsMatch(message);
        }
        catch (RegexMatchTimeoutException e)
        {
            _log.Warn($"指令正则【{command}】匹配超时，已按不匹配处理：{e.Message}");
            return false;
        }
    }


    /// <summary>
    /// 系统指令
    /// </summary>
    /// <returns></returns>
    private async Task<bool> SystemCommand(MessageProccessDTO message)
    {
        if (message.message.StartsWith("docker restart ") && message.message.Split(' ').Length == 3)
        {
            var msg = await DockerHelper.RestartAsync(message.message.Split(' ')[2]);
            message.SendMessage(msg);
        }
        else if (message.message.StartsWith("docker stop ") && message.message.Split(' ').Length == 3)
        {
            var msg = await DockerHelper.StopAsync(message.message.Split(' ')[2]);

            message.SendMessage(msg);
        }
        else if (message.message.ToLower().StartsWith("docker ps"))
        {
            var lists = (await DockerHelper.ListAsync(message.message.Contains("-a")));
            string m = "Docker容器列表获取完成";
            foreach (var item in lists)
            {
                m += $"\r{item.Names[0]}，{item.State}，{item.Status}";
            }
            message.SendMessage(m);
        }

        foreach (var command in SystemCommands)
        {
            if (commandReg(command.Command, message.message, false))
            {
                switch (command.Key)
                {
                    case "统计":
                        var ts = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);
                        var te = ts.AddDays(1);
                        var enableCount = CacheManager.Get<EnvModel>().Count(n => n.Enable);
                        var disableCount = CacheManager.Get<EnvModel>().Count(n => !n.Enable);
                        var todayAdd = CacheManager.Get<EnvModel>().Count(n => n.Enable && n.CreateTime >= ts && n.CreateTime < te);
                        var todayUpdate = CacheManager.Get<EnvModel>().Count(n => n.Enable && n.UpdateTime >= ts && n.UpdateTime < te);

                        var quantumInfo = $@"统计信息如下：
变量总数：{CacheManager.Get<EnvModel>().Count()}
有效变量：{enableCount}
失效变量：{disableCount}
今日新增：{todayAdd}
今日更新：{todayUpdate}";
                        message.SendMessage(quantumInfo);
                        break;
                    case "我的量子":
                        var commandCount = CacheManager.Get<CommandModel>().Count();
                        var scriptCount = CacheManager.Get<TaskModel>().Count();
                        var ipInfo = "";
                        try
                        {
                            var ipdd = await HttpClientHelper.GetAsStringAsync("https://ipinfo.io/ip");
                            ipInfo = "外网IP：" + ipdd;
                        }
                        catch (Exception e)
                        {
                            ipInfo = "IP地址获取失败";
                        }
                        var quantumInfo2 = "量子信息如下：";
                        quantumInfo2 += $"\r版本：{Extends.Version}";
                        quantumInfo2 += $"\r账号：{_setting.UserName}";
                        quantumInfo2 += "\r密码：******";
                        quantumInfo2 += $"\r服务地址：{_setting.ServerPath}";
                        if (!string.IsNullOrEmpty(ipInfo))
                        {
                            quantumInfo2 += $"\r{ipInfo}";
                        }
                        quantumInfo2 += $"\r数据库类型：{_setting.DBType}";
                        // 连接串可能内嵌账号口令，出站回显只保留 host:port；纯地址/文件路径原样
                        var dbAddr = _setting.DBAddress ?? string.Empty;
                        if (dbAddr.Contains('=') && dbAddr.Contains(';'))
                        {
                            var host = string.Empty;
                            foreach (var part in dbAddr.Split(';'))
                            {
                                var kv = part.Split(new[] { '=' }, 2);
                                if (kv.Length != 2) continue;
                                var k = kv[0].Trim();
                                if (k.Equals("Server", StringComparison.OrdinalIgnoreCase)
                                    || k.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                                    || k.Equals("Host", StringComparison.OrdinalIgnoreCase))
                                {
                                    host = kv[1].Trim();
                                    break;
                                }
                            }
                            dbAddr = string.IsNullOrEmpty(host) ? "(已隐藏)" : host;
                        }
                        quantumInfo2 += $"\r数据库地址：{dbAddr}";
                        quantumInfo2 += $"\r快捷回复：{commandCount}";
                        quantumInfo2 += $"\r脚本指令：{scriptCount}";
                        message.SendMessage(quantumInfo2);
                        break;
                        //case "更新":
                        //case "重启":
                        //    message.SendMessage($"{command.Key}操作将在10秒内执行。");
                        //    JobHelper.Update(command.Key);
                        //    break;
                }
                return true;
            }
        }
        return false;
    }
}
