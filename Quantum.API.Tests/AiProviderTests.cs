using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using Quantum.Web.Controllers;
using Quantum.Web.Filters;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// AI 供应商与模型配置（2026-09-20 AI 脚本修复 Agent 计划阶段二）回归：
/// BaseUrl 规范化与密钥掩码、供应商/模型 CRUD 与默认模型唯一性、连通性测试三步（含工具能力三态）、
/// 模型清单导入去重、全局设置的默认值与钳制、LLM 请求体组装与响应解析、控制器权限。
/// </summary>
[Collection("ConstsState")]
public class AiProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly FakeLlmClient _llm = new();
    private readonly AiProviderService _service;

    public AiProviderTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new AiProviderService(_db, _llm);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ==================================================================== 纯函数

    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1")]
    [InlineData("https://api.deepseek.com/", "https://api.deepseek.com/v1")]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4")]
    [InlineData("http://192.168.1.10:11434", "http://192.168.1.10:11434/v1")]
    public void NormalizeBaseUrl_Cases(string input, string expected)
    {
        Assert.Equal(expected, AiProviderService.NormalizeBaseUrl(input));
    }

    [Fact]
    public void MaskKey_KeepsHeadAndTail()
    {
        Assert.Equal("sk-a****cdef", AiProviderService.MaskKey("sk-abcdefghijklmncdef"));
        Assert.Equal("****", AiProviderService.MaskKey("short"));
        Assert.Equal(string.Empty, AiProviderService.MaskKey(null));
    }

    // ==================================================================== 供应商 CRUD

    [Fact]
    public async Task SaveProvider_New_RequiresKeyAndNormalizesUrl()
    {
        var created = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com/",
            ApiKey = "sk-abcdefghijklmnopqrst"
        });

        Assert.Equal("https://api.deepseek.com/v1", created.BaseUrl);
        Assert.Equal("sk-a****qrst", created.ApiKeyMasked);
        Assert.True(created.HasApiKey);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Name = "缺密钥",
            BaseUrl = "https://api.deepseek.com"
        }));
        Assert.Contains("密钥", ex.Message);
    }

    [Fact]
    public async Task SaveProvider_InvalidUrl_Rejected()
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(() => _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Name = "坏地址",
            BaseUrl = "not-a-url",
            ApiKey = "sk-abcdefghijklmnopqrst"
        }));
        Assert.Contains("BaseUrl", ex.Message);
    }

    [Fact]
    public async Task SaveProvider_EmptyKeyOnUpdate_KeepsExisting()
    {
        var created = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "sk-abcdefghijklmnopqrst"
        });

        var updated = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Id = created.Id,
            Name = "DeepSeek 改名",
            BaseUrl = "https://api.deepseek.com",
            ApiKey = null,
            MaxRetries = 3
        });

        Assert.Equal("DeepSeek 改名", updated.Name);
        Assert.Equal(3, updated.MaxRetries);
        Assert.True(updated.HasApiKey);
        Assert.Equal("sk-a****qrst", updated.ApiKeyMasked);
    }

    [Fact]
    public async Task Reveal_ReturnsPlainKey_AndThrowsWhenMissing()
    {
        var created = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "sk-abcdefghijklmnopqrst"
        });

        Assert.Equal("sk-abcdefghijklmnopqrst", await _service.RevealKeyAsync(created.Id));
        await Assert.ThrowsAsync<BusinessException>(() => _service.RevealKeyAsync("不存在的Id"));
    }

    [Fact]
    public async Task Delete_Provider_AlsoDeletesModels()
    {
        var created = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "sk-abcdefghijklmnopqrst"
        });
        await _service.ImportModelsAsync(new AiModelImportModel { ProviderId = created.Id, ModelIds = ["deepseek-chat"] });

        Assert.True(await _service.DeleteAsync([created.Id]));
        Assert.Empty(await _service.GetProvidersAsync());
        Assert.Empty(_db.AiModels);
    }

    // ==================================================================== 连通性测试

    [Fact]
    public async Task Test_Ok_SetsToolSupportAndPersistsResult()
    {
        var created = await CreateProvider();
        await _service.ImportModelsAsync(new AiModelImportModel { ProviderId = created.Id, ModelIds = ["deepseek-chat"] });
        _llm.ModelsToReturn = ["deepseek-chat", "deepseek-reasoner"];
        _llm.ToolProbeReturnsToolCall = true;

        var result = await _service.TestAsync(created.Id);

        Assert.True(result.Ok);
        Assert.Contains("deepseek-chat", result.Message);
        Assert.Equal(AiToolSupport.Supported, result.SupportsTools);
        Assert.Equal(2, result.Models.Count);

        var provider = await _service.GetEntityAsync(created.Id);
        Assert.True(provider.LastTestOk);
        Assert.Equal(AiToolSupport.Supported, provider.SupportsTools);
        Assert.NotNull(provider.LastTestTime);
    }

    [Fact]
    public async Task Test_ToolsRejected_MarksUnsupported()
    {
        var created = await CreateProvider();
        await _service.ImportModelsAsync(new AiModelImportModel { ProviderId = created.Id, ModelIds = ["some-local-model"] });
        _llm.ModelsToReturn = [];
        _llm.ToolProbeError = new LlmException("HTTP 400：tools is not supported by this model", 400);

        var result = await _service.TestAsync(created.Id);

        Assert.True(result.Ok);
        Assert.Equal(AiToolSupport.Unsupported, result.SupportsTools);
    }

    [Fact]
    public async Task Test_NoModelAndNoModelsEndpoint_ReportsFailure()
    {
        var created = await CreateProvider();
        _llm.ModelsError = new LlmException("HTTP 404：not found", 404);

        var result = await _service.TestAsync(created.Id);

        Assert.False(result.Ok);
        Assert.Contains("连接失败", result.Message);
        var provider = await _service.GetEntityAsync(created.Id);
        Assert.False(provider.LastTestOk);
    }

    [Fact]
    public async Task Test_FallsBackToFirstListedModel()
    {
        var created = await CreateProvider();
        _llm.ModelsToReturn = ["m1", "m2"];

        var result = await _service.TestAsync(created.Id);

        Assert.True(result.Ok);
        Assert.Contains("m1", result.Message);
        // 两次请求：连通性 ping（不带工具）+ 工具能力探测（带工具）
        Assert.Equal(2, _llm.ChatRequests.Count);
        Assert.Null(_llm.ChatRequests[0].Tools);
        Assert.NotNull(_llm.ChatRequests[1].Tools);
    }

    [Fact]
    public async Task Test_NoModelAvailable_ReportsFailure()
    {
        var created = await CreateProvider();
        _llm.ModelsToReturn = [];

        var result = await _service.TestAsync(created.Id);

        Assert.False(result.Ok);
        Assert.Contains("未配置模型", result.Message);
        Assert.Empty(_llm.ChatRequests);
    }

    // ==================================================================== 模型与默认模型

    [Fact]
    public async Task ImportModels_SkipsDuplicates()
    {
        var created = await CreateProvider();
        var added = await _service.ImportModelsAsync(new AiModelImportModel
        {
            ProviderId = created.Id,
            ModelIds = ["a", "b", "a"]
        });
        Assert.Equal(2, added);

        var again = await _service.ImportModelsAsync(new AiModelImportModel
        {
            ProviderId = created.Id,
            ModelIds = ["b", "c"]
        });
        Assert.Equal(1, again);
        Assert.Equal(3, _db.AiModels.Count());
    }

    [Fact]
    public async Task SaveModel_ClampsAndSetsDefault()
    {
        var created = await CreateProvider();
        var model = await _service.SaveModelAsync(new AiModelModel
        {
            ProviderId = created.Id,
            ModelId = "deepseek-chat",
            ContextWindow = 99999999,
            MaxOutputTokens = 1,
            Temperature = 9,
            IsDefault = true
        });

        Assert.Equal(2000000, model.ContextWindow);
        Assert.Equal(64, model.MaxOutputTokens);
        Assert.Equal(2, model.Temperature);
        Assert.True(model.IsDefault);

        // 再设一个默认：全局只剩一个
        var second = await _service.SaveModelAsync(new AiModelModel
        {
            ProviderId = created.Id,
            ModelId = "deepseek-reasoner",
            IsDefault = true
        });
        var defaults = _db.AiModels.Where(n => n.IsDefault).ToList();
        Assert.Single(defaults);
        Assert.Equal(second.Id, defaults[0].Id);
    }

    [Fact]
    public async Task SaveModel_AcceptsLargeContextAndOutputCeilings()
    {
        // 用户口径：max_tokens 可配到 128000、上下文可配到 1000000（大窗口模型如 GLM/Kimi/MiniMax 长上下文版）
        var created = await CreateProvider();
        var model = await _service.SaveModelAsync(new AiModelModel
        {
            ProviderId = created.Id,
            ModelId = "long-context-model",
            ContextWindow = 1000000,
            MaxOutputTokens = 128000
        });

        Assert.Equal(1000000, model.ContextWindow);
        Assert.Equal(128000, model.MaxOutputTokens);
        // 上下文预算按模型配置推算（已扣除输出预算）
        Assert.True(AgentService.EstimateBudget(model) > 700000);
    }

    [Fact]
    public async Task DeleteModels_ReassignsDefault()
    {
        var created = await CreateProvider();
        var first = await _service.SaveModelAsync(new AiModelModel { ProviderId = created.Id, ModelId = "m1", IsDefault = true });
        var second = await _service.SaveModelAsync(new AiModelModel { ProviderId = created.Id, ModelId = "m2" });

        await _service.DeleteModelsAsync([first.Id]);

        var (_, model) = await _service.GetDefaultModelAsync();
        Assert.Equal(second.Id, model.Id);
    }

    [Fact]
    public async Task GetDefaultModel_SkipsDisabledProvider()
    {
        var provider = await CreateProvider();
        await _service.SaveModelAsync(new AiModelModel { ProviderId = provider.Id, ModelId = "m1", IsDefault = true });

        await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Id = provider.Id,
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com",
            Enable = false
        });

        var (foundProvider, foundModel) = await _service.GetDefaultModelAsync();
        Assert.Null(foundProvider);
        Assert.Null(foundModel);
    }

    // ==================================================================== 全局设置

    [Fact]
    public async Task Setting_DefaultsAndClamp()
    {
        var setting = await _service.GetSettingAsync();
        Assert.True(setting.Enable);
        Assert.Equal(8, setting.MaxRounds);
        Assert.Equal(AiTestRunNotifyMode.Task, setting.TestRunNotifyMode);

        var saved = await _service.SaveSettingAsync(new AiSettingModel
        {
            Enable = true,
            MaxRounds = 999,
            RunTimeoutSeconds = 5,
            LogTailLines = 999999,
            TestRunNotifyMode = "乱填的值",
            KeepVersionsPerFile = 0,
            SystemPromptExtra = "  本平台脚本注释用中文  "
        });

        Assert.Equal(100, saved.MaxRounds);
        Assert.Equal(60, saved.RunTimeoutSeconds);
        Assert.Equal(2000, saved.LogTailLines);
        Assert.Equal(AiTestRunNotifyMode.Task, saved.TestRunNotifyMode);
        Assert.Equal(1, saved.KeepVersionsPerFile);
        Assert.Equal("本平台脚本注释用中文", saved.SystemPromptExtra);
    }

    // ==================================================================== LLM 报文

    [Fact]
    public void BuildPayload_CarriesToolsAndToolCallHistory()
    {
        var payload = LlmClient.BuildPayload(new LlmChatRequest
        {
            Provider = new AiProviderModel { Name = "p", BaseUrl = "https://x/v1" },
            Model = "m",
            Temperature = 0.3,
            MaxOutputTokens = 1024,
            Messages =
            [
                LlmMessage.System("sys"),
                LlmMessage.User("read the script"),
                new LlmMessage
                {
                    Role = "assistant",
                    Content = null,
                    ToolCalls = [new LlmToolCall { Id = "call_1", Name = "read_script", Arguments = """{"file":"a.cs"}""" }]
                },
                LlmMessage.Tool("call_1", "文件内容")
            ],
            Tools = [new LlmTool { Name = "read_script", Description = "读脚本", ParametersJson = """{"type":"object"}""" }]
        });

        Assert.Equal("m", payload["model"].Value<string>());
        Assert.False(payload["stream"].Value<bool>());
        Assert.Equal(4, ((JArray)payload["messages"]).Count);
        Assert.Equal("tool", payload["messages"][3]["role"].Value<string>());
        Assert.Equal("call_1", payload["messages"][3]["tool_call_id"].Value<string>());
        Assert.Equal("read_script", payload["messages"][2]["tool_calls"][0]["function"]["name"].Value<string>());
        Assert.Equal("auto", payload["tool_choice"].Value<string>());
        Assert.Single((JArray)payload["tools"]);
    }

    [Fact]
    public void BuildPayload_NoTools_OmitsToolFields()
    {
        var payload = LlmClient.BuildPayload(new LlmChatRequest
        {
            Provider = new AiProviderModel { Name = "p" },
            Model = "m",
            Messages = [LlmMessage.User("hi")]
        });

        Assert.Null(payload["tools"]);
        Assert.Null(payload["tool_choice"]);
    }

    [Fact]
    public void ParseResponse_ReadsContentToolsAndUsage()
    {
        var response = LlmClient.ParseResponse("""
            {"choices":[{"message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"c1","type":"function","function":{"name":"read_script","arguments":"{\"file\":\"a.cs\"}"}}]}}],
             "usage":{"prompt_tokens":123,"completion_tokens":45}}
            """, 88);

        Assert.Null(response.Content);
        Assert.True(response.HasToolCalls);
        Assert.Equal("read_script", response.ToolCalls[0].Name);
        Assert.Equal("""{"file":"a.cs"}""", response.ToolCalls[0].Arguments);
        Assert.Equal(123, response.PromptTokens);
        Assert.Equal(45, response.CompletionTokens);
        Assert.Equal(88, response.LatencyMs);
    }

    [Fact]
    public void ParseResponse_PlainText_NoToolCalls()
    {
        var response = LlmClient.ParseResponse("""{"choices":[{"message":{"role":"assistant","content":"pong"}}]}""", 12);
        Assert.Equal("pong", response.Content);
        Assert.False(response.HasToolCalls);
    }

    [Fact]
    public void ParseResponse_ReasoningModelTruncated_FlagsFinishReasonAndReasoning()
    {
        // 实测报文形态（推理型模型：思考内容吃满输出预算、content 为空、finish_reason=length）
        var response = LlmClient.ParseResponse("""{"choices":[{"index":0,"message":{"role":"assistant","content":"","reasoning_content":"The user sent ..."},"finish_reason":"length"}]}""", 30);
        Assert.Equal(string.Empty, response.Content);
        Assert.True(response.HasReasoning);
        Assert.Equal("length", response.FinishReason);
    }

    [Fact]
    public async Task Test_ProbeTruncatedByReasoningModel_StillReportsConnected()
    {
        var created = await CreateProvider();
        await _service.ImportModelsAsync(new AiModelImportModel { ProviderId = created.Id, ModelIds = ["reasoning-model"] });
        _llm.ModelsToReturn = ["reasoning-model"];
        // 探测对话被输出上限截断（推理模型吃满预算）→ 应判「已连通」而不是失败
        _llm.ChatError = new LlmException("供应商在 256 输出上限内没产出正文（该模型先输出思考内容，思考吃满了预算）", truncated: true);

        var result = await _service.TestAsync(created.Id);

        Assert.True(result.Ok);
        Assert.Contains("输出上限", result.Message);
    }

    // ==================================================================== 权限

    [Fact]
    public void AiProviderController_IsManagerOnly()
    {
        var type = typeof(AiProviderController);
        Assert.NotNull(type.GetCustomAttributes(typeof(ManagerOnlyAttribute), inherit: true).SingleOrDefault());
        Assert.NotNull(type.GetCustomAttributes(typeof(CustomAuthorizationFilter), inherit: true).SingleOrDefault());
    }

    // ==================================================================== 测试工具

    private async Task<AiProviderItem> CreateProvider()
    {
        return await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "sk-abcdefghijklmnopqrst"
        });
    }

    /// <summary>假 LLM 客户端：按预置结果回放，并记录收到的请求，避免单测触碰真实网络。</summary>
    private sealed class FakeLlmClient : ILlmClient
    {
        public List<string> ModelsToReturn { get; set; } = [];

        public Exception ModelsError { get; set; }

        public bool ToolProbeReturnsToolCall { get; set; }

        public Exception ToolProbeError { get; set; }

        public List<LlmChatRequest> ChatRequests { get; } = [];

        public Exception ChatError { get; set; }

        public Task<List<string>> ListModelsAsync(AiProviderModel provider, CancellationToken ct = default)
        {
            if (ModelsError != null)
            {
                throw ModelsError;
            }
            return Task.FromResult(ModelsToReturn);
        }

        public Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
        {
            ChatRequests.Add(request);
            if (ChatError != null && request.Tools is not { Count: > 0 })
            {
                throw ChatError;
            }
            if (request.Tools is { Count: > 0 })
            {
                if (ToolProbeError != null)
                {
                    throw ToolProbeError;
                }
                return Task.FromResult(new LlmChatResponse
                {
                    Content = ToolProbeReturnsToolCall ? null : "我不需要工具",
                    ToolCalls = ToolProbeReturnsToolCall
                        ? [new LlmToolCall { Id = "call_1", Name = "get_time", Arguments = """{"zone":"Asia/Shanghai"}""" }]
                        : []
                });
            }
            return Task.FromResult(new LlmChatResponse { Content = "pong", LatencyMs = 5 });
        }
    }
}
