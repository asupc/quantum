using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Application.Channels;
using Quantum.Data;
using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>同一登录账号的 App 与机器人指令不能共享等待步骤或沿用上一条消息的回复路由。</summary>
[Collection("ConstsState")]
public class MessageSourceRoutingTests : IDisposable
{
    private const string Account = "source-routing-test";
    private readonly string _tempDir;
    private readonly string _originalConfigPath = SystemConfigHelper.configPath;
    private readonly (string Key, string Issuer, string Audience) _originalConsts =
        (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience);
    private readonly ConcurrentDictionary<string, TaskCommandStep> _originalSteps = MemoryObjectCache.TaskCommandSteps;
    private readonly Func<string, string, string, Task> _originalMover = MessageProcess.MessageMover;
    private readonly ConcurrentQueue<(string MessageId, string? From, string To, string? Route)> _moves = new();

    public MessageSourceRoutingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "quantum-source-routing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // 先写完整的纯测试配置再切换路径，避免常驻消息泵抢先把空文件初始化为另一个随机账号。
        var configPath = Path.Combine(_tempDir, "appsettings.json");
        var config = new Setting
        {
            UserName = Account, PassWord = "test-only-password", DBType = "Sqlite",
            DBAddress = "source-routing-tests.db", Port = 5088, Host = "http://*",
            MaxConcurrentTasks = 8, MessageQueueInterval = 100, MessageInterval = 1,
            SymmetricSecurityKey = new string('r', 64),
            SecurityAudience = "source-routing-tests", SecurityIssuer = "source-routing-tests",
            ChannelMasterKey = Convert.ToBase64String(new byte[32])
        };
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(new { Quantum = config }));
        SystemConfigHelper.configPath = configPath;
        MemoryObjectCache.TaskCommandSteps = new();
        CacheManager.Set(new List<TaskModel>());
        CacheManager.Set(new List<TaskSubModel>());
        CacheManager.Set(new List<EnvModel>());
        CacheManager.Set(new List<CommandModel>());
        MessageProcess.MessageMover = (id, from, to) =>
        {
            _moves.Enqueue((id, from, to, ChannelReplyContext.Current));
            return Task.CompletedTask;
        };
    }

    public void Dispose()
    {
        MessageProcess.MessageMover = _originalMover;
        MemoryObjectCache.TaskCommandSteps = _originalSteps;
        CacheManager.Set(new List<TaskModel>());
        CacheManager.Set(new List<TaskSubModel>());
        CacheManager.Set(new List<EnvModel>());
        CacheManager.Set(new List<CommandModel>());
        SystemConfigHelper.configPath = _originalConfigPath;
        (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience) = _originalConsts;
        // 常驻消息泵可能正在读取旧配置路径；清理只限本用例临时目录，不把文件竞态当业务失败。
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quantum-source-routing-tests"))
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(_tempDir);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("测试临时目录越界");
        try { Directory.Delete(target, recursive: true); }
        catch (IOException) { /* 后台读取竞态，留给系统临时目录清理。 */ }
        catch (UnauthorizedAccessException) { /* 仅临时文件清理，不影响路由断言。 */ }
    }

    [Theory]
    [InlineData(ChannelManagementService.Qq)]
    [InlineData(ChannelManagementService.Weixin)]
    [InlineData(ChannelManagementService.Feishu)]
    public async Task AppMessage_DoesNotContinueBotWaitingStep(string platform)
    {
        var step = await StartWaitingTaskAsync(platform);
        _moves.Clear();

        await new MessageProcess().MessageAsync(Message("source-routing-continue"));

        Assert.False(step.IsChildTask);
        Assert.True(step.HasChildTask);
        Assert.Equal("bot-task-next-step", step.NextSubTaskId);
        Assert.DoesNotContain(step.Envs, x => x.Name == "source_routing_answer");
        Assert.Empty(_moves);
    }

    [Theory]
    [InlineData(ChannelManagementService.Qq)]
    [InlineData(ChannelManagementService.Weixin)]
    [InlineData(ChannelManagementService.Feishu)]
    public async Task AppCommand_IsNotBlockedByBotWaitingStep(string platform)
    {
        var step = await StartWaitingTaskAsync(platform);
        var appTask = TaskModel("app-task", "source-routing-app");
        CacheManager.Set(new List<TaskModel> { step.Task, appTask });
        _moves.Clear();

        await new MessageProcess().MessageAsync(Message(appTask.Command));

        Assert.Equal(appTask.Id, Assert.Single(_moves).To);
        Assert.False(step.IsChildTask);
    }

    [Theory]
    [InlineData(ChannelManagementService.Qq)]
    [InlineData(ChannelManagementService.Weixin)]
    [InlineData(ChannelManagementService.Feishu)]
    public async Task AppQuit_DoesNotRemoveBotWaitingStep(string platform)
    {
        var step = await StartWaitingTaskAsync(platform);

        await new MessageProcess().MessageAsync(Message("q"));

        Assert.True(MemoryObjectCache.TaskCommandSteps.ContainsKey(step.ThreadId));
    }

    [Fact]
    public async Task AppCommand_ClearsInheritedBotReplyRoute_AndRestoresCallerContext()
    {
        var appTask = TaskModel("app-task", "source-routing-app");
        CacheManager.Set(new List<TaskModel> { appTask });
        using (ChannelReplyContext.Use("stale-bot-route"))
        {
            await new MessageProcess().MessageAsync(Message(appTask.Command));
            Assert.Equal("stale-bot-route", ChannelReplyContext.Current);
        }

        Assert.Null(Assert.Single(_moves).Route);
        Assert.Null(ChannelReplyContext.Current);
    }

    [Fact]
    public async Task BotCommand_UsesItsOwnReplyRoute_NotInheritedRoute()
    {
        var task = TaskModel("bot-task", "source-routing-bot");
        CacheManager.Set(new List<TaskModel> { task });
        using (ChannelReplyContext.Use("stale-bot-route"))
        {
            await new MessageProcess().MessageAsync(Message(task.Command, ChannelManagementService.Weixin, "incoming-bot-route"));
            Assert.Equal("stale-bot-route", ChannelReplyContext.Current);
        }

        Assert.Equal("incoming-bot-route", Assert.Single(_moves).Route);
        Assert.Null(ChannelReplyContext.Current);
    }

    [Theory]
    [InlineData(ChannelManagementService.Qq)]
    [InlineData(ChannelManagementService.Weixin)]
    [InlineData(ChannelManagementService.Feishu)]
    public async Task SameBotPeer_ContinuesStep_UsingLatestMessageRoute(string platform)
    {
        var step = await StartWaitingTaskAsync(platform);
        _moves.Clear();

        await new MessageProcess().MessageAsync(Message("source-routing-continue", platform, "bot-next-route"));

        Assert.True(step.IsChildTask);
        Assert.False(step.HasChildTask);
        Assert.Equal("bot-task-next-step", step.CurrentSubTaskId);
        Assert.Contains(step.Envs, x => x.Name == "source_routing_answer" && x.Value == "source-routing-continue");
        Assert.Equal("bot-next-route", Assert.Single(_moves).Route);
        Assert.Equal(Account, step.UserId); // 脚本 user_id 不改变，只隔离任务等待归属。
    }

    [Theory]
    [InlineData(ChannelManagementService.Qq, ChannelManagementService.Weixin)]
    [InlineData(ChannelManagementService.Weixin, ChannelManagementService.Feishu)]
    [InlineData(ChannelManagementService.Feishu, ChannelManagementService.Qq)]
    public async Task DifferentBotPlatform_DoesNotContinueWaitingStep(string source, string incoming)
    {
        var step = await StartWaitingTaskAsync(source);
        _moves.Clear();

        await new MessageProcess().MessageAsync(Message("source-routing-continue", incoming, "other-route"));

        Assert.False(step.IsChildTask);
        Assert.Empty(_moves);
    }

    [Fact]
    public async Task DifferentPrivatePeer_DoesNotContinueWaitingStep()
    {
        var step = await StartWaitingTaskAsync(ChannelManagementService.Weixin);
        _moves.Clear();

        await new MessageProcess().MessageAsync(Message("source-routing-continue", ChannelManagementService.Weixin,
            "other-peer-route", peer: "another-peer"));

        Assert.False(step.IsChildTask);
        Assert.Empty(_moves);
    }

    [Fact]
    public async Task ChangedAccountVersion_DoesNotContinueOldWaitingStep()
    {
        var step = await StartWaitingTaskAsync(ChannelManagementService.Weixin);
        _moves.Clear();

        await new MessageProcess().MessageAsync(Message("source-routing-continue", ChannelManagementService.Weixin,
            "reconfigured-route", version: 2));

        Assert.False(step.IsChildTask);
        Assert.Empty(_moves);
    }

    [Fact]
    public async Task BotMessage_DoesNotContinueAppWaitingStep()
    {
        var step = await StartWaitingTaskAsync();
        _moves.Clear();

        await new MessageProcess().MessageAsync(Message("source-routing-continue", ChannelManagementService.Weixin, "bot-route"));

        Assert.False(step.IsChildTask);
        Assert.Empty(_moves);
    }

    [Fact]
    public async Task BotQuit_OnlyRemovesItsOwnWaitingStep()
    {
        var botStep = await StartWaitingTaskAsync(ChannelManagementService.Weixin);
        var appStep = await StartWaitingTaskAsync(taskId: "app-wait-task");

        await new MessageProcess().MessageAsync(Message("q", ChannelManagementService.Weixin, "bot-quit-route"));

        Assert.False(MemoryObjectCache.TaskCommandSteps.ContainsKey(botStep.ThreadId));
        Assert.True(MemoryObjectCache.TaskCommandSteps.ContainsKey(appStep.ThreadId));
    }

    [Fact]
    public async Task AppQuit_OnlyRemovesAppWaitingStep()
    {
        var botStep = await StartWaitingTaskAsync(ChannelManagementService.Weixin);
        var appStep = await StartWaitingTaskAsync(taskId: "app-wait-task");

        await new MessageProcess().MessageAsync(Message("q"));

        Assert.True(MemoryObjectCache.TaskCommandSteps.ContainsKey(botStep.ThreadId));
        Assert.False(MemoryObjectCache.TaskCommandSteps.ContainsKey(appStep.ThreadId));
    }

    [Fact]
    public async Task LegacyAppStepWithoutActorKey_StillContinuesFromAnotherTaskSession()
    {
        var step = await StartWaitingTaskAsync();
        step.ActorKey = null;
        var message = Message("source-routing-continue");
        message.SessionKey = "another-task-session";

        await new MessageProcess().MessageAsync(message);

        Assert.True(step.IsChildTask);
        Assert.False(step.HasChildTask);
    }

    [Fact]
    public async Task AppClientSuppliedChannelSessionAndActor_CannotTakeOverBotStep()
    {
        var step = await StartWaitingTaskAsync(ChannelManagementService.Weixin);
        var message = Message("q");
        message.SessionKey = "channel:weixinbot";
        message.ChannelActorKey = step.ActorKey; // 没有受信回复路由，仍按 App 处理。

        await new MessageProcess().MessageAsync(message);

        Assert.True(MemoryObjectCache.TaskCommandSteps.ContainsKey(step.ThreadId));
    }

    [Fact]
    public async Task ConcurrentAppAndBotCommands_KeepIndependentReplyContexts()
    {
        var appTask = TaskModel("app-task", "source-routing-app");
        var botTask = TaskModel("bot-task", "source-routing-bot");
        CacheManager.Set(new List<TaskModel> { appTask, botTask });
        using (ChannelReplyContext.Use("stale-parent-route"))
        {
            await Task.WhenAll(
                Task.Run(() => new MessageProcess().MessageAsync(Message(appTask.Command))),
                Task.Run(() => new MessageProcess().MessageAsync(Message(botTask.Command, ChannelManagementService.Weixin, "bot-route"))));
            Assert.Equal("stale-parent-route", ChannelReplyContext.Current);
        }

        Assert.Null(Assert.Single(_moves, x => x.To == appTask.Id).Route);
        Assert.Equal("bot-route", Assert.Single(_moves, x => x.To == botTask.Id).Route);
        Assert.Null(ChannelReplyContext.Current);
    }

    [Fact]
    public void Clone_PreservesTrustedRouteAndActorMetadata()
    {
        var source = Message("source-routing-bot", ChannelManagementService.Weixin, "bot-route");

        var clone = source.Clone();

        Assert.Equal(source.ChannelReplyRouteId, clone.ChannelReplyRouteId);
        Assert.Equal(source.ChannelActorKey, clone.ChannelActorKey);
    }

    [Fact]
    public void JsonClients_CannotReadOrSetTrustedRouteAndActorMetadata()
    {
        const string json = """{"message":"q","ChannelReplyRouteId":"forged-route","ChannelActorKey":"forged-actor"}""";
        var source = Message("source-routing-bot", ChannelManagementService.Weixin, "bot-route");
        var fromNewtonsoft = Newtonsoft.Json.JsonConvert.DeserializeObject<MessageProccessDTO>(json)!;
        var fromSystemText = System.Text.Json.JsonSerializer.Deserialize<MessageProccessDTO>(json)!;

        Assert.Null(fromNewtonsoft.ChannelReplyRouteId);
        Assert.Null(fromNewtonsoft.ChannelActorKey);
        Assert.Null(fromSystemText.ChannelReplyRouteId);
        Assert.Null(fromSystemText.ChannelActorKey);
        Assert.DoesNotContain("ChannelReplyRouteId", Newtonsoft.Json.JsonConvert.SerializeObject(source));
        Assert.DoesNotContain("ChannelActorKey", Newtonsoft.Json.JsonConvert.SerializeObject(source));
        Assert.DoesNotContain("ChannelReplyRouteId", System.Text.Json.JsonSerializer.Serialize(source));
        Assert.DoesNotContain("ChannelActorKey", System.Text.Json.JsonSerializer.Serialize(source));
    }

    [Fact]
    public async Task NotifyFacade_AppAndBotMessages_KeepTaskSessions_AndOnlyBotGetsOutbox()
    {
        var appTask = TaskModel("app-task", "source-routing-app");
        var botTask = TaskModel("bot-task", "source-routing-bot");
        CacheManager.Set(new List<TaskModel> { appTask, botTask });
        var captured = new ConcurrentQueue<MessageProccessDTO>();
        var originalSender = SendMessageHelper.Sender;
        var sentinel = "source-routing-notify-" + Guid.NewGuid().ToString("N");
        SendMessageHelper.Sender = message =>
        {
            if (message.message.StartsWith(sentinel, StringComparison.Ordinal)) captured.Enqueue(message);
            return Task.CompletedTask;
        };
        MessageProcess.MessageMover = async (_, _, to) =>
            await new QuantumNotifyFacade(new NotifyService(), to, to).SendAsync("回执", sentinel + to);
        try
        {
            await new MessageProcess().MessageAsync(Message(botTask.Command, ChannelManagementService.Weixin, "bot-route"));
            await new MessageProcess().MessageAsync(Message(appTask.Command));
            for (var i = 0; captured.Count < 2 && i < 200; i++) await Task.Delay(10);
            Assert.Equal(2, captured.Count);
        }
        finally
        {
            SendMessageHelper.Sender = originalSender;
        }

        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var account = new ChannelAccountModel { Id = "account-weixin", Platform = ChannelManagementService.Weixin,
                BotId = "test-bot", Enabled = true, BindingVersion = 1 };
            db.ChannelAccounts.Add(account);
            db.ChannelReplyRoutes.Add(new ChannelReplyRouteModel { Id = "bot-route", AccountId = account.Id,
                PeerId = "test-peer", ReplyMessageId = "bot-incoming-message",
                BindingVersion = 1, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) });
            await db.SaveChangesAsync();
            var push = new AppPushService(db, new AppMessageService(db, NullLogger<AppMessageService>.Instance),
                new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance), NullLogger<AppPushService>.Instance);
            var replies = new ChannelReplyService(db, new ChannelAccountGate());
            foreach (var message in captured)
            {
                await push.SendChatMessageAsync(message.message, sessionKey: message.SessionKey);
                if (message.ChannelReplyRouteId != null)
                    await replies.QueueAsync(message.ChannelReplyRouteId, message.message);
            }

            var rows = await db.ChatMessages.AsNoTracking().ToListAsync();
            Assert.Equal(appTask.Id, Assert.Single(rows, x => x.Content == sentinel + appTask.Id).SessionKey);
            Assert.Equal(botTask.Id, Assert.Single(rows, x => x.Content == sentinel + botTask.Id).SessionKey);
            var outbox = Assert.Single(await db.ChannelOutboxes.AsNoTracking().ToListAsync());
            Assert.Equal("bot-route", outbox.ReplyRouteId);
            Assert.Equal(sentinel + botTask.Id, outbox.Content);
        }
    }

    private async Task<TaskCommandStep> StartWaitingTaskAsync(string? platform = null, string taskId = "bot-task")
    {
        var task = TaskModel(taskId, "source-routing-start-" + taskId);
        CacheManager.Set(CacheManager.Get<TaskModel>().Append(task).ToList());
        CacheManager.Set(CacheManager.Get<TaskSubModel>().Append(new TaskSubModel
        {
            Id = taskId + "-next-step", TaskId = task.Id, Name = "等待后续输入",
            Command = "source-routing-continue", CommandEnv = "source_routing_answer", Sort = 1
        }).ToList());
        await new MessageProcess().MessageAsync(Message(task.Command, platform, platform == null ? null : "bot-start-route"));
        var step = Assert.Single(MemoryObjectCache.TaskCommandSteps.Values, x => x.Task.Id == task.Id);
        Assert.True(step.HasChildTask);
        return step;
    }

    private static TaskModel TaskModel(string id, string command) => new()
    {
        Id = id, Name = id, Command = command, Enable = true,
        // 不存在的测试脚本产生确定的拒绝终态，不执行外部副作用、不连接真实数据库。
        FileName = "source_routing_definitely_missing.cs"
    };

    private static MessageProccessDTO Message(string content, string? platform = null, string? route = null,
        string peer = "test-peer", int version = 1) => new()
    {
        CommunicationType = CommunicationType.App,
        user_id = Account,
        message = content,
        message_id = Guid.NewGuid().ToString("N"),
        SessionKey = platform == null ? null : "channel:" + platform.ToLowerInvariant(),
        ChannelReplyRouteId = route,
        ChannelActorKey = platform == null ? null : ChannelCommandWorker.ResolveActorKey(
            new ChannelAccountModel { Id = "account-" + platform, Platform = platform, BindingVersion = version }, peer)
    };
}
