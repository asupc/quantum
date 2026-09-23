using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 协议方言（2026-09-20 用户要求「支持协议可选」）：
/// openai=chat/completions（默认）/ anthropic=Messages API / responses=Responses API。
/// 断言各方言的载荷结构（system 位置、schema 字段名、工具结果回喂形态）与响应解析
/// （文本/工具调用/usage/截断口径归一化），以及供应商协议字段的归一化。
/// </summary>
[Collection("ConstsState")]
public class AiProtocolTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly FakeLlmClient _llm = new();
    private readonly AiProviderService _service;

    public AiProtocolTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new AiProviderService(_db, _llm);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private const string ToolSchema = """{"type":"object","properties":{"file":{"type":"string"}}}""";

    private static AiProviderModel Provider(string protocol) => new()
    {
        Id = "p1",
        Name = "p",
        BaseUrl = "https://api.example.com/v1",
        ApiKey = "sk-x",
        Protocol = protocol
    };

    // ==================================================================== 协议字段

    [Fact]
    public async Task SaveProvider_ProtocolDefaultsToOpenAi_AndRejectsUnknown()
    {
        var created = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Name = "默认协议",
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "sk-abcdefghijklmnopqrst"
        });
        Assert.Equal(AiProtocol.OpenAi, created.Protocol);

        var anthropic = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Id = created.Id,
            Name = "切 Anthropic",
            BaseUrl = "https://api.anthropic.com",
            Protocol = AiProtocol.Anthropic
        });
        Assert.Equal(AiProtocol.Anthropic, anthropic.Protocol);

        var responses = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Id = created.Id,
            Name = "切 Responses",
            BaseUrl = "https://api.openai.com",
            Protocol = AiProtocol.Responses
        });
        Assert.Equal(AiProtocol.Responses, responses.Protocol);

        var bogus = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Id = created.Id,
            Name = "乱填",
            BaseUrl = "https://api.openai.com",
            Protocol = "不存在的方言"
        });
        Assert.Equal(AiProtocol.OpenAi, bogus.Protocol);
    }

    // ==================================================================== Anthropic 方言

    [Fact]
    public void AnthropicPayload_SystemTopLevel_InputSchema_AndGroupedToolResults()
    {
        var payload = LlmClient.BuildPayload(new LlmChatRequest
        {
            Provider = Provider(AiProtocol.Anthropic),
            Model = "claude-sonnet-4-x",
            MaxOutputTokens = 8192,
            Temperature = 0.2,
            Messages =
            [
                LlmMessage.System("sys"),
                LlmMessage.User("读脚本"),
                new LlmMessage
                {
                    Role = "assistant",
                    Content = "好",
                    ToolCalls = [new LlmToolCall { Id = "toolu_1", Name = "read_script", Arguments = """{"file":"a.cs"}""" }]
                },
                LlmMessage.Tool("toolu_1", "内容 A"),
                LlmMessage.Tool("toolu_2", "内容 B")
            ],
            Tools = [new LlmTool { Name = "read_script", Description = "读脚本", ParametersJson = ToolSchema }]
        }, AiProtocol.Anthropic);

        // system 提到顶层、不进 messages
        Assert.Equal("sys", payload["system"].Value<string>());
        Assert.DoesNotContain("\"role\":\"system\"", payload["messages"].ToString());
        Assert.Equal(8192, payload["max_tokens"].Value<int>());

        var messages = (JArray)payload["messages"];
        Assert.Equal(3, messages.Count);
        // 助手工具调用 = tool_use 块，参数是对象（不是字符串）
        Assert.Equal("tool_use", messages[1]["content"][1]["type"].Value<string>());
        Assert.Equal("a.cs", messages[1]["content"][1]["input"]["file"].Value<string>());
        // 两个工具结果合并进同一条 user 消息
        Assert.Equal("user", messages[2]["role"].Value<string>());
        var results = (JArray)messages[2]["content"];
        Assert.Equal(2, results.Count);
        Assert.All(results, n => Assert.Equal("tool_result", n["type"].Value<string>()));
        Assert.Equal("toolu_1", results[0]["tool_use_id"].Value<string>());
        // schema 字段名是 input_schema，且工具声明不带 function 包装
        Assert.NotNull(payload["tools"][0]["input_schema"]);
        Assert.Null(payload["tools"][0]["function"]);
        Assert.Null(payload["tools"][0]["type"]);
    }

    [Fact]
    public void AnthropicResponse_TextToolUseUsageAndTruncationNormalized()
    {
        var response = LlmClient.ParseResponse("""
            {"id":"msg_1","type":"message","role":"assistant","stop_reason":"tool_use",
             "content":[{"type":"thinking","thinking":"..."},
                        {"type":"text","text":"先读脚本。"},
                        {"type":"tool_use","id":"toolu_9","name":"read_script","input":{"file":"a.cs"}}],
             "usage":{"input_tokens":101,"output_tokens":22}}
            """, 44, AiProtocol.Anthropic);

        Assert.Equal("先读脚本。", response.Content);
        Assert.Equal("read_script", response.ToolCalls[0].Name);
        Assert.Equal("toolu_9", response.ToolCalls[0].Id);
        Assert.Contains("a.cs", response.ToolCalls[0].Arguments);
        Assert.Equal(101, response.PromptTokens);
        Assert.Equal(22, response.CompletionTokens);
        Assert.True(response.HasReasoning);
        Assert.Equal("tool_use", response.FinishReason);

        var truncated = LlmClient.ParseResponse("""
            {"type":"message","stop_reason":"max_tokens","content":[{"type":"text","text":"半"}],"usage":{"input_tokens":1,"output_tokens":2}}
            """, 5, AiProtocol.Anthropic);
        Assert.Equal("length", truncated.FinishReason);
        Assert.True(LlmClient.IsTruncatedFinish(truncated.FinishReason));
    }

    // ==================================================================== Responses 方言

    [Fact]
    public void ResponsesPayload_InstructionsAndItemShapes()
    {
        var payload = LlmClient.BuildPayload(new LlmChatRequest
        {
            Provider = Provider(AiProtocol.Responses),
            Model = "gpt-5-x",
            MaxOutputTokens = 4096,
            Messages =
            [
                LlmMessage.System("sys"),
                LlmMessage.User("读脚本"),
                new LlmMessage { Role = "assistant", Content = null, ToolCalls = [new LlmToolCall { Id = "call_1", Name = "read_script", Arguments = "{}" }] },
                LlmMessage.Tool("call_1", "内容 A")
            ],
            Tools = [new LlmTool { Name = "read_script", Description = "读脚本", ParametersJson = ToolSchema }]
        }, AiProtocol.Responses);

        Assert.Equal("sys", payload["instructions"].Value<string>());
        Assert.Equal(4096, payload["max_output_tokens"].Value<int>());
        Assert.Null(payload["messages"]);

        var input = (JArray)payload["input"];
        Assert.Equal("user", input[0]["role"].Value<string>());
        Assert.Equal("function_call", input[1]["type"].Value<string>());
        Assert.Equal("call_1", input[1]["call_id"].Value<string>());
        Assert.Equal("function_call_output", input[2]["type"].Value<string>());
        Assert.Equal("call_1", input[2]["call_id"].Value<string>());
        // 工具声明是扁平结构
        Assert.Equal("function", payload["tools"][0]["type"].Value<string>());
        Assert.Equal("read_script", payload["tools"][0]["name"].Value<string>());
        Assert.Null(payload["tools"][0]["function"]);
    }

    [Fact]
    public void ResponsesResponse_OutputItemsAndIncompleteNormalized()
    {
        var response = LlmClient.ParseResponse("""
            {"id":"resp_1","status":"completed",
             "output":[{"type":"reasoning","summary":[]},
                       {"type":"message","content":[{"type":"output_text","text":"结论"}]},
                       {"type":"function_call","call_id":"call_x","name":"list_envs","arguments":"{}"}],
             "usage":{"input_tokens":7,"output_tokens":8}}
            """, 15, AiProtocol.Responses);

        Assert.Equal("结论", response.Content);
        Assert.Equal("list_envs", response.ToolCalls[0].Name);
        Assert.Equal("call_x", response.ToolCalls[0].Id);
        Assert.Equal(7, response.PromptTokens);
        Assert.Equal(8, response.CompletionTokens);
        Assert.True(response.HasReasoning);
        Assert.False(LlmClient.IsTruncatedFinish(response.FinishReason));

        var incomplete = LlmClient.ParseResponse("""
            {"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[],"usage":{"input_tokens":1,"output_tokens":2}}
            """, 5, AiProtocol.Responses);
        Assert.Equal("length", incomplete.FinishReason);
        Assert.True(LlmClient.IsTruncatedFinish(incomplete.FinishReason));
    }

    // ==================================================================== 端到端（假客户端）

    [Fact]
    public async Task Test_AnthropicProvider_ProbesThroughDialectAndReportsToolSupport()
    {
        var created = await _service.SaveProviderAsync(new AiProviderSaveModel
        {
            Name = "Claude",
            BaseUrl = "https://api.anthropic.com",
            Protocol = AiProtocol.Anthropic,
            ApiKey = "sk-ant-abcdefghijklmnop"
        });
        await _service.ImportModelsAsync(new AiModelImportModel { ProviderId = created.Id, ModelIds = ["claude-sonnet-4-x"] });
        _llm.ModelsToReturn = ["claude-sonnet-4-x"];
        _llm.ToolProbeReturnsToolCall = true;

        var result = await _service.TestAsync(created.Id);

        Assert.True(result.Ok);
        Assert.Equal(AiToolSupport.Supported, result.SupportsTools);
        // 探测走的仍是同一套 ChatAsync（方言细节在客户端内部），测试即验证「换协议不改上层」
        Assert.Equal(2, _llm.ChatRequests.Count);
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public List<string> ModelsToReturn { get; set; } = [];

        public bool ToolProbeReturnsToolCall { get; set; }

        public List<LlmChatRequest> ChatRequests { get; } = [];

        public Task<List<string>> ListModelsAsync(AiProviderModel provider, CancellationToken ct = default)
            => Task.FromResult(ModelsToReturn);

        public Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
        {
            ChatRequests.Add(request);
            if (request.Tools is { Count: > 0 })
            {
                return Task.FromResult(new LlmChatResponse
                {
                    Content = ToolProbeReturnsToolCall ? null : "不需要工具",
                    ToolCalls = ToolProbeReturnsToolCall
                        ? [new LlmToolCall { Id = "toolu_1", Name = "get_time", Arguments = """{"zone":"Asia/Shanghai"}""" }]
                        : []
                });
            }
            return Task.FromResult(new LlmChatResponse { Content = "pong" });
        }
    }
}
