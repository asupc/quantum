using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>LLM 会话消息（方言无关的归一化形态）</summary>
public sealed class LlmMessage
{
    /// <summary>system / user / assistant / tool</summary>
    public string Role { get; set; }

    public string Content { get; set; }

    /// <summary>role=tool 时对应的工具调用 Id</summary>
    public string ToolCallId { get; set; }

    /// <summary>role=assistant 需要把上一轮的工具调用原样回喂时填写</summary>
    public List<LlmToolCall> ToolCalls { get; set; }

    public static LlmMessage System(string content) => new() { Role = "system", Content = content };
    public static LlmMessage User(string content) => new() { Role = "user", Content = content };
    public static LlmMessage Assistant(string content) => new() { Role = "assistant", Content = content };
    public static LlmMessage Tool(string toolCallId, string content) => new() { Role = "tool", ToolCallId = toolCallId, Content = content };
}

/// <summary>模型请求的工具调用</summary>
public sealed class LlmToolCall
{
    public string Id { get; set; }

    public string Name { get; set; }

    /// <summary>参数 JSON 原文（解析失败由调用方按容错处理）</summary>
    public string Arguments { get; set; }
}

/// <summary>提供给模型的工具声明（parameters 为 JSON Schema 字符串）</summary>
public sealed class LlmTool
{
    public string Name { get; set; }

    public string Description { get; set; }

    public string ParametersJson { get; set; }
}

/// <summary>一次对话请求</summary>
public sealed class LlmChatRequest
{
    public AiProviderModel Provider { get; set; }

    public string Model { get; set; }

    public double Temperature { get; set; } = 0.2;

    public int MaxOutputTokens { get; set; } = 4096;

    public List<LlmMessage> Messages { get; set; } = [];

    /// <summary>为空表示不带工具（纯对话）；null 与空列表等价</summary>
    public List<LlmTool> Tools { get; set; }
}

/// <summary>一次对话响应</summary>
public sealed class LlmChatResponse
{
    public string Content { get; set; }

    public List<LlmToolCall> ToolCalls { get; set; } = [];

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public long LatencyMs { get; set; }

    /// <summary>结束原因（统一口径：length=被输出上限截断；其余为方言原值）</summary>
    public string FinishReason { get; set; }

    /// <summary>推理型模型（DeepSeek-R1/GLM 等）返回的思考内容是否存在（不参与业务，只用于诊断）</summary>
    public bool HasReasoning { get; set; }

    /// <summary>原始响应 JSON（SaveFullPrompt 打开时落盘排查）</summary>
    public string RawJson { get; set; }

    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>
/// LLM 调用失败（携带排查所需的状态码与响应片段）。
/// ContextOverflow 用于「上下文超长」的定向兜底（缩预算重试一次）；
/// Truncated 用于「输出上限太小把内容截断/推理模型把预算花在思考上」——服务本身是通的。
/// </summary>
public class LlmException : Exception
{
    public int StatusCode { get; init; }

    public bool ContextOverflow { get; init; }

    public bool Truncated { get; init; }

    public LlmException(string message, int statusCode = 0, bool contextOverflow = false, bool truncated = false) : base(message)
    {
        StatusCode = statusCode;
        ContextOverflow = contextOverflow;
        Truncated = truncated;
    }
}

/// <summary>LLM 客户端（便于测试注入假实现）</summary>
public interface ILlmClient
{
    Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken ct = default);

    /// <summary>拉取供应商模型清单（{BaseUrl}/models）</summary>
    Task<List<string>> ListModelsAsync(AiProviderModel provider, CancellationToken ct = default);
}

/// <summary>
/// 多方言 LLM 客户端（2026-09-20 新增，同日追加协议可选）：
/// 供应商可分别按 <see cref="AiProtocol"/> 选用三种方言——
/// ①openai：OpenAI 兼容 chat/completions（DeepSeek/通义/智谱/Kimi/OpenRouter/Ollama/大多数中转，默认）；
/// ②anthropic：Messages API（/v1/messages，x-api-key + anthropic-version，content 块 + tool_use/tool_result）；
/// ③responses：OpenAI Responses API（/v1/responses，input/output 项式，function_call/function_call_output）。
/// 方言差异全部收敛在本类（URL/鉴权头/载荷组装/响应解析/usage 字段名），
/// 上层 Agent 循环、工具集、提案链路看到的始终是归一化的 LlmMessage/LlmTool/LlmToolCall。
/// HttpClient 按「供应商 + 超时 + 代理开关」缓存复用（沿用 ctx.Http 的 §1-13 取向），执行侧不得 Dispose。
/// </summary>
public class LlmClient : ILlmClient
{
    private static readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(LlmClient));

    private static readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    /// <summary>错误响应体截断长度（进错误文案/日志，避免整段 HTML 刷屏）</summary>
    private const int ErrorBodyLimit = 400;

    /// <summary>Anthropic 要求的版本头（值稳定，跟随官方 2023-06-01 基线）</summary>
    private const string AnthropicVersion = "2023-06-01";

    public async Task<LlmChatResponse> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
    {
        var provider = request?.Provider ?? throw new BusinessException("未指定 AI 供应商！");
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new BusinessException($"供应商「{provider.Name}」未配置 API 密钥！");
        }
        var protocol = AiProtocol.Normalize(provider.Protocol);
        // OpenAI 方言走 SSE 流式（2026-09-21 生产 504 事故：中转前置 nginx 读超时 60s，会掐掉
        // 总时长超 60s 的非流式长输出轮——如 propose_fix 要吐整份脚本源码；实测同一长生成
        // 非流式 60.1s 必 504，流式首块 2.3s 到达、85s 总时长正常完成，因为网关超时只计空闲。
        // anthropic/responses 方言本期维持非流式（未启用，启用前需按各方言 SSE 事件格式补实现）。
        if (protocol == AiProtocol.OpenAi)
        {
            return await ChatStreamingAsync(provider, request, ct);
        }
        var body = JsonConvert.SerializeObject(BuildPayload(request, protocol));
        var (status, responseText, latency) = await SendAsync(provider, protocol, protocol == AiProtocol.Anthropic ? "messages" : "responses", body, ct);
        if (status != 200)
        {
            throw new LlmException($"供应商「{provider.Name}」调用失败（HTTP {status}）：{Truncate(responseText)}",
                status, IsContextOverflow(status, responseText));
        }
        return FinalizeResponse(provider, request, ParseResponse(responseText, latency, protocol));
    }

    /// <summary>空回复/截断判定的共享收口（流式与非流式同口径）。</summary>
    private static LlmChatResponse FinalizeResponse(AiProviderModel provider, LlmChatRequest request, LlmChatResponse response)
    {
        if (string.IsNullOrEmpty(response.Content) && response.ToolCalls.Count == 0)
        {
            // 两种「空回复」要分开说：被输出上限截断（推理模型把预算花在思考上是常见原因）说明服务是通的，
            // 只是参数/模型类型不匹配；其余才是真的异常响应。
            if (IsTruncatedFinish(response.FinishReason))
            {
                throw new LlmException(
                    $"供应商「{provider.Name}」在 {request.MaxOutputTokens} 输出上限内没产出正文"
                    + (response.HasReasoning ? "（该模型先输出思考内容，思考吃满了预算）" : string.Empty)
                    + "：请到「供应商与模型」把该模型的「输出上限」调大后重试。",
                    truncated: true);
            }
            throw new LlmException($"供应商「{provider.Name}」返回空回复：{Truncate(response.RawJson)}");
        }
        return response;
    }

    /// <summary>
    /// OpenAI 方言的流式实现：SSE 逐块读取并聚合（内容增量拼接、工具调用按 index 归并参数片段），
    /// 传输层语义与非流式完全一致（上层无感知）。读流期间连接中断按普通请求失败走重试。
    /// 超时语义 = 空闲超时（2026-09-21 生产事故三：HttpClient.Timeout 掐的是含读流在内的总时长，
    /// propose_fix 长输出单轮超 120 秒即被客户端自己掐死——网关只计空闲，客户端也必须只计空闲）：
    /// 响应头等待与相邻两行数据的间隔各以供应商超时秒数为上限，总时长不设 HttpClient 级限制
    /// （run 级 RunTimeoutSeconds 兜底）。连接建立耗时同样受供应商超时约束（SocketsHttpHandler.ConnectTimeout）。
    /// </summary>
    private async Task<LlmChatResponse> ChatStreamingAsync(AiProviderModel provider, LlmChatRequest request, CancellationToken ct)
    {
        var url = $"{AiProviderService.NormalizeBaseUrl(provider.BaseUrl)}/chat/completions";
        var body = JsonConvert.SerializeObject(BuildPayload(request, AiProtocol.OpenAi, stream: true));
        var client = GetClient(provider);
        var retries = Math.Max(0, provider.MaxRetries);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 10, 600));
        Exception lastError = null;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            var watch = Stopwatch.StartNew();
            // 每次尝试独立的空闲计时：CancelAfter 可重复调用重置计时，读流期间每行归零
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {provider.ApiKey}");
                message.Headers.Accept.ParseAdd("text/event-stream");
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                // ResponseHeadersRead：拿到响应头即返回、正文按流读——流式对网关总时长超时免疫的前提
                idleCts.CancelAfter(timeout);
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, idleCts.Token);
                var status = (int)response.StatusCode;
                if (status != 200)
                {
                    var text = await response.Content.ReadAsStringAsync(ct);
                    if (status is >= 500 or 429 && attempt < retries)
                    {
                        _log.Warn($"AI 供应商「{provider.Name}」返回 HTTP {status}，第 {attempt + 1} 次重试");
                        await Task.Delay(400 * (attempt + 1), ct);
                        continue;
                    }
                    throw new LlmException($"供应商「{provider.Name}」调用失败（HTTP {status}）：{Truncate(text)}",
                        status, IsContextOverflow(status, text));
                }

                var accumulator = new SseAccumulator();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                // 不用 reader.EndOfStream 做循环条件：它内部是同步 Peek，会阻塞线程池线程等网络数据
                while (true)
                {
                    string line;
                    try
                    {
                        // 只要读到一行数据计时就归零：长生成总时长可远超超时秒数而不误杀
                        idleCts.CancelAfter(timeout);
                        line = await reader.ReadLineAsync(idleCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"流式响应已 {timeout.TotalSeconds:0} 秒无新数据（可在供应商设置调大「超时」）");
                    }
                    if (line is null)
                    {
                        break;
                    }
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue; // 空行/冒号注释/事件名行一律跳过（OpenAI 兼容只用单行 data）
                    }
                    var data = line["data:".Length..].Trim();
                    if (data.Length == 0 || data == "[DONE]")
                    {
                        if (data == "[DONE]")
                        {
                            break;
                        }
                        continue;
                    }
                    try
                    {
                        accumulator.Apply(data);
                    }
                    catch (Exception e)
                    {
                        // 单块坏数据只丢该块，不断流（部分网关会夹非标准块）
                        _log.Warn($"SSE 块解析失败（{provider.Name}）：{Truncate(data)}；{e.Message}");
                    }
                }
                watch.Stop();
                return FinalizeResponse(provider, request, accumulator.ToResponse(watch.ElapsedMilliseconds));
            }
            catch (LlmException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // 响应头阶段空闲超时：不盲重试（再等一整个超时窗口大概率还是停滞），与实体注释「超时不重试」对齐
                throw new LlmException($"供应商「{provider.Name}」请求失败：响应头已 {timeout.TotalSeconds:0} 秒未到达（可在供应商设置调大「超时」）");
            }
            catch (TimeoutException e)
            {
                // 读流阶段空闲超时（循环内已转译）——同样不重试
                throw new LlmException($"供应商「{provider.Name}」请求失败：{e.Message}");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
            {
                lastError = e;
                if (attempt < retries)
                {
                    _log.Warn($"AI 供应商「{provider.Name}」流式请求异常（{e.Message}），第 {attempt + 1} 次重试");
                    await Task.Delay(400 * (attempt + 1), ct);
                    continue;
                }
            }
        }
        throw new LlmException($"供应商「{provider.Name}」请求失败：{lastError?.Message ?? "未知网络错误"}");
    }

    public async Task<List<string>> ListModelsAsync(AiProviderModel provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new BusinessException($"供应商「{provider.Name}」未配置 API 密钥！");
        }
        var protocol = AiProtocol.Normalize(provider.Protocol);
        var (status, responseText, _) = await SendAsync(provider, protocol, "models", null, ct, HttpMethod.Get);
        if (status != 200)
        {
            throw new LlmException($"供应商「{provider.Name}」模型清单拉取失败（HTTP {status}）：{Truncate(responseText)}", status);
        }
        var json = JsonConvert.DeserializeObject<JObject>(responseText);
        return (json?["data"] as JArray ?? [])
            .Select(n => n?["id"]?.Value<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ==================================================================== 载荷组装（按方言分支）

    /// <summary>
    /// 请求体组装（null 值一律不带，避免个别网关把 null 判为非法）。
    /// protocol 默认 openai；stream 仅对方言自身支持流式的载荷生效（openai），其余方言恒 false。
    /// </summary>
    internal static JObject BuildPayload(LlmChatRequest request, string protocol = AiProtocol.OpenAi, bool stream = false)
    {
        return AiProtocol.Normalize(protocol) switch
        {
            AiProtocol.Anthropic => BuildAnthropicPayload(request),
            AiProtocol.Responses => BuildResponsesPayload(request),
            _ => BuildOpenAiPayload(request, stream)
        };
    }

    private static JObject BuildOpenAiPayload(LlmChatRequest request, bool stream)
    {
        var messages = new JArray();
        foreach (var message in request.Messages)
        {
            var item = new JObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content ?? string.Empty
            };
            if (!string.IsNullOrEmpty(message.ToolCallId))
            {
                item["tool_call_id"] = message.ToolCallId;
            }
            if (message.ToolCalls is { Count: > 0 })
            {
                item["tool_calls"] = new JArray(message.ToolCalls.Select(call => new JObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments
                    }
                }));
            }
            messages.Add(item);
        }
        var payload = new JObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = stream
        };
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = new JArray(request.Tools.Select(tool => new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ParseSchema(tool.ParametersJson)
                }
            }));
            payload["tool_choice"] = "auto";
        }
        return payload;
    }

    /// <summary>
    /// Anthropic Messages API：system 提到顶层；工具结果必须作为「user 消息里的 tool_result 块」，
    /// 且同一轮多个结果要合并进**同一条** user 消息（Anthropic 不接受分散的多条）。
    /// </summary>
    private static JObject BuildAnthropicPayload(LlmChatRequest request)
    {
        var systems = request.Messages.Where(n => n.Role == "system" && !string.IsNullOrWhiteSpace(n.Content))
            .Select(n => n.Content);
        var messages = new JArray();
        var pendingResults = new JArray();
        foreach (var message in request.Messages.Where(n => n.Role != "system"))
        {
            if (message.Role == "tool")
            {
                pendingResults.Add(new JObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = message.ToolCallId ?? string.Empty,
                    ["content"] = message.Content ?? string.Empty
                });
                continue;
            }
            FlushToolResults(messages, pendingResults);

            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            {
                var blocks = new JArray();
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    blocks.Add(new JObject { ["type"] = "text", ["text"] = message.Content });
                }
                foreach (var call in message.ToolCalls)
                {
                    blocks.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = ParseJsonOrEmptyObject(call.Arguments)
                    });
                }
                messages.Add(new JObject { ["role"] = "assistant", ["content"] = blocks });
            }
            else
            {
                messages.Add(new JObject
                {
                    ["role"] = message.Role == "assistant" ? "assistant" : "user",
                    ["content"] = message.Content ?? string.Empty
                });
            }
        }
        FlushToolResults(messages, pendingResults);

        var payload = new JObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = false,
            ["messages"] = messages
        };
        var systemText = string.Join("\n\n", systems);
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            payload["system"] = systemText;
        }
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = new JArray(request.Tools.Select(tool => new JObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                // Anthropic 的 schema 字段名是 input_schema（不是 parameters）
                ["input_schema"] = ParseSchema(tool.ParametersJson)
            }));
        }
        return payload;
    }

    private static void FlushToolResults(JArray messages, JArray pendingResults)
    {
        if (pendingResults.Count == 0)
        {
            return;
        }
        messages.Add(new JObject { ["role"] = "user", ["content"] = pendingResults.DeepClone() });
        pendingResults.Clear();
    }

    /// <summary>
    /// OpenAI Responses API：system → instructions；工具调用回喂为 function_call 项 +
    /// function_call_output 项（不使用 previous_response_id，保持无状态可重放）。
    /// </summary>
    private static JObject BuildResponsesPayload(LlmChatRequest request)
    {
        var systems = request.Messages.Where(n => n.Role == "system" && !string.IsNullOrWhiteSpace(n.Content))
            .Select(n => n.Content);
        var input = new JArray();
        foreach (var message in request.Messages.Where(n => n.Role != "system"))
        {
            if (message.Role == "tool")
            {
                input.Add(new JObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = message.ToolCallId ?? string.Empty,
                    ["output"] = message.Content ?? string.Empty
                });
                continue;
            }
            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    input.Add(new JObject { ["role"] = "assistant", ["content"] = message.Content });
                }
                foreach (var call in message.ToolCalls)
                {
                    input.Add(new JObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.Id,
                        ["name"] = call.Name,
                        ["arguments"] = string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments
                    });
                }
                continue;
            }
            input.Add(new JObject
            {
                ["role"] = message.Role == "assistant" ? "assistant" : "user",
                ["content"] = message.Content ?? string.Empty
            });
        }

        var payload = new JObject
        {
            ["model"] = request.Model,
            ["input"] = input,
            ["max_output_tokens"] = request.MaxOutputTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = false
        };
        var systemText = string.Join("\n\n", systems);
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            payload["instructions"] = systemText;
        }
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = new JArray(request.Tools.Select(tool => new JObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ParseSchema(tool.ParametersJson)
            }));
            payload["tool_choice"] = "auto";
        }
        return payload;
    }

    private static JToken ParseSchema(string parametersJson)
        => string.IsNullOrWhiteSpace(parametersJson)
            ? new JObject { ["type"] = "object", ["properties"] = new JObject() }
            : JToken.Parse(parametersJson);

    private static JToken ParseJsonOrEmptyObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JObject();
        }
        try
        {
            return JToken.Parse(json);
        }
        catch
        {
            return new JObject();
        }
    }

    // ==================================================================== 响应解析（按方言分支）

    /// <summary>响应解析（拆出来便于单测直接喂真实报文；protocol 默认 openai）。</summary>
    internal static LlmChatResponse ParseResponse(string responseText, long latencyMs, string protocol = AiProtocol.OpenAi)
    {
        return AiProtocol.Normalize(protocol) switch
        {
            AiProtocol.Anthropic => ParseAnthropicResponse(responseText, latencyMs),
            AiProtocol.Responses => ParseResponsesResponse(responseText, latencyMs),
            _ => ParseOpenAiResponse(responseText, latencyMs)
        };
    }

    private static LlmChatResponse ParseOpenAiResponse(string responseText, long latencyMs)
    {
        var json = JsonConvert.DeserializeObject<JObject>(responseText);
        var choice = json?["choices"]?[0];
        var message = choice?["message"];
        return new LlmChatResponse
        {
            Content = message?["content"]?.Type == JTokenType.String ? message["content"].Value<string>() : null,
            ToolCalls = ParseToolCalls(message?["tool_calls"]),
            PromptTokens = json?["usage"]?["prompt_tokens"]?.Value<int?>() ?? 0,
            CompletionTokens = json?["usage"]?["completion_tokens"]?.Value<int?>() ?? 0,
            FinishReason = choice?["finish_reason"]?.Value<string>(),
            HasReasoning = message?["reasoning_content"] != null || message?["reasoning"] != null,
            LatencyMs = latencyMs,
            RawJson = responseText
        };
    }

    private static LlmChatResponse ParseAnthropicResponse(string responseText, long latencyMs)
    {
        var json = JsonConvert.DeserializeObject<JObject>(responseText);
        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();
        var hasThinking = false;
        foreach (var block in json?["content"] as JArray ?? [])
        {
            switch (block?["type"]?.Value<string>())
            {
                case "text":
                    text.Append(block["text"]?.Value<string>());
                    break;
                case "tool_use":
                    calls.Add(new LlmToolCall
                    {
                        Id = block["id"]?.Value<string>() ?? Guid.NewGuid().ToString("N"),
                        Name = block["name"]?.Value<string>(),
                        Arguments = block["input"]?.ToString(Formatting.None)
                    });
                    break;
                case "thinking":
                case "redacted_thinking":
                    hasThinking = true;
                    break;
            }
        }
        var stopReason = json?["stop_reason"]?.Value<string>();
        return new LlmChatResponse
        {
            // 统一口径：stop_reason=max_tokens 等价于 OpenAI 的 finish_reason=length
            Content = text.Length == 0 ? null : text.ToString(),
            ToolCalls = calls.Where(n => !string.IsNullOrWhiteSpace(n.Name)).ToList(),
            PromptTokens = json?["usage"]?["input_tokens"]?.Value<int?>() ?? 0,
            CompletionTokens = json?["usage"]?["output_tokens"]?.Value<int?>() ?? 0,
            FinishReason = string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase) ? "length" : stopReason,
            HasReasoning = hasThinking,
            LatencyMs = latencyMs,
            RawJson = responseText
        };
    }

    private static LlmChatResponse ParseResponsesResponse(string responseText, long latencyMs)
    {
        var json = JsonConvert.DeserializeObject<JObject>(responseText);
        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();
        var hasReasoning = false;
        foreach (var item in json?["output"] as JArray ?? [])
        {
            switch (item?["type"]?.Value<string>())
            {
                case "message":
                    foreach (var block in item["content"] as JArray ?? [])
                    {
                        if (block?["type"]?.Value<string>() == "output_text")
                        {
                            text.Append(block["text"]?.Value<string>());
                        }
                    }
                    break;
                case "function_call":
                    calls.Add(new LlmToolCall
                    {
                        // Responses 用 call_id 回喂 function_call_output；缺失时退回 id
                        Id = item["call_id"]?.Value<string>() ?? item["id"]?.Value<string>(),
                        Name = item["name"]?.Value<string>(),
                        Arguments = item["arguments"]?.Value<string>()
                    });
                    break;
                case "reasoning":
                    hasReasoning = true;
                    break;
            }
        }
        var incomplete = string.Equals(json?["status"]?.Value<string>(), "incomplete", StringComparison.OrdinalIgnoreCase);
        var reason = json?["incomplete_details"]?["reason"]?.Value<string>();
        return new LlmChatResponse
        {
            Content = text.Length == 0 ? null : text.ToString(),
            ToolCalls = calls.Where(n => !string.IsNullOrWhiteSpace(n.Name)).ToList(),
            PromptTokens = json?["usage"]?["input_tokens"]?.Value<int?>() ?? 0,
            CompletionTokens = json?["usage"]?["output_tokens"]?.Value<int?>() ?? 0,
            FinishReason = incomplete && reason != null && reason.Contains("token", StringComparison.OrdinalIgnoreCase)
                ? "length"
                : json?["status"]?.Value<string>(),
            HasReasoning = hasReasoning,
            LatencyMs = latencyMs,
            RawJson = responseText
        };
    }

    private static List<LlmToolCall> ParseToolCalls(JToken token)
    {
        if (token is not JArray array)
        {
            return [];
        }
        var calls = new List<LlmToolCall>();
        foreach (var item in array)
        {
            var name = item?["function"]?["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            calls.Add(new LlmToolCall
            {
                Id = item?["id"]?.Value<string>() ?? Guid.NewGuid().ToString("N"),
                Name = name,
                Arguments = item?["function"]?["arguments"]?.Value<string>()
            });
        }
        return calls;
    }

    /// <summary>统一的「被输出上限截断」判定（各方言口径已归一化）。</summary>
    internal static bool IsTruncatedFinish(string finishReason)
        => string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
           || string.Equals(finishReason, "max_tokens", StringComparison.OrdinalIgnoreCase);

    // ==================================================================== 传输

    /// <summary>
    /// 发送请求（GET 时 body 为 null）：网络异常/429/5xx/超时按 MaxRetries 退避重试；
    /// 4xx（含 400 超长）不重试，交给上层做缩预算兜底。URL 与鉴权头按方言分支。
    /// 超时为每次尝试独立的显式 CTS（HttpClient 实例级超时已设无限，见 <see cref="GetClient"/>）。
    /// </summary>
    private static async Task<(int Status, string Body, long LatencyMs)> SendAsync(
        AiProviderModel provider, string protocol, string relativePath, string body, CancellationToken ct, HttpMethod method = null)
    {
        var url = $"{AiProviderService.NormalizeBaseUrl(provider.BaseUrl)}/{relativePath}";
        var client = GetClient(provider);
        var retries = Math.Max(0, provider.MaxRetries);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 10, 600));
        Exception lastError = null;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            var watch = Stopwatch.StartNew();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(timeout);
            try
            {
                using var message = new HttpRequestMessage(method ?? HttpMethod.Post, url);
                if (AiProtocol.Normalize(protocol) == AiProtocol.Anthropic)
                {
                    // Anthropic：x-api-key + anthropic-version（不收 Authorization: Bearer）
                    message.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey);
                    message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                }
                else
                {
                    message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {provider.ApiKey}");
                }
                if (body != null)
                {
                    message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }
                using var response = await client.SendAsync(message, attemptCts.Token);
                var text = await response.Content.ReadAsStringAsync(attemptCts.Token);
                var status = (int)response.StatusCode;
                if (status is >= 500 or 429 && attempt < retries)
                {
                    _log.Warn($"AI 供应商「{provider.Name}」返回 HTTP {status}，第 {attempt + 1} 次重试");
                    await Task.Delay(400 * (attempt + 1), ct);
                    continue;
                }
                return (status, text, watch.ElapsedMilliseconds);
            }
            catch (LlmException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // 本尝试自身超时（非流式=整个请求总时长）：不盲重试，与实体注释「超时不重试」对齐
                throw new LlmException($"供应商「{provider.Name}」请求失败：请求已 {timeout.TotalSeconds:0} 秒未完成（可在供应商设置调大「超时」）");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                lastError = e;
                if (attempt < retries)
                {
                    _log.Warn($"AI 供应商「{provider.Name}」请求异常（{e.Message}），第 {attempt + 1} 次重试");
                    await Task.Delay(400 * (attempt + 1), ct);
                    continue;
                }
            }
        }
        throw new LlmException($"供应商「{provider.Name}」请求失败：{lastError?.Message ?? "未知网络错误"}");
    }

    /// <summary>
    /// 按「供应商 + 超时 + 代理开关」缓存 HttpClient（执行侧不得 Dispose）。
    /// 实例级 Timeout 恒为无限：HttpClient.Timeout 掐的是含 SSE 读流在内的总时长，会误杀
    /// 总时长超「超时秒数」的合法长生成（2026-09-21 生产事故三）；总时长改由调用方显式控制——
    /// 非流式=每次尝试的 CTS 总超时，流式=空闲超时 + run 级 RunTimeoutSeconds 兜底。
    /// </summary>
    private static HttpClient GetClient(AiProviderModel provider)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 10, 600));
        var key = $"{provider.Id}|{timeout}|{provider.UsePlatformProxy}";
        return _clients.GetOrAdd(key, _ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseProxy = provider.UsePlatformProxy,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // 连接建立（含 TLS 握手）仍受供应商超时约束，防实例级超时放无限后连接挂起无人管
            ConnectTimeout = timeout
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
    }

    /// <summary>上下文超长的启发式判定（各家文案不一：maximum context length / too many tokens 等）。</summary>
    private static bool IsContextOverflow(int status, string body)
    {
        if (status != 400 || string.IsNullOrEmpty(body))
        {
            return false;
        }
        var text = body.ToLowerInvariant();
        return (text.Contains("context") && (text.Contains("length") || text.Contains("window") || text.Contains("token")))
            || text.Contains("too many tokens")
            || text.Contains("reduce the length");
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var trimmed = text.Trim();
        return trimmed.Length <= ErrorBodyLimit ? trimmed : trimmed[..ErrorBodyLimit] + "…";
    }
}

/// <summary>
/// OpenAI 方言 SSE 增量聚合器：把一串 data 块（delta.content 增量、delta.tool_calls 按 index
/// 归并的参数片段、末块 finish_reason/usage）聚合成与非流式解析完全同构的 <see cref="LlmChatResponse"/>。
/// 独立成类便于单测直接喂块序列。
/// </summary>
internal sealed class SseAccumulator
{
    private readonly System.Text.StringBuilder _content = new();
    private readonly List<LlmToolCall> _calls = [];
    private string _finishReason;
    private bool _hasReasoning;
    private int _promptTokens;
    private int _completionTokens;
    /// <summary>全部 data 块原文（含 [DONE] 前）：SaveFullPrompt 落盘的响应侧排查数据——
    /// 正文都在前面的增量块里，只存末块的话（通常只剩 usage 尾块）落盘几乎空白。</summary>
    private readonly System.Text.StringBuilder _rawJson = new();

    /// <summary>应用一个 data 块的 JSON 原文（单块坏数据由调用方决定丢弃与否）。</summary>
    public void Apply(string chunkJson)
    {
        var json = JObject.Parse(chunkJson);
        _rawJson.AppendLine(chunkJson);
        var usage = json["usage"];
        if (usage != null)
        {
            _promptTokens = usage["prompt_tokens"]?.Value<int?>() ?? _promptTokens;
            _completionTokens = usage["completion_tokens"]?.Value<int?>() ?? _completionTokens;
        }
        // 纯 usage 尾块（stream_options.include_usage 或网关附带）的 choices 为空数组：下标访问前先判数
        if (json["choices"] is not JArray { Count: > 0 } choices || choices[0] is not JObject choice)
        {
            return;
        }
        var finish = choice["finish_reason"]?.Value<string>();
        if (!string.IsNullOrEmpty(finish))
        {
            _finishReason = finish;
        }
        if (choice["delta"] is not JObject delta)
        {
            return;
        }
        if (delta["content"]?.Type == JTokenType.String)
        {
            _content.Append(delta["content"].Value<string>());
        }
        if (delta["reasoning_content"] != null || delta["reasoning"] != null)
        {
            _hasReasoning = true;
        }
        foreach (var call in delta["tool_calls"] as JArray ?? [])
        {
            var index = call["index"]?.Value<int?>() ?? _calls.Count;
            while (_calls.Count <= index)
            {
                _calls.Add(new LlmToolCall());
            }
            var target = _calls[index];
            var id = call["id"]?.Value<string>();
            if (!string.IsNullOrEmpty(id))
            {
                target.Id = id;
            }
            if (call["function"] is not JObject function)
            {
                continue;
            }
            var name = function["name"]?.Value<string>();
            if (!string.IsNullOrEmpty(name))
            {
                target.Name = name;
            }
            var arguments = function["arguments"]?.Value<string>();
            if (!string.IsNullOrEmpty(arguments))
            {
                target.Arguments = (target.Arguments ?? string.Empty) + arguments;
            }
        }
    }

    public LlmChatResponse ToResponse(long latencyMs) => new()
    {
        Content = _content.Length == 0 ? null : _content.ToString(),
        ToolCalls = _calls.Where(n => !string.IsNullOrWhiteSpace(n.Name)).ToList(),
        PromptTokens = _promptTokens,
        CompletionTokens = _completionTokens,
        FinishReason = _finishReason,
        HasReasoning = _hasReasoning,
        LatencyMs = latencyMs,
        RawJson = _rawJson.Length == 0 ? string.Empty : _rawJson.ToString()
    };
}
