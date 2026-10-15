using Newtonsoft.Json.Linq;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 对抗性加固回归（2026-10-11）：
/// 1) 外触请求体防护——深嵌套 JsonConvert.SerializeObject 会递归爆栈（StackOverflowException 不可捕获、
///    直接打死进程；已用探针复现「测试主机进程崩溃 : Stack overflow」），宽结构则放大内存；
/// 2) 分页上限与 Skip 溢出——PageSize 无上限时一个查询即可要求整表物化，int 乘法溢出产生负 OFFSET；
/// 3) 摘要脱敏——\w*…(关键字)…\w* 形态在长词串上是 O(n²)，且输入来自未截断的脚本异常消息；
/// 4) SSE tool_calls index 无上限——{"index":2000000000} 一次撑出 20 亿对象。
/// </summary>
public class AdversarialHardeningTests
{
    // ---------------------------------------------------------------- 外触请求体防护

    /// <summary>
    /// 构造「容器嵌套层数 = levels」的载荷：层级口径与 ValidatePayload / JSON 层级一致
    /// （JArray 的子节点即元素本身，每包一层 +1；标量叶子不计层）。
    /// 边界用例必须用这个口径，否则测的是「levels-1」而不是 levels。
    /// </summary>
    private static JToken DeepToken(int levels)
    {
        JToken token = new JValue(1);
        for (var i = 0; i < levels; i++)
        {
            token = new JArray(token);
        }
        return token;
    }

    [Fact]
    public void OpenTriggerPayload_DeepNesting_IsRejectedBeforeSerialization()
    {
        // 100k 层：不拦的话 RunCore 的 JsonConvert.SerializeObject 会爆栈打死进程
        var ex = Assert.Throws<BusinessException>(
            () => OpenTriggerTaskService.ValidatePayload(DeepToken(100_000)));
        Assert.Contains("嵌套过深", ex.Message);
    }

    [Fact]
    public void OpenTriggerPayload_AtDepthLimit_IsAccepted()
    {
        // 最内层节点深度恰好等于上限仍放行：上限只拦「超过」上限的结构
        OpenTriggerTaskService.ValidatePayload(DeepToken(OpenTriggerTaskService.MaxPayloadDepth));
    }

    [Fact]
    public void OpenTriggerPayload_JustOverDepthLimit_IsRejected()
    {
        var ex = Assert.Throws<BusinessException>(
            () => OpenTriggerTaskService.ValidatePayload(DeepToken(OpenTriggerTaskService.MaxPayloadDepth + 1)));
        Assert.Contains("嵌套过深", ex.Message);
    }

    [Fact]
    public void OpenTriggerPayload_WideStructure_IsRejectedByNodeCap()
    {
        var wide = new JArray();
        for (var i = 0; i <= OpenTriggerTaskService.MaxPayloadNodes; i++)
        {
            wide.Add(i);
        }
        var ex = Assert.Throws<BusinessException>(
            () => OpenTriggerTaskService.ValidatePayload(wide));
        Assert.Contains("节点数过多", ex.Message);
    }

    [Fact]
    public void OpenTriggerPayload_NormalShallowPayload_IsAccepted()
    {
        OpenTriggerTaskService.ValidatePayload(
            JToken.Parse("""{"event":"push","items":[{"id":1,"name":"a"},{"id":2,"name":"b"}],"ok":true}"""));
        // GET 链路传入的查询参数字典（非 JToken）与 null 同样放行
        OpenTriggerTaskService.ValidatePayload(new Dictionary<string, string> { ["a"] = "b" });
        OpenTriggerTaskService.ValidatePayload(null);
    }

    [Fact]
    public void OpenTriggerPayload_SingleHugeLeaf_IsRejectedBySizeCap()
    {
        // 节点数只有 2，深度只有 1：只有体积上限能拦住它（现场实测 20MB 单叶载荷曾被完整序列化进环境变量）
        var huge = new JObject { ["blob"] = new string('x', OpenTriggerTaskService.MaxPayloadChars + 1) };

        var ex = Assert.Throws<BusinessException>(() => OpenTriggerTaskService.ValidatePayload(huge));

        Assert.Contains("请求体过大", ex.Message);
    }

    [Fact]
    public void OpenTriggerPayload_PastBindingDepthCap_IsStillWalkedAndRejected()
    {
        // 绑定层（MVC 默认 Newtonsoft 读取侧 MaxDepth=32）曾把深于 32 层的载荷静默绑成 null：
        // 请求照旧「执行成功」、运行记录记成功，而载荷被丢弃。改为手动读流 + 显式放开读取深度后，
        // 判定权回到本方法。33 层属合法范围（应放行执行），超过上限才拒绝。
        OpenTriggerTaskService.ValidatePayload(DeepToken(33));
        OpenTriggerTaskService.ValidatePayload(DeepToken(OpenTriggerTaskService.MaxPayloadDepth));

        foreach (var levels in new[] { OpenTriggerTaskService.MaxPayloadDepth + 1, 1_000, 100_000 })
        {
            var ex = Assert.Throws<BusinessException>(
                () => OpenTriggerTaskService.ValidatePayload(DeepToken(levels)));
            Assert.Contains("过深", ex.Message);
        }
    }

    // ---------------------------------------------------------------- 分页上限与 Skip 溢出

    [Theory]
    [InlineData(100000, 1000000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(50000, 50000)]
    public void BaseQuery_Skip_NeverOverflowsToNegative(int pageIndex, int pageSize)
    {
        var query = new TaskQuery { PageIndex = pageIndex, PageSize = pageSize };

        Assert.True(query.Skip >= 0, $"Skip={query.Skip} 不得为负（负 OFFSET 在 MySQL 直接报错）");
        Assert.True(query.PageSize <= BaseQuery.MaxPageSize);
    }

    [Fact]
    public void BaseQuery_PageSize_IsClampedBothEnds()
    {
        Assert.Equal(BaseQuery.MinPageSize, new TaskQuery { PageSize = 0 }.PageSize);
        Assert.Equal(BaseQuery.MinPageSize, new TaskQuery { PageSize = -5 }.PageSize);
        Assert.Equal(BaseQuery.MaxPageSize, new TaskQuery { PageSize = 2_000_000_000 }.PageSize);
        Assert.Equal(20, new TaskQuery().PageSize);
        // 导出链路依赖的大页值必须仍在上限内
        Assert.Equal(999_999, new TaskQuery { PageSize = 999_999 }.PageSize);
    }

    [Fact]
    public void BaseQuery_PageIndex_IsClampedToMinimum()
    {
        Assert.Equal(1, new TaskQuery { PageIndex = -1 }.PageIndex);
        Assert.Equal(1, new TaskQuery { PageIndex = 0 }.PageIndex);
    }

    // ---------------------------------------------------------------- 摘要脱敏：超长输入不再拖住链路

    [Fact]
    public void SanitizeSummary_LongKeywordFreeRun_CompletesQuickly()
    {
        // 1MB 无关键字词串：修复前 SecretAssignPattern 在每个起点重扫，O(n²) 拖住执行链路
        var hostile = new string('a', 1_000_000);

        var started = DateTime.UtcNow;
        var clean = TaskExecutionResult.SanitizeSummary(hostile);
        var elapsed = DateTime.UtcNow - started;

        Assert.NotNull(clean);
        Assert.Equal(TaskExecutionResult.MaxSummaryLength, clean.Length);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"脱敏耗时 {elapsed.TotalSeconds:F1}s，疑似仍在做二次方扫描");
    }

    [Fact]
    public void SanitizeSummary_NormalSecrets_StillRedacted()
    {
        CacheManager.Set(new List<Quantum.Entities.Model.EnvModel>());

        var clean = TaskExecutionResult.SanitizeSummary(
            "连接失败，password=Admin@123，Authorization: Bearer eyJhbGciOi.some.token");

        Assert.DoesNotContain("Admin@123", clean);
        Assert.DoesNotContain("eyJhbGciOi", clean);
        Assert.Contains("***", clean);
    }

    // ---------------------------------------------------------------- SSE tool_calls index 上限

    [Fact]
    public void SseAccumulator_HugeToolCallIndex_DoesNotBlowUp()
    {
        var acc = new SseAccumulator();
        // 不拦的话 _calls 会被撑到 20 亿个对象（OOM）
        acc.Apply("""{"choices":[{"delta":{"tool_calls":[{"index":2000000000,"function":{"name":"x","arguments":"{}"}}]}}]}""");
        acc.Apply("""{"choices":[{"delta":{"tool_calls":[{"index":-1,"function":{"name":"y"}}]}}]}""");

        var response = acc.ToResponse(1);

        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public void SseAccumulator_IndexAtCapBoundary_Behaves()
    {
        var acc = new SseAccumulator();
        acc.Apply($"{{\"choices\":[{{\"delta\":{{\"tool_calls\":[{{\"index\":{SseAccumulator.MaxToolCallsPerResponse - 1},\"id\":\"c\",\"function\":{{\"name\":\"last\",\"arguments\":\"{{}}\"}}}}]}}}}]}}");

        var response = acc.ToResponse(1);

        Assert.Single(response.ToolCalls);
        Assert.Equal("last", response.ToolCalls[0].Name);
    }

    [Fact]
    public void SseAccumulator_LegitimateCalls_StillParsed()
    {
        var acc = new SseAccumulator();
        acc.Apply("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"read_script","arguments":""}}]}}]}""");
        acc.Apply("""{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"list_envs","arguments":"{}"}}]}}]}""");
        acc.Apply("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"file\":\"a.cs\"}"}}]}}]}""");

        var response = acc.ToResponse(1);

        Assert.Equal(2, response.ToolCalls.Count);
        Assert.Equal("""{"file":"a.cs"}""", response.ToolCalls[0].Arguments);
        Assert.Equal("list_envs", response.ToolCalls[1].Name);
    }
}
