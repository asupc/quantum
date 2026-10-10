using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using Quantum.Web.Controllers;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 外触内执请求体链路的对抗性回归（2026-10-11 第二轮）。
///
/// 背景：首轮修复（ValidatePayload 深度/节点上限）被独立验证者当场击穿——MVC 的 Newtonsoft
/// 输入格式器内部固定 JsonTextReader.MaxDepth=32，且本项目 SuppressModelStateInvalidFilter=true，
/// 于是**深于 32 层的请求体在绑定阶段就被静默绑成 null**：端点照旧返回「执行成功」、运行记录记
/// Succeeded/None，脚本却收到 null，载荷被悄悄丢弃（深度 33/40/64/1000 全部复现）。换参数类型
/// （object → JToken）无效，因为深度上限在格式器里与目标类型无关。
///
/// 因此服务端改为**手动读流 + JToken.Parse**（迭代式解析、无深度上限）。本类直接驱动真实
/// Controller，走 HttpContext.Request.Body，覆盖「绑定层」这一段——首轮用例只直调 ValidatePayload，
/// 看不到解绑层，故漏掉了这个洞。
/// </summary>
[Collection("ConstsState")]
public class OpenTriggerPayloadBindingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly OpenTriggerTaskController _controller;
    private readonly OpenTriggerTaskService _service;

    public OpenTriggerPayloadBindingTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new OpenTriggerTaskService(_db);
        _controller = new OpenTriggerTaskController(_service);
        CacheManager.Set(new List<OpenTriggerTask>());
        CacheManager.Set(new List<TaskModel>());
        CacheManager.Set(new List<EnvModel>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// 播种一条可命中的外触配置：同时写库并放进缓存。
    /// 必须写库——写路径走 CacheManager.Refresh（摘除键），随后 Get 会回源到库，
    /// 只 Set 缓存不写库会让「写路径是否刷新」的断言失去意义。
    /// </summary>
    private OpenTriggerTask SeedConfig(string secret = "advsecret", string whitelist = "127.0.0.1")
    {
        var row = new OpenTriggerTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "对抗测试",
            Secret = secret,
            // 脚本名校验只要求 scripts/quantum 内 + .cs；本类不断言执行结果，只断言入口是否被拦
            SrciptFile = "probe.cs",
            HttpMethod = "POST",
            Enable = true,
            Whitelist = whitelist
        };
        _db.OpenTriggerTasks.Add(row);
        _db.SaveChanges();
        _db.Entry(row).State = EntityState.Detached;
        CacheManager.Set(_db.OpenTriggerTasks.AsNoTracking().ToList());
        return row;
    }

    /// <summary>把控制器挂到带请求体的真实 HttpContext 上（模拟 Kestrel 收到的请求）。</summary>
    private void AttachBody(string rawBody)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawBody));
        httpContext.Request.ContentLength = httpContext.Request.Body.Length;
        httpContext.Request.Method = "POST";
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
        httpContext.RequestServices = new MinimalServiceProvider();
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    /// <summary>外触触发路径不解析任何服务（Controller 只用 Request.Body 与 Connection），空容器即可。</summary>
    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object GetService(Type serviceType) => null;
    }

    private static string NestedObjectJson(int levels, string marker = "MK")
    {
        var sb = new StringBuilder();
        for (var i = 0; i < levels; i++)
        {
            sb.Append("{\"a\":");
        }
        sb.Append('"').Append(marker).Append('"');
        sb.Append('}', levels);
        return sb.ToString();
    }

    private static string NestedArrayJson(int levels)
        => new string('[', levels) + "1" + new string(']', levels);

    [Fact]
    public async Task DeepObjectBody_IsEitherExecutedWithPayloadOrClearlyRejected()
    {
        SeedConfig();

        // 首轮击穿点：33 层及以上曾返回「执行成功」但载荷被绑成 null（静默丢弃）。
        // 现在的契约是确定性的：≤ 上限 → 执行；> 上限 → 明确拒绝。不存在第三种「报成功但载荷为 null」。
        foreach (var depth in new[] { 33, 40, 64 })
        {
            AttachBody(NestedObjectJson(depth, $"D{depth}"));
            Assert.Equal("执行成功", await _controller.TriggerByPost("advsecret"));
        }
        foreach (var depth in new[] { 65, 100, 1000 })
        {
            AttachBody(NestedObjectJson(depth, $"D{depth}"));
            var ex = await Assert.ThrowsAsync<BusinessException>(() => _controller.TriggerByPost("advsecret"));
            Assert.Contains("嵌套过深", ex.Message);
        }
    }

    [Fact]
    public async Task DeepBody_BeyondLimit_IsRejectedWithClearMessage()
    {
        SeedConfig();

        // 超过服务上限必须明确拒绝（而不是靠绑定层异常漏出泛化 500）
        AttachBody(NestedObjectJson(OpenTriggerTaskService.MaxPayloadDepth + 1));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => _controller.TriggerByPost("advsecret"));

        Assert.Contains("嵌套过深", ex.Message);
    }

    [Fact]
    public async Task DeepBody_WithinLimit_ReachesExecution()
    {
        SeedConfig();

        // 32/33 层在旧实现里分别对应「绑定层异常」与「静默 null」；现在都应正常走到执行
        foreach (var depth in new[] { 32, 33, OpenTriggerTaskService.MaxPayloadDepth })
        {
            AttachBody(NestedObjectJson(depth));

            var response = await _controller.TriggerByPost("advsecret");

            Assert.Equal("执行成功", response);
        }
    }

    [Fact]
    public async Task DeepArrayBody_IsHandledWithoutOpaqueGenericError()
    {
        SeedConfig();

        // 数组深体在旧实现里抛 ModelStateDictionary 的 InvalidOperationException（泛化 500）
        AttachBody(NestedArrayJson(OpenTriggerTaskService.MaxPayloadDepth - 1));
        Assert.Equal("执行成功", await _controller.TriggerByPost("advsecret"));

        AttachBody(NestedArrayJson(OpenTriggerTaskService.MaxPayloadDepth + 1));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => _controller.TriggerByPost("advsecret"));
        Assert.Contains("嵌套过深", ex.Message);
    }

    [Fact]
    public async Task EmptyOrMalformedBody_IsRejectedInsteadOfSilentlyNulled()
    {
        SeedConfig();

        // 空体/坏 JSON 曾一律「执行成功」且载荷为 null——正是静默丢弃的另一副面孔
        AttachBody("");
        var empty = await Assert.ThrowsAsync<BusinessException>(() => _controller.TriggerByPost("advsecret"));
        Assert.Contains("不能为空", empty.Message);

        AttachBody("{invalid json here");
        var broken = await Assert.ThrowsAsync<BusinessException>(() => _controller.TriggerByPost("advsecret"));
        Assert.Contains("合法 JSON", broken.Message);

        AttachBody("null");
        var literalNull = await Assert.ThrowsAsync<BusinessException>(() => _controller.TriggerByPost("advsecret"));
        Assert.Contains("不能为空", literalNull.Message);
    }

    [Fact]
    public async Task HugeSingleLeaf_IsRejectedBySizeCap()
    {
        SeedConfig();

        AttachBody(JsonConvert.SerializeObject(new { blob = new string('x', OpenTriggerTaskService.MaxPayloadChars + 1) }));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => _controller.TriggerByPost("advsecret"));

        Assert.Contains("请求体过大", ex.Message);
    }

    [Fact]
    public async Task WideBody_IsRejectedByNodeCap()
    {
        SeedConfig();

        AttachBody("[" + string.Join(',', Enumerable.Range(0, OpenTriggerTaskService.MaxPayloadNodes + 2)) + "]");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => _controller.TriggerByPost("advsecret"));

        Assert.Contains("节点数过多", ex.Message);
    }

    [Fact]
    public async Task NewConfig_InvalidatesCacheSoTriggerSeesIt()
    {
        // 首轮验证者发现的次生缺陷：AddAsync 不刷新缓存，Trigger 只读 CacheManager，
        // 于是新建配置在下一次任意刷新（如启停）之前形同不存在——「保存成功」后立即触发仍报
        // 「未找到相关配置」。断言写路径确实摘除了缓存键（CacheManager.Refresh 的语义）：
        // 键还在则 Get 返回初始哨兵快照，键已摘除则回源（真实库不会产出哨兵名）。
        SeedSentinel();
        Assert.Contains(CacheManager.Get<OpenTriggerTask>(), n => n.Name == SentinelName);

        await _service.AddAsync(new OpenTriggerTask
        {
            Name = "新建",
            Secret = "freshsecret",
            SrciptFile = "probe.cs",
            HttpMethod = "POST",
            Enable = true,
            Whitelist = "127.0.0.1"
        });

        Assert.DoesNotContain(CacheManager.Get<OpenTriggerTask>(), n => n.Name == SentinelName);
    }

    [Fact]
    public async Task DeletedConfig_InvalidatesCacheSoTriggerStopsSeeingIt()
    {
        SeedSentinel();
        var row = SeedConfig("gone-secret");

        await _service.DeleteAsync(row.Id);

        Assert.DoesNotContain(CacheManager.Get<OpenTriggerTask>(), n => n.Name == SentinelName);
    }

    [Fact]
    public async Task UpdatedConfig_InvalidatesCache()
    {
        SeedSentinel();
        var row = SeedConfig("upd-secret");

        await _service.AddAsync(new OpenTriggerTask
        {
            Id = row.Id,
            Name = row.Name,
            Secret = "upd-secret",
            SrciptFile = row.SrciptFile,
            HttpMethod = row.HttpMethod,
            Enable = row.Enable,
            Whitelist = "10.9.9.9"
        });

        Assert.DoesNotContain(CacheManager.Get<OpenTriggerTask>(), n => n.Name == SentinelName);
    }

    /// <summary>哨兵名：独一无二，回源真实库绝不可能产出同名行。</summary>
    private const string SentinelName = "__adv_sentinel_never_in_db__";

    /// <summary>把缓存置为「只含一行哨兵」的快照，用于观察写路径是否摘除缓存键。</summary>
    private static void SeedSentinel()
        => CacheManager.Set(new List<OpenTriggerTask>
        {
            new() { Id = "sentinel-id", Name = SentinelName, Secret = "sentinel", SrciptFile = "probe.cs" }
        });
}
