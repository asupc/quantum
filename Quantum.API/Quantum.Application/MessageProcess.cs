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
        // 每条消息独占自己的原路回复上下文；App/Web 的 null 必须显式清空，不能继承上一次机器人路由。
        using var replyContext = Channels.ChannelReplyContext.Use(message.ChannelReplyRouteId);
        _setting = SystemConfigHelper.GetSetting();
        // 原「BlackQQ 黑名单」已删除：Web/App/通道三条链路的 user_id 使用同一账号，
        // 该黑名单只能把唯一操作者静默丢弃（且只打 Console、不落日志、不回复），是个纯粹的自伤开关。

        SystemCommands = SystemCommandHelper.Get();
        // 脚本侧 user_id 仍为唯一登录账号；等待步骤另按来源归属，避免 App 与机器人互相接管。
        var accountKey = _setting.UserName ?? string.Empty;
        var actorKey = ResolveActorKey(message, accountKey);
        bool OwnsStep(TaskCommandStep step) => step.UserId == accountKey
            && (step.ActorKey ?? $"app:{step.UserId}") == actorKey;

        try
        {
            message.message = message.message.Trim();
            if (await SystemCommand(message))
            {
                return;
            }
            // 快捷回复不按通讯类型隔离（CommandModel.CommunicationType 已删除）。
            var commands = CacheManager.Get<CommandModel>().Where(n => n.Enable);
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
                            user_id = accountKey,
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
                var count = MemoryObjectCache.TaskCommandSteps.Count(n => OwnsStep(n.Value));
                if (count > 0)
                {
                    foreach (var (key, _) in MemoryObjectCache.TaskCommandSteps.Where(n => OwnsStep(n.Value)).ToList())
                    {
                        MemoryObjectCache.TaskCommandSteps.TryRemove(key, out _);
                    }
                    message.SendMessage("已退出当前来源的指令任务。");
                }
                return;
            }

            #endregion

            var tasks = CacheManager.Get<TaskModel>().Where(n => n.Enable).ToList();

            var currentTask = MemoryObjectCache.TaskCommandSteps.FirstOrDefault(n => OwnsStep(n.Value) && n.Value.HasChildTask).Value;
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
                    // 命中口径与 SelectTaskCandidates 对齐：裸消息直接命中走原文；
                    // 未命中的是⑤「会话内省略指令词」候选，指令环境变量收到拼回的完整指令文本
                    // （与手打「指令词 消息」等价，脚本既有「剥离指令词取关键字」口径不变）。
                    var commandText = commandReg(task.Command, message.message, task.EnableRegex)
                        ? message.message
                        : TryBuildImplicitCommandText(task.Command, task.EnableRegex, message.message);
                    if (commandText == null)
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
                        Value = commandText
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
                        Value = accountKey
                    });
                    commonEnvs.Add(new EnvModel
                    {
                        Name = "CommunicationType",
                        Value = ((int)message.CommunicationType).ToString()
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
                        UserId = accountKey,
                        ActorKey = actorKey,
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
                taskList.Add(Task.Run(async () =>
                {
                    if (!string.IsNullOrEmpty(item.Task.TaskStartNotify) && (!item.IsChildTask))
                    {
                        message.SendMessage(item.Task.TaskStartNotify, item.Task.TextToPicture);
                    }
                    // 指令触发改走运行记录链路（G2 入口矩阵）：真实终态 + t_log 同事务落库，
                    // 本入口只记录结果，不参与自动重试与告警计数
                    var result = await TaskRunRecorder.RunStepAsync(item, TaskTriggerSource.Command,
                        message.user_id, LogType.指令触发, $"{message.user_name}({message.user_id})");
                    if (item.HasChildTask)
                    {
                        return;
                    }
                    if (!string.IsNullOrEmpty(item.Task.TaskEndNotify))
                    {
                        // 完成文案只在结果成功时发送；失败时回一条同位置的安全摘要，避免「静默失败」
                        message.SendMessage(result.IsSuccess
                            ? item.Task.TaskEndNotify
                            : $"任务「{item.Task.Name}」执行失败（{result.OutcomeLabel}）：{result.SafeSummary}", item.Task.TextToPicture);
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

    /// <summary>App/Web 保留跨任务会话续接；机器人只使用受信工作器元数据，不用可由客户端指定的会话键判身份。</summary>
    internal static string ResolveActorKey(MessageProccessDTO message, string accountKey)
        => string.IsNullOrWhiteSpace(message.ChannelReplyRouteId)
            ? $"app:{accountKey}"
            : (string.IsNullOrWhiteSpace(message.ChannelActorKey)
                ? $"channel-route:{message.ChannelReplyRouteId}"
                : message.ChannelActorKey);

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
    /// 任务触发候选筛选（internal 供单测）：
    /// ① 点选精确路由——TargetTaskId 非空、命中 commTasks 中启用任务、且指令匹配该任务 → 只返回它；
    ///    任务不存在/禁用/通讯类型不符/指令不匹配 → 视为失效落入后续匹配，不直接否决
    ///    （选项 reply 是任意文本，可能本就指向会话内另一个指令匹配的任务）；
    /// ② 全局唯一命中——只有一个任务匹配时，由任务决定会话归属，与发送位置无关；
    /// ③ 会话任务集路由——SessionKey 非空时取组（Id == SessionKey 或 ResolveSessionKey(任务) == SessionKey）：
    ///    组内指令匹配唯一命中 → 只返回它；多命中 → 仅返回组内命中集、不外溢全局
    ///    （P4 决策：合并会话内手打只在组内多触发，不再误触会话外任务，纯数字双触发不回归）；
    ///    零命中或组不存在（含已删任务的会话键）→ 回落全局；
    /// ④ 无会话键或组内零命中 → 全局匹配集；
    /// ⑤ 会话内省略指令词（2026-10-10）——裸消息全局零命中时的兜底：App/Web 手打（非点选代发、非机器人来源）
    ///    在任务会话内直接发内容（如会话「电影港」里发「西游记」），视为省略了指令词的「电影港搜索 西游记」：
    ///    组内（口径同③）**恰一个**带触发指令的任务 → 拼回完整指令文本再验一次 matcher（与手打全指令严格等价，
    ///    指令是精确形态如 ^电影港搜索$ 时拼回后不匹配、维持不触发），命中则返回该任务；
    ///    组内多个带指令任务（无法判定指哪个）/ 组内无带指令任务（如纯定时脚本）→ 不触发，维持现状。
    ///    指令环境变量由 MessageAsync 注入拼回后的完整指令文本（脚本按既有「剥离指令词取关键字」口径解析不变）。
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

        var globalHits = commTasks.Where(n => matcher(n.Command, message.message, n.EnableRegex)).ToList();
        if (globalHits.Count == 1)
        {
            return globalHits;
        }

        // 多任务匹配时才用来源会话消歧；单任务匹配始终归任务会话。
        if (!string.IsNullOrWhiteSpace(message.SessionKey))
        {
            var sessionKey = message.SessionKey.Trim();
            var group = globalHits.Where(n => n.Id == sessionKey
                || TaskExcuteService.ResolveSessionKey(n.Id, n.SessionName) == sessionKey).ToList();
            if (group.Count > 0)
            {
                if (group.Count == 1)
                {
                    return group;
                }
                // 多命中：仅返回组内命中集，不外溢全局（P4）
                if (group.Count > 1)
                {
                    return group;
                }
            }
            // 组不存在（含已删任务的会话键）→ 回落全局
        }

        // ⑤ 会话内省略指令词：仅裸消息（TargetTaskId 空）且 App/Web 主动发送（机器人单线程会话
        // 没有「指定会话」语义，任意闲聊都会误触）时兜底，显式命中（globalHits 非空）永远优先。
        if (globalHits.Count == 0
            && string.IsNullOrWhiteSpace(message.TargetTaskId)
            && string.IsNullOrWhiteSpace(message.ChannelReplyRouteId)
            && !string.IsNullOrWhiteSpace(message.SessionKey))
        {
            var sessionKey = message.SessionKey.Trim();
            var commandTasks = commTasks
                .Where(n => !string.IsNullOrWhiteSpace(n.Command)
                    && (n.Id == sessionKey || TaskExcuteService.ResolveSessionKey(n.Id, n.SessionName) == sessionKey))
                .ToList();
            if (commandTasks.Count == 1)
            {
                var task = commandTasks[0];
                var fullText = TryBuildImplicitCommandText(task.Command, task.EnableRegex, message.message);
                if (fullText != null && matcher(task.Command, fullText, task.EnableRegex))
                {
                    return [task];
                }
            }
        }

        return globalHits;
    }

    /// <summary>
    /// 会话内省略指令词的拼回（⑤ 与 MessageAsync 指令环境变量注入共用同一口径）：
    /// 提取触发指令的指令词，拼出「指令词 + 空格 + 裸消息」，即与手打完整指令同一文本。
    /// 提不出指令词（纯元字符正则）或消息为空 → null（该任务不支持省略指令词）。
    /// </summary>
    internal static string TryBuildImplicitCommandText(string command, bool enableRegex, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !TryExtractCommandWord(command, enableRegex, out var word))
        {
            return null;
        }
        return word + " " + message.Trim();
    }

    /// <summary>
    /// 指令词提取：非正则指令整条就是字面量；正则指令取首个分支（顶层 | 切分）的前导字面量——
    /// 剥掉 ^ 与 (?i)/(?: 等内联组前缀，遇正则元字符即停。启发式提取，误提只会让拼回文本过不了
    /// matcher 复验而安全降级为不触发，不会误放行。
    /// </summary>
    internal static bool TryExtractCommandWord(string command, bool enableRegex, out string word)
    {
        word = null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        var text = command.Trim();
        if (!enableRegex)
        {
            word = text;
            return true;
        }

        var alt = text.Split('|')[0];
        var i = 0;
        while (i < alt.Length)
        {
            if (alt[i] == '^')
            {
                i++;
                continue;
            }
            if (alt[i] == '(' && i + 1 < alt.Length && alt[i + 1] == '?')
            {
                var close = alt.IndexOf(')', i + 2);
                if (close < 0)
                {
                    break;
                }
                i = close + 1;
                continue;
            }
            break;
        }
        var sb = new StringBuilder();
        for (; i < alt.Length; i++)
        {
            var c = alt[i];
            if (c == '\\' || ".*+?[](){}|^$".Contains(c))
            {
                break;
            }
            sb.Append(c);
        }
        word = sb.ToString().Trim();
        return word.Length > 0;
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
