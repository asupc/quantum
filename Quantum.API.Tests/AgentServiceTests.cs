using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Web.Controllers;
using Quantum.Web.Filters;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// AI 脚本修复 Agent（2026-09-20 计划阶段三/四）回归：
/// 工具面（白名单/路径越狱/只读保证/截断）、上下文与 JSON 协议容错（含上下文超长压缩重试，仅兜一次）、
/// 工具循环收敛与轮数上限、提案生成 → 应用（基线哈希复校 + 门禁复校 + 版本留痕）→ 过期/被拒路径、
/// 影子试运行（真执行 + 影子清理）。
/// </summary>
[Collection("ConstsState")]
public class AgentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly FakeLlmClient _llm = new();
    private readonly AiProviderService _providerService;
    private readonly ScriptVersionService _versionService;
    private readonly AgentTestRunService _testRunService;
    private readonly AgentProposalService _proposalService;
    private readonly AgentService _service;

    public AgentServiceTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _providerService = new AiProviderService(_db, _llm);
        _versionService = new ScriptVersionService(_db);
        _testRunService = new AgentTestRunService(_db);
        _proposalService = new AgentProposalService(_db, _versionService, _testRunService);
        _service = new AgentService(_db, _providerService, _llm, null!, _proposalService, _testRunService);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private const string TaskSource = """
        using Quantum.Plugins;
        public class AgentLoopTask : IQuantumTask
        {
            public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
            {
                ctx.Log("agent loop test");
                return Task.CompletedTask;
            }
        }
        """;

    // ==================================================================== 工具面

    [Fact]
    public void Tools_SchemaIsValidAndUnique()
    {
        // 全关实例（不传写权限）锁定默认行为：工具集与写权限扩展前完全一致，不含任何写工具
        var names = new List<string>();
        foreach (var tool in NewToolbox().Tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.NotNull(JObject.Parse(tool.ParametersJson));
            names.Add(tool.Name);
        }
        Assert.Equal(11, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
        // 全关实例下不存在任何写工具（写能力只能由 t_ai_setting 四开关显式开启后拼入）
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("run_shell", names);
        Assert.DoesNotContain("apply_fix", names);
        Assert.DoesNotContain("delete_script", names);
        Assert.DoesNotContain("save_task", names);
        Assert.DoesNotContain("delete_task", names);
        Assert.DoesNotContain("save_env", names);
        Assert.DoesNotContain("delete_env", names);
        Assert.DoesNotContain("save_custom_data_title", names);
        Assert.DoesNotContain("delete_custom_data_title", names);
        Assert.DoesNotContain("save_custom_data", names);
        Assert.DoesNotContain("update_custom_data", names);
        Assert.DoesNotContain("delete_custom_data", names);
        Assert.DoesNotContain("list_tasks", names);
        Assert.Contains("propose_fix", names);
    }

    [Fact]
    public async Task Toolbox_UnknownTool_Rejected()
    {
        var box = NewToolbox();
        var result = await box.InvokeAsync("delete_script", []);
        Assert.Contains("不支持的工具", result);
    }

    [Fact]
    public async Task Toolbox_ReadScript_BlocksTraversalAndReportsMissing()
    {
        var box = NewToolbox();
        Assert.Contains("脚本不存在", await box.InvokeAsync("read_script", new JObject { ["file"] = "../appsettings.json" }));
        Assert.Contains("脚本不存在", await box.InvokeAsync("read_script", new JObject { ["file"] = "no_such_script.cs" }));
    }

    [Fact]
    public async Task Toolbox_ReadScript_RangeAndLineNumbers()
    {
        WriteScript("agent_tool_read.cs", TaskSource);
        var box = NewToolbox();
        var text = await box.InvokeAsync("read_script", new JObject { ["file"] = "agent_tool_read.cs", ["startLine"] = 2, ["endLine"] = 3 });
        Assert.Contains("第 2-3 行", text);
        Assert.Contains("    2| public class AgentLoopTask", text);
        Assert.DoesNotContain("ctx.Log", text);
    }

    [Fact]
    public async Task Toolbox_ListScripts_HidesStagingDir()
    {
        WriteScript("agent_tool_list.cs", TaskSource);
        WriteScript("agent-tmp/shadow-hidden.cs", TaskSource);
        var box = NewToolbox();
        var text = await box.InvokeAsync("list_scripts", []);
        Assert.Contains("agent_tool_list.cs", text);
        Assert.DoesNotContain("agent-tmp", text);

        // 脚本编辑页的树同样排除该目录，且任务不允许绑定影子文件
        var taskService = new TaskService(_db,
            new AppMessageService(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AppMessageService>.Instance), _versionService);
        Assert.DoesNotContain(taskService.GetScriptList().SelectMany(Flatten).Select(n => n.path ?? string.Empty),
            n => n.Contains("agent-tmp"));
        Assert.Throws<Quantum.Utils.BusinessException>(() => TaskService.ValidateScriptFileName("agent-tmp/shadow-hidden.cs"));

        File.Delete(ScriptPath("agent-tmp/shadow-hidden.cs"));
    }

    private static IEnumerable<Entities.DTOs.ScriptsFile> Flatten(Entities.DTOs.ScriptsFile node)
    {
        yield return node;
        foreach (var child in node.children ?? [])
        {
            foreach (var item in Flatten(child))
            {
                yield return item;
            }
        }
    }

    [Fact]
    public async Task Toolbox_CompileScript_ReportsGateAndCompileDiagnostics()
    {
        var box = NewToolbox();
        var blocked = await box.InvokeAsync("compile_script", new JObject
        {
            ["file"] = "agent_tool_compile.cs",
            ["source"] = "using System.IO;\nusing Quantum.Plugins;\npublic class X : IQuantumTask { public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct) { File.ReadAllText(\"c:/x\"); return Task.CompletedTask; } }"
        });
        Assert.Contains("校验未通过", blocked);
        Assert.Contains("门禁", blocked);

        var ok = await box.InvokeAsync("compile_script", new JObject
        {
            ["file"] = "agent_tool_compile.cs",
            ["source"] = TaskSource
        });
        Assert.Contains("校验通过", ok);
    }

    [Fact]
    public async Task Toolbox_ListEnvs_MasksValuesByDefault()
    {
        _db.Envs.Add(new EnvModel { Name = "SECRET_TOKEN", Value = "abcdefghijklmnop", Enable = true, Remark = "测试", CreateTime = DateTime.Now });
        await _db.SaveChangesAsync();

        var masked = await NewToolbox().InvokeAsync("list_envs", []);
        Assert.Contains("SECRET_TOKEN", masked);
        Assert.DoesNotContain("abcdefghijklmnop", masked);
        Assert.Contains("已隐藏", masked);

        var explicitAllow = new AgentToolbox(_db, true, "c1", _proposalService, _testRunService, AiTestRunNotifyMode.Task);
        var plain = await explicitAllow.InvokeAsync("list_envs", []);
        Assert.Contains("abcdefghijklmnop", plain);
    }

    [Fact]
    public async Task Toolbox_QueryCustomData_ReturnsRowsAndTitles()
    {
        _db.CustomDataTitles.Add(new CustomDataTitleModel { Type = "agent_seen", TypeName = "去重记录", Title1 = "键", Title2 = "值" });
        _db.CustomDatas.Add(new CustomDataModel { Type = "agent_seen", Data1 = "abc", Data2 = "1", CreateTime = DateTime.Now });
        await _db.SaveChangesAsync();

        var text = await NewToolbox().InvokeAsync("query_custom_data", new JObject { ["type"] = "agent_seen" });
        Assert.Contains("去重记录", text);
        Assert.Contains("abc", text);
    }

    [Fact]
    public async Task Toolbox_ReadRunLog_NoLogDirectory_ReportsClearly()
    {
        var text = await NewToolbox().InvokeAsync("read_run_log", new JObject { ["file"] = "agent_no_log.cs" });
        Assert.Contains("执行日志", text);
    }

    [Fact]
    public async Task Toolbox_ListCapabilities_ContainsContractAndGateRules()
    {
        var text = await NewToolbox().InvokeAsync("list_capabilities", []);
        Assert.Contains("IQuantumTask", text);
        Assert.Contains("ctx.Notify", text);
        Assert.Contains("System.IO", text);
    }

    // ==================================================================== 纯函数

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("hello", 2)]
    public void EstimateTokens_Boundaries(string text, int expected)
    {
        Assert.Equal(expected, AgentService.EstimateTokens(text));
    }

    [Fact]
    public void EstimateTokens_CjkCostsMoreThanAscii()
    {
        var cjk = AgentService.EstimateTokens(new string('中', 100));
        var ascii = AgentService.EstimateTokens(new string('a', 100));
        Assert.True(cjk > ascii);
        Assert.InRange(cjk, 60, 90);
    }

    [Fact]
    public void EstimateBudget_KeepsHeadroomForOutput()
    {
        var budget = AgentService.EstimateBudget(new AiModelModel { ContextWindow = 32768, MaxOutputTokens = 4096 });
        Assert.InRange(budget, 20000, 25000);
    }

    [Fact]
    public void ParseArguments_ToleratesFencesAndBrokenJson()
    {
        Assert.Equal("a.cs", AgentService.ParseArguments("""{"file":"a.cs"}""")["file"].Value<string>());
        Assert.Equal("a.cs", AgentService.ParseArguments("```json\n{\"file\":\"a.cs\"}\n```")["file"].Value<string>());
        Assert.Equal("a.cs", AgentService.ParseArguments("前面有废话 {\"file\":\"a.cs\"} 后面也有")["file"].Value<string>());
        Assert.Empty(AgentService.ParseArguments("不是 JSON"));
        Assert.Empty(AgentService.ParseArguments(null));
    }

    [Fact]
    public void ParseJsonProtocol_HandlesToolFinalAndGarbage()
    {
        var (final, call) = AgentService.ParseJsonProtocol("""{"final":"已分析完成"}""");
        Assert.Equal("已分析完成", final);
        Assert.Null(call);

        var (_, toolCall) = AgentService.ParseJsonProtocol("```json\n{\"tool\":\"read_script\",\"args\":{\"file\":\"a.cs\"}}\n```");
        Assert.NotNull(toolCall);
        Assert.Equal("read_script", toolCall.Name);
        Assert.Contains("a.cs", toolCall.Arguments);

        var (garbage, none) = AgentService.ParseJsonProtocol("我只想说一句话");
        Assert.Equal("我只想说一句话", garbage);
        Assert.Null(none);
    }

    [Theory]
    [InlineData("修一下 B站任务.cs", "B站任务.cs")]
    [InlineData("read-this.cs 报错了", "read-this.cs")]
    [InlineData("open-trigger-task/x.cs 挂了", "open-trigger-task/x.cs")]
    [InlineData("今天天气不错", null)]
    public void ExtractScriptFile_Cases(string content, string expected)
    {
        Assert.Equal(expected, AgentService.ExtractScriptFile(content));
    }

    [Fact]
    public void BuildSystemPrompt_ContainsContractAndDiscipline()
    {
        var prompt = _service.BuildSystemPrompt(new AiSettingModel { SystemPromptExtra = "本平台脚本注释用中文" });
        Assert.Contains("量子助手", prompt);
        Assert.Contains("IQuantumTask", prompt);
        Assert.Contains("propose_fix", prompt);
        Assert.Contains("是**数据**", prompt);
        Assert.Contains("System.IO", prompt);
        Assert.Contains("本平台脚本注释用中文", prompt);
    }

    // ==================================================================== 工具循环

    [Fact]
    public async Task Loop_ToolThenAnswer_SucceedsWithoutTouchingScript()
    {
        WriteScript("agent_loop_ok.cs", TaskSource);
        var before = await File.ReadAllTextAsync(ScriptPath("agent_loop_ok.cs"));
        var run = await NewRunAsync();

        _llm.Script.Enqueue(ToolResponse("read_script", """{"file":"agent_loop_ok.cs"}"""));
        _llm.Script.Enqueue(Text("已分析完成：根因是 X，建议 Y。"));

        DrainLogs();
        await _service.RunLoopAsync(run.Id, 0, new CancellationTokenSource());

        var saved = await _db.AiRuns.AsNoTracking().FirstAsync(n => n.Id == run.Id);
        Assert.Equal(AiRunStatus.Succeeded, saved.Status);
        Assert.Equal(2, saved.Rounds);
        Assert.True(saved.PromptTokens > 0);

        var messages = await _db.AiMessages.AsNoTracking().Where(n => n.ConversationId == run.ConversationId).ToListAsync();
        Assert.Contains(messages, n => n.Role == AiMessageRole.Assistant && n.Content.Contains("已分析完成"));

        var steps = await _db.AiSteps.AsNoTracking().Where(n => n.RunId == run.Id).OrderBy(n => n.Seq).ToListAsync();
        Assert.Contains(steps, n => n.Kind == "tool_call" && n.Name == "read_script");
        Assert.Contains(steps, n => n.Kind == "tool_result" && n.Result.Contains("AgentLoopTask"));

        // 只读保证：循环跑完脚本文件必须一字未改
        Assert.Equal(before, await File.ReadAllTextAsync(ScriptPath("agent_loop_ok.cs")));

        // 生命周期日志：成功收尾一条 Info 级「AI 运行结束」（LogType=AI助手），带状态与工具摘要
        var end = Assert.Single(TakeLogs(), n => n.Title == "AI 运行结束");
        Assert.Equal(LogSeverity.Info, end.Severity);
        Assert.Equal(LogType.AI助手, end.LogType);
        Assert.Equal("AI", end.Operator);
        Assert.Contains("Succeeded", end.Remark);
        Assert.Contains("read_script×1", end.Remark);
    }

    [Fact]
    public async Task Loop_ExceedsMaxRounds_FailsWithHint()
    {
        WriteScript("agent_loop_rounds.cs", TaskSource);
        await SetMaxRoundsAsync(2);
        var run = await NewRunAsync();

        for (var i = 0; i < 5; i++)
        {
            _llm.Script.Enqueue(ToolResponse("read_script", """{"file":"agent_loop_rounds.cs"}"""));
        }

        DrainLogs();
        await _service.RunLoopAsync(run.Id, 0, new CancellationTokenSource());

        var saved = await _db.AiRuns.AsNoTracking().FirstAsync(n => n.Id == run.Id);
        Assert.Equal(AiRunStatus.Failed, saved.Status);
        Assert.Equal(2, saved.Rounds);
        var message = await _db.AiMessages.AsNoTracking()
            .Where(n => n.ConversationId == run.ConversationId).OrderByDescending(n => n.Seq).FirstAsync();
        Assert.Contains("最大轮数", message.Content);

        // 收敛失败（FinishAsync 路径）：Warn 级「AI 运行结束」
        var end = Assert.Single(TakeLogs(), n => n.Title == "AI 运行结束");
        Assert.Equal(LogSeverity.Warn, end.Severity);
        Assert.Contains("Failed", end.Remark);
    }

    [Fact]
    public async Task Loop_ProposeFix_CreatesProposalMessageAndWaitsConfirm()
    {
        WriteScript("agent_loop_propose.cs", TaskSource);
        var run = await NewRunAsync();
        var improved = TaskSource.Replace("agent loop test", "agent loop test v2");

        _llm.Script.Enqueue(ToolResponse("propose_fix", new JObject
        {
            ["file"] = "agent_loop_propose.cs",
            ["source"] = improved,
            ["summary"] = "在日志里补了 v2 标记"
        }.ToString()));

        DrainLogs();
        await _service.RunLoopAsync(run.Id, 0, new CancellationTokenSource());

        var saved = await _db.AiRuns.AsNoTracking().FirstAsync(n => n.Id == run.Id);
        Assert.Equal(AiRunStatus.WaitingConfirm, saved.Status);

        var proposal = await _db.AiProposals.AsNoTracking().FirstAsync();
        Assert.Equal("agent_loop_propose.cs", proposal.FileName);
        Assert.Equal(AiProposalStatus.Pending, proposal.Status);
        Assert.Equal(ScriptVersionService.HashOf(TaskSource), proposal.BaseHash);
        Assert.Equal(ScriptVersionService.HashOf(improved), proposal.NewHash);

        var message = await _db.AiMessages.AsNoTracking().FirstAsync(n => n.Kind == AiMessageKind.Proposal);
        Assert.Contains(proposal.Id, message.Payload);
        Assert.NotNull(proposal.MessageId);

        // 提案阶段同样不落盘
        Assert.Equal(ScriptVersionService.Normalize(TaskSource),
            ScriptVersionService.Normalize(await File.ReadAllTextAsync(ScriptPath("agent_loop_propose.cs"))));

        // 提案收尾不走 FinishAsync，但仍须补记 Info 级「AI 运行结束」（开始/结束成对）
        var end = Assert.Single(TakeLogs(), n => n.Title == "AI 运行结束");
        Assert.Equal(LogSeverity.Info, end.Severity);
        Assert.Contains("WaitingConfirm", end.Remark);
        Assert.Contains("propose_fix×1", end.Remark);
    }

    [Fact]
    public async Task Loop_JsonFallbackMode_RunsToolThenFinishes()
    {
        WriteScript("agent_loop_json.cs", TaskSource);
        var run = await NewRunAsync();
        // 供应商标记为不支持工具 → 走 JSON 协议
        var provider = await _db.AiProviders.FirstAsync();
        provider.SupportsTools = AiToolSupport.Unsupported;
        await _db.SaveChangesAsync();

        _llm.Script.Enqueue(Text("""{"tool":"read_script","args":{"file":"agent_loop_json.cs"}}"""));
        _llm.Script.Enqueue(Text("""{"final":"JSON 协议下也完成了分析"}"""));

        await _service.RunLoopAsync(run.Id, 0, new CancellationTokenSource());
        var saved = await _db.AiRuns.AsNoTracking().FirstAsync(n => n.Id == run.Id);
        Assert.Equal(AiRunStatus.Succeeded, saved.Status);
        Assert.Contains("JSON 协议", (await _db.AiMessages.AsNoTracking()
            .Where(n => n.ConversationId == run.ConversationId).OrderByDescending(n => n.Seq).FirstAsync()).Content);
        // JSON 协议下不带 tools 字段
        Assert.All(_llm.Requests, n => Assert.Null(n.Tools));
    }

    // ==================================================================== 运行恢复 / 并发隔离 / 日志

    [Fact]
    public async Task GetLatestRun_ReturnsNewestRun_OrNullForEmptyConversation()
    {
        var conversation = await _service.EnsureDefaultConversationAsync();
        // 空会话：null（页面据此清空运行态）
        Assert.Null(await _service.GetLatestRunAsync(conversation.Id));

        var older = new AiRunModel { ConversationId = conversation.Id, Status = AiRunStatus.Succeeded, CreateTime = DateTime.Now.AddMinutes(-5) };
        var running = new AiRunModel { ConversationId = conversation.Id, Status = AiRunStatus.Running, CreateTime = DateTime.Now };
        _db.AiRuns.AddRange(older, running);
        await _db.SaveChangesAsync();

        var latest = await _service.GetLatestRunAsync(conversation.Id);
        Assert.NotNull(latest);
        Assert.Equal(running.Id, latest.Id);
        Assert.Equal(AiRunStatus.Running, latest.Status);
    }

    [Fact]
    public async Task Start_SameConversationWhileRunning_RejectedWithoutUserMessage()
    {
        var conversation = await _service.EnsureDefaultConversationAsync();
        await EnsureProviderAsync();
        _db.AiRuns.Add(new AiRunModel
        {
            ConversationId = conversation.Id,
            TriggerType = AiRunTrigger.Manual,
            Status = AiRunStatus.Running,
            CreateTime = DateTime.Now
        });
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<Quantum.Utils.BusinessException>(
            () => _service.StartAsync(conversation.Id, "同会话再触发一个任务"));

        Assert.Contains("正在运行", ex.Message);
        // 被拒的用户消息不能落库（否则成孤儿，还会被下次运行的上下文带进去）
        Assert.False(await _db.AiMessages.AnyAsync(n => n.ConversationId == conversation.Id));
        // WaitingConfirm 不算占用：结束后（含提案等待）同会话可继续
        _db.AiRuns.Remove(await _db.AiRuns.FirstAsync(n => n.ConversationId == conversation.Id));
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Start_WaitingConfirm_DoesNotBlockNewRun()
    {
        var conversation = await _service.EnsureDefaultConversationAsync();
        await EnsureProviderAsync();
        _db.AiRuns.Add(new AiRunModel
        {
            ConversationId = conversation.Id,
            TriggerType = AiRunTrigger.Manual,
            Status = AiRunStatus.WaitingConfirm,
            CreateTime = DateTime.Now.AddMinutes(-1)
        });
        await _db.SaveChangesAsync();

        // WaitingConfirm 是提案等确认，不算占用——允许继续对话补充信息
        // （本用例只验证准入放行；后台循环经 null scopeFactory 无法在单测内执行，run 落库即返回）
        var ex = await Record.ExceptionAsync(() => _service.StartAsync(conversation.Id, "补充信息"));
        Assert.NotEqual("本会话有任务正在运行，请等它结束或先停止！", ex?.Message);
    }

    [Fact]
    public async Task Loop_ContextSnapshot_TriggerMessageIn_LaterMessagesExcluded()
    {
        WriteScript("agent_snap.cs", TaskSource);
        var conversation = await _service.EnsureDefaultConversationAsync();
        var trigger = await _service.AppendMessageAsync(conversation.Id, AiMessageRole.User, AiMessageKind.Text, "先分析 agent_snap.cs 为什么失败");
        // 触发点之后落库的第二条用户消息（模拟 run 排队期间另一任务的触发抢先落库）
        await _service.AppendMessageAsync(conversation.Id, AiMessageRole.User, AiMessageKind.Text, "后到的另一个任务：改 agent_snap2.cs");
        var run = await NewRunAsync();
        _llm.Script.Enqueue(Text("已分析完成"));

        await _service.RunLoopAsync(run.Id, trigger.Seq, new CancellationTokenSource());

        var request = _llm.Requests[0];
        // 触发消息必在（显式追加，不靠「最近 N 条碰巧含它」）
        Assert.Contains(request.Messages, n => n.Role == "user" && n.Content.Contains("先分析 agent_snap.cs"));
        // 触发点之后的消息不带进本轮上下文（后到的任务由它自己的 run 处理）
        Assert.DoesNotContain(request.Messages, n => n.Content != null && n.Content.Contains("后到的另一个任务"));
    }

    [Fact]
    public void CompactMessages_DropsHistory_TruncatesEarly_KeepsTriggerTailAndStructure()
    {
        var messages = new List<LlmMessage>
        {
            LlmMessage.System("系统提示"),
            LlmMessage.User("旧历史问题"),
            LlmMessage.Assistant("旧历史回答"),
            LlmMessage.User("触发消息"),
            new LlmMessage
            {
                Role = "assistant",
                ToolCalls = [new LlmToolCall { Id = "call_1", Name = "read_script", Arguments = "{}" }]
            },
            LlmMessage.Tool("call_1", new string('x', 5000)),
            LlmMessage.Tool("call_2", new string('y', 5000)),
            LlmMessage.Tool("call_3", "最近一条工具结果"),
            LlmMessage.Assistant("最近结论")
        };

        var compacted = AgentService.CompactMessages(messages, 4);

        // 系统提示与触发消息保留，历史整体丢弃
        Assert.Equal("系统提示", compacted[0].Content);
        Assert.Equal("触发消息", compacted[1].Content);
        Assert.DoesNotContain(compacted, n => n.Content == "旧历史问题");
        // 只删历史条目、run 内条目不删（role=tool 与 assistant.tool_calls 的配对结构不能断）
        Assert.Equal(messages.Count - 2, compacted.Count);
        // 较早的大体积工具结果截短并带压缩标记，最近两条原样
        var truncated = compacted.First(n => n.ToolCallId == "call_1");
        Assert.Contains("已压缩", truncated.Content);
        Assert.True(truncated.Content.Length < 1000);
        Assert.Equal("最近一条工具结果", compacted.First(n => n.ToolCallId == "call_3").Content);
        Assert.Equal("最近结论", compacted[^1].Content);
        // assistant 的工具调用声明原样保留（压缩只动 Content）
        var assistant = compacted.First(n => n.Role == "assistant" && n.ToolCalls is { Count: > 0 });
        Assert.Equal("call_1", assistant.ToolCalls[0].Id);
    }

    [Fact]
    public async Task Loop_ContextOverflow_CompactsAndRetries_Recovers()
    {
        var conversation = await _service.EnsureDefaultConversationAsync();
        await EnsureProviderAsync();
        await _service.AppendMessageAsync(conversation.Id, AiMessageRole.User, AiMessageKind.Text, "很早以前的问题");
        await _service.AppendMessageAsync(conversation.Id, AiMessageRole.Assistant, AiMessageKind.Text, "很早以前的回答");
        var trigger = await _service.AppendMessageAsync(conversation.Id, AiMessageRole.User, AiMessageKind.Text, "本轮触发消息");
        var run = await NewRunAsync();
        _llm.Exceptions.Enqueue(new LlmException(
            "供应商「Mock」调用失败（HTTP 400）：maximum context length exceeded", 400, contextOverflow: true));
        _llm.Script.Enqueue(Text("压缩后成功"));
        DrainLogs();

        await _service.RunLoopAsync(run.Id, trigger.Seq, new CancellationTokenSource());

        var saved = await _db.AiRuns.AsNoTracking().FirstAsync(n => n.Id == run.Id);
        Assert.Equal(AiRunStatus.Succeeded, saved.Status);
        // 压缩重试不消耗轮数（原轮重试），共两次请求
        Assert.Equal(1, saved.Rounds);
        Assert.Equal(2, _llm.Requests.Count);
        // 第二次请求：历史已丢、系统提示与触发消息保留
        var retry = _llm.Requests[1];
        Assert.Contains(retry.Messages, n => n.Role == "system");
        Assert.Contains(retry.Messages, n => n.Content != null && n.Content.Contains("本轮触发消息"));
        Assert.DoesNotContain(retry.Messages, n => n.Content != null && n.Content.Contains("很早以前"));
        // 压缩动作留系统日志（AI助手类）
        var warn = Assert.Single(TakeLogs(), n => n.Title == "AI 上下文超长，压缩后重试");
        Assert.Equal(LogType.AI助手, warn.LogType);
    }

    [Fact]
    public async Task Loop_ContextOverflow_SecondTime_FailsRunWithOriginalError()
    {
        var run = await NewRunAsync();
        _llm.Exceptions.Enqueue(new LlmException(
            "供应商「Mock」调用失败（HTTP 400）：maximum context length exceeded", 400, contextOverflow: true));
        _llm.Exceptions.Enqueue(new LlmException(
            "供应商「Mock」调用失败（HTTP 400）：maximum context length exceeded", 400, contextOverflow: true));

        await _service.RunLoopAsync(run.Id, 0, new CancellationTokenSource());

        var saved = await _db.AiRuns.AsNoTracking().FirstAsync(n => n.Id == run.Id);
        // 只兜一次：第二次仍溢出则整个 run 失败，报原始上下文错误（提示用户检查模型窗口配置）
        Assert.Equal(AiRunStatus.Failed, saved.Status);
        Assert.Contains("context length", saved.Error);
        Assert.Equal(2, _llm.Requests.Count);
        Assert.Equal(1, saved.Rounds);
    }

    [Fact]
    public void AllocateSeq_ConcurrentAllocation_AllUniqueSequential()
    {
        var conversation = _service.EnsureDefaultConversationAsync().GetAwaiter().GetResult();
        var allocated = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 16, _ =>
        {
            var message = new AiMessageModel
            {
                ConversationId = conversation.Id,
                Role = AiMessageRole.User,
                Kind = AiMessageKind.Text,
                Content = "并发分配"
            };
            allocated.Add(AgentService.AllocateSeq(_db, message));
        });
        // 锁内「DB max 与本地未提交 Added 行取大」：16 路并发拿到 1..16 唯一递增，无重号
        Assert.Equal(Enumerable.Range(1, 16).Select(n => (long)n), allocated.OrderBy(n => n));
    }

    [Fact]
    public async Task Loop_LlmException_WritesErrorLifecycleLog()
    {
        var run = await NewRunAsync();
        _llm.ThrowNext = true;

        DrainLogs();
        await _service.RunLoopAsync(run.Id, 0, new CancellationTokenSource());

        var saved = await _db.AiRuns.AsNoTracking().FirstAsync(n => n.Id == run.Id);
        Assert.Equal(AiRunStatus.Failed, saved.Status);

        var fail = Assert.Single(TakeLogs(), n => n.Title == "AI 运行失败");
        Assert.Equal(LogSeverity.Error, fail.Severity);
        Assert.Equal(LogType.AI助手, fail.LogType);
        Assert.False(fail.Success);
        Assert.Contains("模拟供应商中断", fail.Exception);
    }

    [Fact]
    public async Task Loop_SaveFullPrompt_FlagControlsDumpFiles()
    {
        var dir = Path.Combine("logs", "ai");
        // 默认关：不落盘
        var run1 = await NewRunAsync();
        _llm.Script.Enqueue(Text("关闭态结论"));
        await _service.RunLoopAsync(run1.Id, 0, new CancellationTokenSource());
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir, $"{run1.Id[..8]}_*.json").Length > 0);

        // 打开后：每轮一对请求/响应写 logs/ai/{run前8位}_{轮次}.json
        var setting = await _providerService.GetSettingAsync();
        setting.SaveFullPrompt = true;
        await _db.SaveChangesAsync();
        var run2 = await NewRunAsync();
        _llm.Script.Enqueue(Text("开启态结论"));
        await _service.RunLoopAsync(run2.Id, 0, new CancellationTokenSource());

        var files = Directory.GetFiles(dir, $"{run2.Id[..8]}_*.json");
        var file = Assert.Single(files);
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("ResponseRaw", text);
        Assert.Contains("开启态结论", text);
        Assert.Contains("system", text);
        File.Delete(file); // 清理测试产物，落盘目录不随仓库走
    }

    // ==================================================================== 提案应用

    [Fact]
    public async Task Apply_WritesFileAndRecordsAgentVersion()
    {
        WriteScript("agent_apply.cs", TaskSource);
        var proposal = await NewProposalAsync("agent_apply.cs", TaskSource.Replace("agent loop test", "v2"));

        var result = await _proposalService.ApplyAsync(proposal.Id, "admin");

        Assert.True(result.Success);
        Assert.Contains("v2", await File.ReadAllTextAsync(ScriptPath("agent_apply.cs")));
        var saved = await _db.AiProposals.AsNoTracking().FirstAsync(n => n.Id == proposal.Id);
        Assert.Equal(AiProposalStatus.Applied, saved.Status);
        var version = await _db.ScriptVersions.AsNoTracking().FirstAsync(n => n.FileName == "agent_apply.cs");
        Assert.Equal(ScriptVersionSource.AgentApply, version.Source);
        Assert.Equal(saved.AppliedVersionId, version.Id);
    }

    [Fact]
    public async Task Apply_WhenScriptChangedAfterProposal_MarksStaleAndRefuses()
    {
        WriteScript("agent_stale.cs", TaskSource);
        var proposal = await NewProposalAsync("agent_stale.cs", TaskSource.Replace("agent loop test", "v2"));
        // 提案生成后人工又改了一版
        WriteScript("agent_stale.cs", TaskSource.Replace("agent loop test", "人工改动"));

        var ex = await Assert.ThrowsAsync<Quantum.Utils.BusinessException>(() => _proposalService.ApplyAsync(proposal.Id, "admin"));

        Assert.Contains("已被改动", ex.Message);
        Assert.Contains("人工改动", await File.ReadAllTextAsync(ScriptPath("agent_stale.cs")));
        Assert.Equal(AiProposalStatus.Stale, (await _db.AiProposals.AsNoTracking().FirstAsync(n => n.Id == proposal.Id)).Status);
    }

    [Fact]
    public async Task Apply_BlockedCandidate_RejectedWithoutDiskWrite()
    {
        WriteScript("agent_blocked.cs", TaskSource);
        var conversation = await _service.EnsureDefaultConversationAsync();
        var proposal = await _proposalService.CreateAsync(conversation.Id, null, "agent_blocked.cs", """
            using System.IO;
            using Quantum.Plugins;
            public class X : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    File.ReadAllText("c:/secret");
                    return Task.CompletedTask;
                }
            }
            """, "故意的门禁用例", "{}");

        var result = await _proposalService.ApplyAsync(proposal.Id, "admin");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Blocked);
        Assert.Equal(ScriptVersionService.Normalize(TaskSource),
            ScriptVersionService.Normalize(await File.ReadAllTextAsync(ScriptPath("agent_blocked.cs"))));
        Assert.Equal(AiProposalStatus.Pending, (await _db.AiProposals.AsNoTracking().FirstAsync(n => n.Id == proposal.Id)).Status);
    }

    [Fact]
    public async Task Discard_MarksStatusAndAppendsMessage()
    {
        WriteScript("agent_discard.cs", TaskSource);
        var proposal = await NewProposalAsync("agent_discard.cs", TaskSource.Replace("agent loop test", "v2"));

        Assert.True(await _proposalService.DiscardAsync(proposal.Id));

        Assert.Equal(AiProposalStatus.Discarded, (await _db.AiProposals.AsNoTracking().FirstAsync(n => n.Id == proposal.Id)).Status);
        Assert.Equal(ScriptVersionService.Normalize(TaskSource),
            ScriptVersionService.Normalize(await File.ReadAllTextAsync(ScriptPath("agent_discard.cs"))));
    }

    // ==================================================================== 影子试运行

    [Fact]
    public async Task TestRun_ExecutesShadowAndCleansUp()
    {
        WriteScript("agent_testrun.cs", TaskSource);
        var proposal = await NewProposalAsync("agent_testrun.cs", TaskSource.Replace("agent loop test", "v2"));

        var result = await _testRunService.RunAsync(proposal.FileName, proposal.NewContent, AiTestRunNotifyMode.Silent);

        Assert.Equal(AiTestStatus.Passed, result.Status);
        Assert.False(result.HasException);
        Assert.NotNull(result.LogId);
        Assert.Contains("v2", result.LogTail);

        // 影子文件不残留（只断言本用例的产物；隐藏目录用例另有同名目录文件属正常），真实脚本一字未改
        var stagingDir = Path.Combine("scripts", "quantum", AgentTestRunService.StagingDir);
        var leftovers = Directory.Exists(stagingDir)
            ? Directory.GetFiles(stagingDir, "agent_testrun-*.cs")
            : [];
        Assert.Empty(leftovers);
        Assert.Equal(ScriptVersionService.Normalize(TaskSource),
            ScriptVersionService.Normalize(await File.ReadAllTextAsync(ScriptPath("agent_testrun.cs"))));

        // 试运行日志落 t_log（页面走既有日志详情端点看全文）
        var log = await _db.Logs.AsNoTracking().FirstAsync(n => n.Id == result.LogId);
        Assert.Equal(LogType.AI试运行, log.LogType);
        Assert.True(log.Success);
    }

    [Fact]
    public async Task TestRun_ProposalStatusAndMessageRecorded()
    {
        WriteScript("agent_testrun_msg.cs", TaskSource);
        var proposal = await NewProposalAsync("agent_testrun_msg.cs", TaskSource.Replace("agent loop test", "v2"));

        await _proposalService.TestRunAsync(proposal.Id, new AiSettingModel { TestRunNotifyMode = AiTestRunNotifyMode.Silent });

        var saved = await _db.AiProposals.AsNoTracking().FirstAsync(n => n.Id == proposal.Id);
        Assert.Equal(AiTestStatus.Passed, saved.TestStatus);
        Assert.NotNull(saved.TestRunLogId);
        Assert.Contains(await _db.AiMessages.AsNoTracking().ToListAsync(), n => n.Kind == AiMessageKind.TestResult);
    }

    // ==================================================================== 权限

    [Fact]
    public void AiAgentController_IsManagerOnly()
    {
        var type = typeof(AiAgentController);
        Assert.NotNull(type.GetCustomAttributes(typeof(ManagerOnlyAttribute), inherit: true).SingleOrDefault());
        Assert.NotNull(type.GetCustomAttributes(typeof(CustomAuthorizationFilter), inherit: true).SingleOrDefault());
    }

    // ==================================================================== 测试工具

    private static string ScriptPath(string fileName) => Path.Combine("scripts", "quantum", fileName);

    private static void WriteScript(string fileName, string content)
    {
        var path = ScriptPath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private AgentToolbox NewToolbox() => new(_db, false, "conv-tool", _proposalService, _testRunService, AiTestRunNotifyMode.Silent);

    private async Task<AiRunModel> NewRunAsync()
    {
        var conversation = await _service.EnsureDefaultConversationAsync();
        await EnsureProviderAsync();
        var run = new AiRunModel
        {
            ConversationId = conversation.Id,
            TriggerType = AiRunTrigger.Manual,
            Status = AiRunStatus.Running,
            CreateTime = DateTime.Now
        };
        _db.AiRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    private async Task EnsureProviderAsync()
    {
        var provider = await _providerService.SaveProviderAsync(new Entities.DTOs.AiProviderSaveModel
        {
            Name = "Mock",
            BaseUrl = "https://mock.local",
            ApiKey = "sk-mock-1234567890"
        });
        await _providerService.SaveModelAsync(new AiModelModel
        {
            ProviderId = provider.Id,
            ModelId = "mock-model",
            IsDefault = true
        });
    }

    /// <summary>清空 LogServiceHelper 全局队列：后台 AddLogs 线程只在生产启动，单测里队列跨用例残留。</summary>
    private static void DrainLogs()
    {
        while (LogServiceHelper.Logs.TryDequeue(out _))
        {
        }
    }

    private static List<LogModel> TakeLogs()
    {
        var logs = new List<LogModel>();
        while (LogServiceHelper.Logs.TryDequeue(out var log))
        {
            logs.Add(log);
        }
        return logs;
    }

    private async Task SetMaxRoundsAsync(int rounds)
    {
        var setting = await _providerService.GetSettingAsync();
        setting.MaxRounds = rounds;
        await _db.SaveChangesAsync();
    }

    private async Task<AiProposalModel> NewProposalAsync(string fileName, string source)
    {
        var conversation = await _service.EnsureDefaultConversationAsync();
        var build = ScriptBuildService.Build(source, ScriptPath(fileName));
        Assert.True(build.Success, "测试前置：候选源码本身必须能过校验");
        return await _proposalService.CreateAsync(conversation.Id, null, fileName, source, "测试提案", "{}");
    }

    private static LlmChatResponse ToolResponse(string name, string arguments) => new()
    {
        ToolCalls = [new LlmToolCall { Id = "call_" + Guid.NewGuid().ToString("N")[..6], Name = name, Arguments = arguments }],
        PromptTokens = 10,
        CompletionTokens = 5
    };

    private static LlmChatResponse Text(string content) => new() { Content = content, PromptTokens = 10, CompletionTokens = 5 };

    /// <summary>按脚本回放的假 LLM：队列空时返回一句收尾文本，避免测试悬挂；ThrowNext 模拟供应商异常。</summary>
    private sealed class FakeLlmClient : ILlmClient
    {
        public Queue<LlmChatResponse> Script { get; } = new();

        /// <summary>按次抛出的异常队列（优先于 ThrowNext）——模拟供应商特定故障形态（如上下文超长 400）</summary>
        public Queue<Exception> Exceptions { get; } = new();

        public List<LlmChatRequest> Requests { get; } = [];

        public bool ThrowNext { get; set; }

        public Task<List<string>> ListModelsAsync(AiProviderModel provider, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Exceptions.Count > 0)
            {
                throw Exceptions.Dequeue();
            }
            if (ThrowNext)
            {
                throw new LlmException("模拟供应商中断：connect timeout", 500);
            }
            return Task.FromResult(Script.Count > 0 ? Script.Dequeue() : Text("（脚本已耗尽）"));
        }
    }
}
