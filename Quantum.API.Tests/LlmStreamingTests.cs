using Newtonsoft.Json.Linq;
using Quantum.Application;
using Quantum.Entities.Model;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// OpenAI 方言 SSE 聚合（2026-09-21 生产 504 事故修复：中转 nginx 60s 读超时掐非流式长输出轮，改流式）：
/// 内容增量拼接、工具调用按 index 归并参数片段、末块 finish_reason/usage、纯 usage 尾块、
/// 载荷 stream 标志（openai 可流式、其它方言恒 false）。
/// </summary>
public class LlmStreamingTests
{
    private static string Chunk(string json) => json;

    [Fact]
    public void ContentDeltas_ConcatenatedInOrder()
    {
        var acc = new SseAccumulator();
        acc.Apply("""{"choices":[{"delta":{"content":"修复"}}]}""");
        acc.Apply("""{"choices":[{"delta":{"content":"方案："}}]}""");
        acc.Apply("""{"choices":[{"delta":{"content":"加防御","reasoning_content":"思考中"}}]}""");
        acc.Apply("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""");
        acc.Apply("""{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20}}""");

        var response = acc.ToResponse(88);
        Assert.Equal("修复方案：加防御", response.Content);
        Assert.Equal("stop", response.FinishReason);
        Assert.True(response.HasReasoning);
        Assert.Equal(100, response.PromptTokens);
        Assert.Equal(20, response.CompletionTokens);
        Assert.False(response.HasToolCalls);
    }

    [Fact]
    public void ToolCallFragments_MergedByIndex()
    {
        var acc = new SseAccumulator();
        // 真实网关块形态：首个块带 id+name+空参，后续块只有 index+参数片段；两个工具交错下发
        acc.Apply("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"read_script","arguments":""}}]}}]}""");
        acc.Apply("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"fil"}}]}}]}""");
        acc.Apply("""{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","function":{"name":"list_envs","arguments":"{}"}}]}}]}""");
        acc.Apply("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"e\":\"a.cs\"}"}}]}}]}""");
        acc.Apply("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        var response = acc.ToResponse(1);
        Assert.Null(response.Content);
        Assert.Equal(2, response.ToolCalls.Count);
        Assert.Equal("call_a", response.ToolCalls[0].Id);
        Assert.Equal("read_script", response.ToolCalls[0].Name);
        Assert.Equal("""{"file":"a.cs"}""", response.ToolCalls[0].Arguments);
        Assert.Equal("call_b", response.ToolCalls[1].Id);
        Assert.Equal("list_envs", response.ToolCalls[1].Name);
        Assert.Equal("tool_calls", response.FinishReason);
    }

    [Fact]
    public void TruncatedStream_FinishLengthCarriesThrough()
    {
        var acc = new SseAccumulator();
        acc.Apply("""{"choices":[{"delta":{"content":"半"}}]}""");
        acc.Apply("""{"choices":[{"delta":{},"finish_reason":"length"}]}""");

        var response = acc.ToResponse(5);
        Assert.Equal("半", response.Content);
        Assert.True(LlmClient.IsTruncatedFinish(response.FinishReason));
    }

    [Fact]
    public void EmptyReasoningOnlyStream_IsNotContent()
    {
        var acc = new SseAccumulator();
        acc.Apply("""{"choices":[{"delta":{"reasoning_content":"先想想"},"content":null}]}""");
        acc.Apply("""{"choices":[{"delta":{},"finish_reason":"length"}]}""");

        var response = acc.ToResponse(5);
        Assert.Null(response.Content);
        Assert.True(response.HasReasoning);
    }

    [Fact]
    public void RawJson_AllDataChunksAccumulated()
    {
        var acc = new SseAccumulator();
        acc.Apply("""{"choices":[{"delta":{"content":"修复"}}]}""");
        acc.Apply("""{"choices":[{"delta":{"content":"方案"}}]}""");
        acc.Apply("""{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20}}""");

        var response = acc.ToResponse(9);
        // SaveFullPrompt 落盘口径：RawJson 必须含全部 data 块——正文都在前面的增量块里，
        // 只存末块（通常只剩 usage 尾块）的话落盘的响应侧几乎空白
        Assert.Equal(3, response.RawJson.Split("choices").Length - 1);
        Assert.Contains("\"修复\"", response.RawJson);
        Assert.Contains("\"方案\"", response.RawJson);
        Assert.Contains("prompt_tokens", response.RawJson);
    }

    [Fact]
    public void BuildPayload_StreamFlag_OnlyOpenAiHonorsIt()
    {
        var request = new LlmChatRequest
        {
            Provider = new AiProviderModel { Name = "p" },
            Model = "m",
            Messages = [LlmMessage.User("hi")]
        };
        Assert.False(LlmClient.BuildPayload(request)["stream"].Value<bool>());
        Assert.True(LlmClient.BuildPayload(request, AiProtocol.OpenAi, stream: true)["stream"].Value<bool>());
        // anthropic/responses 本期不支持流式：即使误传 stream 也恒 false
        Assert.False(LlmClient.BuildPayload(request, AiProtocol.Anthropic, stream: true)["stream"].Value<bool>());
        Assert.False(LlmClient.BuildPayload(request, AiProtocol.Responses, stream: true)["stream"].Value<bool>());
    }
}
