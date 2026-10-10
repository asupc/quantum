using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 会话内省略指令词（MessageProcess 入站⑤级兜底，2026-10-10）：
/// App/Web 在任务会话里手打裸消息（如会话「电影港」发「西游记」），等价于手打完整指令「电影港搜索 西游记」——
/// 指令环境变量收到拼回后的完整指令文本，脚本既有「剥离指令词取关键字」口径不受影响；
/// 显式命中（如回复资源ID）仍收原始消息；无指令脚本（纯定时）不被裸消息误触；不发生跨会话迁移。
/// </summary>
[Collection("ConstsState")]
public class ImplicitSessionCommandTests : IDisposable
{
    private const string Account = "implicit-session-test";
    private readonly string _tempDir;
    private readonly string _originalConfigPath = SystemConfigHelper.configPath;
    private readonly (string Key, string Issuer, string Audience) _originalConsts =
        (Consts.SymmetricSecurityKey, Consts.SecurityIssuer, Consts.SecurityAudience);
    private readonly ConcurrentDictionary<string, TaskCommandStep> _originalSteps = MemoryObjectCache.TaskCommandSteps;
    private readonly Func<string, string, string, Task> _originalMover = MessageProcess.MessageMover;
    private readonly ConcurrentQueue<(string MessageId, string From, string To)> _moves = new();

    public ImplicitSessionCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "quantum-implicit-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // 先写完整的纯测试配置再切换路径，避免常驻消息泵抢先把空文件初始化为另一个随机账号。
        var configPath = Path.Combine(_tempDir, "appsettings.json");
        var config = new Setting
        {
            UserName = Account, PassWord = "test-only-password", DBType = "Sqlite",
            DBAddress = "implicit-session-tests.db", Port = 5088, Host = "http://*",
            MaxConcurrentTasks = 8, MessageQueueInterval = 100, MessageInterval = 1,
            SymmetricSecurityKey = new string('r', 64),
            SecurityAudience = "implicit-session-tests", SecurityIssuer = "implicit-session-tests",
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
            _moves.Enqueue((id, from, to));
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
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quantum-implicit-session-tests"))
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(_tempDir);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("测试临时目录越界");
        try { Directory.Delete(target, recursive: true); }
        catch (IOException) { /* 后台读取竞态，留给系统临时目录清理。 */ }
        catch (UnauthorizedAccessException) { /* 仅临时文件清理，不影响路由断言。 */ }
    }

    [Fact]
    public async Task BareMessage_InSession_InjectsPrefixedCommandText_AndStaysInSession()
    {
        var (search, _) = SeedMovieHarbor();
        CacheManager.Set(new List<TaskSubModel>
        {
            new() { Id = "search-next", TaskId = search.Id, Name = "等待回复", Command = "^[0-9]+$", CommandEnv = "dygangs_id", Sort = 1 }
        });

        await new MessageProcess().MessageAsync(Message("西游记"));

        // 带子任务 → 步骤留存可查环境变量：指令变量收到拼回的完整指令文本
        var step = Assert.Single(MemoryObjectCache.TaskCommandSteps.Values);
        Assert.Equal(search.Id, step.Task.Id);
        Assert.Equal("电影港搜索 西游记", step.Envs.Single(n => n.Name == "dygangs_msg").Value);
        // 会话内触发不迁会话（任务会话 = 来源会话）
        Assert.Empty(_moves);
    }

    [Fact]
    public async Task ExplicitCommand_KeepsRawMessage_WithoutPrefix()
    {
        var (search, _) = SeedMovieHarbor();
        CacheManager.Set(new List<TaskSubModel>
        {
            new() { Id = "search-next", TaskId = search.Id, Name = "等待回复", Command = "^[0-9]+$", CommandEnv = "dygangs_id", Sort = 1 }
        });

        // 显式命中（资源ID回复走正则数字分支）：环境变量仍是用户原话，不拼指令词
        await new MessageProcess().MessageAsync(Message("45148"));

        var step = Assert.Single(MemoryObjectCache.TaskCommandSteps.Values);
        Assert.Equal("45148", step.Envs.Single(n => n.Name == "dygangs_msg").Value);
    }

    [Fact]
    public async Task BareMessage_SessionWithOnlyNoCommandTask_DoesNotTrigger()
    {
        var (_, watch) = SeedMovieHarbor();
        // 只留无指令脚本（纯定时）：裸消息不得误触
        CacheManager.Set(new List<TaskModel> { watch });

        await new MessageProcess().MessageAsync(Message("西游记"));

        Assert.Empty(MemoryObjectCache.TaskCommandSteps);
        Assert.Empty(_moves);
    }

    /// <summary>会话「电影港」：t-search 带触发指令（电影港搜索），t-watch 无指令（纯定时）。</summary>
    private static (TaskModel Search, TaskModel Watch) SeedMovieHarbor()
    {
        var search = new TaskModel
        {
            Id = "dygangs-search", Name = "电影港搜索", Command = "^电影港搜索.*|^[0-9,，\\s]+$", EnableRegex = true,
            CommandEnv = "dygangs_msg", SessionName = "电影港", Enable = true,
            // 不存在的测试脚本产生确定的拒绝终态，不执行外部副作用、不连接真实数据库。
            FileName = "implicit_session_definitely_missing.cs"
        };
        var watch = new TaskModel
        {
            Id = "dygangs-watch", Name = "电影港监控", Command = null,
            SessionName = "电影港", Enable = true,
            FileName = "implicit_session_definitely_missing.cs"
        };
        CacheManager.Set(new List<TaskModel> { search, watch });
        return (search, watch);
    }

    private static MessageProccessDTO Message(string content) => new()
    {
        CommunicationType = CommunicationType.App,
        user_id = Account,
        message = content,
        message_id = Guid.NewGuid().ToString("N"),
        SessionKey = "电影港"
    };
}
