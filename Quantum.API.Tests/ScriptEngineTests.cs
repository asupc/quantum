using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Plugins;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 脚本执行引擎改造（2026-09-16 计划）回归：
/// 门禁三级扫描（类别拦截/零误报/别名绕过/警告不阻断）、编译服务（诊断聚合/哈希缓存/契约预检）、
/// 进程内执行器（ctx 注入/Variables 合并/协作取消/异常隔离/执行侧二次校验）、
/// 门面直调（Env 增删改查/Notify 契约）、白名单（.cs 唯一形态）与保存流水线拒绝落盘。
/// 与 ConstsState 同 Collection：EnvService 读侧走 CacheManager 全表缓存，未命中会按当前
/// SystemConfigHelper.configPath 回源开库（连接池持有文件句柄）——须与临时改写 configPath 的
/// UserSystemRemovalTests 串行，否则其清理 File.Delete 会撞池内句柄。
/// </summary>
[Collection("ConstsState")]
public class ScriptEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;

    public ScriptEngineTests()
    {
        (_connection, _db) = AppTestDb.Create();
        // EnvModel 缓存置空：保证本类所有 CacheManager.Get 命中缓存、绝不触发回源开库（隔离并行集合的 configPath 状态）
        CacheManager.Set(new List<EnvModel>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ==================================================================== 门禁 ①②：类别拦截

    [Theory]
    [InlineData("io", "var t = System.IO.File.ReadAllText(\"a.txt\");")]
    [InlineData("io-literal", "using System.IO;\npublic class T { void M() { File.Exists(\"a\"); } }")]
    [InlineData("process", "var p = new System.Diagnostics.Process();")]
    [InlineData("process-start", "System.Diagnostics.Process.Start(\"cmd\");")]
    [InlineData("env-exit", "System.Environment.Exit(0);")]
    [InlineData("env-props", "var u = System.Environment.UserName;")]
    [InlineData("reflection", "var a = System.Reflection.Assembly.Load(\"x\");")]
    [InlineData("runtime-loader", "var ctx = new System.Runtime.Loader.AssemblyLoadContext(\"x\");")]
    [InlineData("activator", "var o = System.Activator.CreateInstance(typeof(object));")]
    [InlineData("marshal", "var i = System.Runtime.InteropServices.Marshal.SizeOf(typeof(int));")]
    [InlineData("dllimport", "using System.Runtime.InteropServices;\npublic class T { [DllImport(\"x\")] static extern void F(); } }")]
    [InlineData("registry", "var k = Microsoft.Win32.Registry.CurrentUser;")]
    [InlineData("socket", "var s = new System.Net.Sockets.TcpClient();")]
    [InlineData("thread", "System.Threading.Thread.Sleep(100);")]
    [InlineData("appdomain", "var d = System.AppDomain.CurrentDomain;")]
    [InlineData("expressions", "var e = System.Linq.Expressions.Expression.Constant(1);")]
    [InlineData("console", "Console.WriteLine(\"x\");")]
    [InlineData("type-gettype", "var t = System.Type.GetType(\"System.IO.File, System.Runtime\");")]
    [InlineData("unsafe", "public unsafe class T { }")]
    [InlineData("unsafe-block", "public class T { unsafe void M() { } } }")]
    [InlineData("stackalloc", "public class T { unsafe void M() { char* b = stackalloc char[10]; } }")]
    [InlineData("pointer", "public class T { unsafe void M(int* p) { } }")]
    [InlineData("fixed", "public class T { unsafe void M(int[] a) { fixed (int* p = a) { } } }")]
    [InlineData("dynamic", "public class T { object M() { dynamic x = 1; return x.Foo(); } }")]
    [InlineData("microsoft-csharp", "var b = new Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo();")]
    public void Gate_BlocksEachDangerousCategory(string _, string source)
    {
        var result = BuildWrapped(source);
        Assert.False(result.Passed, $"应被门禁拦截：{source}");
    }

    [Theory]
    [InlineData("var-推断文件流", "public class T { void M() { var f = Get(); f.CopyTo(System.IO.File.Create(\"x\")); } System.IO.FileInfo Get() => null; }")]
    [InlineData("using-别名", "using F = System.IO.File;\npublic class T { void M() { F.Exists(\"a\"); } }")]
    [InlineData("using-命名空间别名", "using IO = System.IO;\npublic class T { void M() { IO.File.Exists(\"a\"); } }")]
    [InlineData("namespace-伪装", "namespace System.IO { public class T { void M() { File.Exists(\"a\"); } } }")]
    public void Gate_AliasVarAndNamespaceTricks_CannotBypass(string _, string source)
    {
        // 语义级按「符号最终归属」判定：别名/var/命名空间伪装无法洗白
        var result = BuildWrapped(source);
        Assert.False(result.Passed, $"混淆形态应被拦截：{source}");
    }

    [Fact]
    public void Gate_CleanTask_PassesWithoutBlocked()
    {
        var source = """
            using Quantum.Plugins;
            public class CleanTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    ctx.Log("hello " + ctx.TaskName);
                    await Task.Delay(10, ct);
                }
            }
            """;
        var result = BuildWrapped(source);
        Assert.True(result.Passed);
        Assert.Empty(result.Blocked);
    }

    // ==================================================================== 门禁 ③：启发式警告（不阻断）

    [Fact]
    public void Gate_DeadLoopWithoutCt_WarnsButDoesNotBlock()
    {
        var source = """
            using Quantum.Plugins;
            public class LoopTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    while (true) { }
                    return Task.CompletedTask;
                }
            }
            """;
        var result = BuildWrapped(source);
        Assert.True(result.Passed, "警告类不阻断");
        Assert.Contains(result.Warnings, w => w.Code == "dead-loop");
    }

    [Fact]
    public void Gate_DeadLoopWithCt_NoDeadLoopWarning()
    {
        var source = """
            using Quantum.Plugins;
            public class LoopTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    while (!ct.IsCancellationRequested) { await Task.Delay(100, ct); }
                }
            }
            """;
        var result = BuildWrapped(source);
        Assert.True(result.Passed);
        Assert.DoesNotContain(result.Warnings, w => w.Code == "dead-loop");
    }

    [Fact]
    public void Gate_HardcodedUrlOutsideCtxHttp_WarnsButCtxHttpCallDoesNot()
    {
        var source = """
            using Quantum.Plugins;
            public class UrlTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    await ctx.Http.GetAsync("https://example.com/a", ct);
                    var u = "http://example.com/b";
                    ctx.Log(u);
                }
            }
            """;
        var result = BuildWrapped(source);
        Assert.True(result.Passed);
        // ctx.Http 调用实参内的 URL 不告警；裸字符串里的 URL 告警
        Assert.DoesNotContain(result.Warnings, w => w.Code == "hardcoded-url" && w.Message.Contains("example.com/a"));
        Assert.Contains(result.Warnings, w => w.Code == "hardcoded-url" && w.Message.Contains("example.com/b"));
    }

    // ==================================================================== 编译服务

    [Fact]
    public void Build_SyntaxError_ReturnsErrorsWithoutGate()
    {
        var result = ScriptBuildService.Build("public class Broken {");
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Build_SameSource_HitsCacheByHash()
    {
        var source = """
            using Quantum.Plugins;
            public class CachedTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """;
        var first = ScriptBuildService.Build(source);
        var second = ScriptBuildService.Build(source);
        // 同哈希命中缓存：同一结果实例（零重编）
        Assert.Same(first, second);
        Assert.True(first.Success);
        var other = ScriptBuildService.Build(source + "\n// v2");
        Assert.NotEqual(first.Hash, other.Hash);
    }

    [Fact]
    public void Build_FailedVersion_NotKeptInCache()
    {
        // 失败版本不入缓存：编辑期反复「保存-失败-重试」曾逐版累积永驻条目（含门禁/编译诊断对象）
        var source = "public class Broken {";
        var first = ScriptBuildService.Build(source);
        var second = ScriptBuildService.Build(source);
        Assert.False(first.Success);
        Assert.NotSame(first, second); // 失败即弃，第二次是重新计算的结果
    }

    [Fact]
    public void Remove_CleansVersionTracking_AndRebuildStillWorks()
    {
        // 删除脚本清缓存（TaskService.DeleteScript 调用）：移除后同内容重建仍成功、幂等
        var fileName = $"remove-test-{Guid.NewGuid():N}.cs";
        var source = """
            using Quantum.Plugins;
            public class RebuildTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """;
        var built = ScriptBuildService.Build(source, fileName);
        Assert.True(built.Success);

        ScriptBuildService.Remove(fileName);

        var rebuilt = ScriptBuildService.Build(source, fileName);
        Assert.True(rebuilt.Success);
        Assert.Equal(built.Hash, rebuilt.Hash);
        ScriptBuildService.Remove(fileName); // 幂等，不抛
    }

    [Fact]
    public void Build_BlockedSource_HasNoTaskType()
    {
        var result = ScriptBuildService.Build("using System.IO;\npublic class X { void M() { File.Exists(\"a\"); } }");
        Assert.False(result.Success);
        Assert.NotEmpty(result.Blocked);
        Assert.Null(result.TaskTypeName);
    }

    [Theory]
    [InlineData("no-task", "public class X { }")]
    [InlineData("multi-task", "using Quantum.Plugins;\npublic class A : IQuantumTask { public Task RunAsync(QuantumTaskContext c, CancellationToken t) => Task.CompletedTask; }\npublic class B : IQuantumTask { public Task RunAsync(QuantumTaskContext c, CancellationToken t) => Task.CompletedTask; }")]
    [InlineData("task-not-public", "using Quantum.Plugins;\ninternal class A : IQuantumTask { public Task RunAsync(QuantumTaskContext c, CancellationToken t) => Task.CompletedTask; }")]
    [InlineData("task-no-ctor", "using Quantum.Plugins;\npublic class A : IQuantumTask { public A(int x) { } public Task RunAsync(QuantumTaskContext c, CancellationToken t) => Task.CompletedTask; }")]
    public void Build_ContractViolations_RejectedWithFriendlyError(string code, string source)
    {
        var result = ScriptBuildService.Build(source);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    [Fact]
    public void Build_ReferenceWhitelistLibraries_AreUsable()
    {
        // 计划 2.5 引用集：Newtonsoft / HtmlAgilityPack / Microsoft.Data.Sqlite / EF Sqlite / BCL crypto / System.Text.Json
        var source = """
            using System.Security.Cryptography;
            using System.Text;
            using HtmlAgilityPack;
            using Microsoft.Data.Sqlite;
            using Newtonsoft.Json;
            using Newtonsoft.Json.Linq;
            using Quantum.Plugins;
            public class RefsTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    var md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("x")));
                    var json = JObject.Parse("{\"ok\":true}");
                    var stj = System.Text.Json.JsonSerializer.Serialize(new { a = 1 });
                    var doc = new HtmlDocument();
                    doc.LoadHtml("<ul><li>1</li></ul>");
            await using var conn = new SqliteConnection("Data Source=:memory:");
            await conn.OpenAsync(ct);
            var ef = typeof(Microsoft.EntityFrameworkCore.DbContext);
            ctx.Log($"{md5} {json["ok"]} {stj} {doc.DocumentNode.SelectNodes("//li").Count} {ef != null}");
                }
            }
            """;
        var result = ScriptBuildService.Build(source);
        Assert.True(result.Success, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Build_DemoScripts_AllPassPipeline()
    {
        // 仓库随带的三个示例任务（含全接口示例）必须始终能过门禁+编译——它们是用户抄写模板
        foreach (var demo in Directory.GetFiles(FindDemoDir(), "*.cs"))
        {
            var result = ScriptBuildService.Build(File.ReadAllText(demo), demo);
            Assert.True(result.Success, $"示例 {Path.GetFileName(demo)} 未通过流水线："
                + string.Join("\n", result.Blocked.Select(b => $"[L{b.Line}]{b.Message}"))
                + string.Join("\n", result.Errors.Select(e => $"[L{e.Line}]{e.Message}")));
        }
    }

    [Fact]
    public void Build_DiagnosticLines_MatchScriptOwnLineNumbers()
    {
        // 编译服务前置「两行 global using + #line 1」预置：诊断行号必须是脚本自身行号。
        // 若取物理行号（GetLineSpan），前端「第 N 行」会整体偏移 +2，定位到无关代码。
        var blockedProbe = """
            using Quantum.Plugins;
            public class ProbeTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    var text = System.IO.File.ReadAllText("a.txt");
                    return Task.CompletedTask;
                }
            }
            """;
        var blocked = ScriptBuildService.Build(blockedProbe);
        Assert.Contains(blocked.Blocked, n => n.Line == 6);

        var errorProbe = """
            using Quantum.Plugins;
            public class ProbeTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    int number = "not a number";
                    return Task.CompletedTask;
                }
            }
            """;
        var errors = ScriptBuildService.Build(errorProbe);
        var compileError = Assert.Single(errors.Errors, n => n.Code == "CS0029");
        Assert.Equal(6, compileError.Line);
    }

    [Fact]
    public void Build_NewSourceVersionForSameFile_UnloadsOldBestEffort()
    {
        var fileName = Path.GetFullPath("scripts/quantum/engine_version_test.cs");
        Directory.CreateDirectory("scripts/quantum");
        var v1 = "using Quantum.Plugins;\npublic class VTask : IQuantumTask { public Task RunAsync(QuantumTaskContext c, CancellationToken t) => Task.CompletedTask; }";
        var v2 = v1 + "\n// v2";
        var r1 = ScriptBuildService.Build(v1, fileName);
        var r2 = ScriptBuildService.Build(v2, fileName);
        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.NotEqual(r1.Hash, r2.Hash);
        // 旧版本被卸载后再执行 v1 源码：重新构建即可（文件已不引用旧哈希）
        var again = ScriptBuildService.Build(v1, fileName);
        Assert.True(again.Success);
    }

    [Fact]
    public void GetOrLoadType_AfterUnload_RecompilesInsteadOfCrashing()
    {
        // §1-11：首载后 AssemblyBytes 已置空；若条目被卸载（文件更新/驱逐）而持租约者再入，
        // 旧实现走 LoadFromBytes(null) → new MemoryStream(null) 抛。修后凭留存源码重编译恢复。
        var source = """
            using Quantum.Plugins;
            public class ReloadRaceTask11 : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """;
        var result = ScriptBuildService.Build(source);
        Assert.True(result.Success);

        var entry = ReadCacheEntry(result.Hash);
        Assert.NotNull(entry.GetOrLoadType());   // 首次加载：AssemblyBytes 归位后被清空
        entry.Unload();                          // 模拟驱逐后的卸载：context/type 置空、字节早已 null

        var reloaded = entry.GetOrLoadType();    // 修复前此处抛 ArgumentNullException
        Assert.NotNull(reloaded);
        Assert.True(typeof(IQuantumTask).IsAssignableFrom(reloaded));
    }

    [Fact]
    public void Evict_WhileLeaseHeld_DefersUnloadUntilRelease()
    {
        // §1-11：驱逐统一按 _activeCount——仍有活跃租约时 Evict 只标记待卸，不强卸（否则在跑脚本的 ALC 被抽走）；
        // 最后一个租约 Release 时才补卸载。全程不得抛。
        var source = """
            using Quantum.Plugins;
            public class DeferredUnloadTask11 : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """;
        var result = ScriptBuildService.Build(source);
        var entry = ReadCacheEntry(result.Hash);

        entry.Acquire();
        entry.Evict();                       // 活跃租约持有 → 只置 stale
        Assert.NotNull(entry.GetOrLoadType()); // 仍可取类型（未因强卸而坏）
        entry.Release();                     // 归零 → 补卸载，不抛
        Assert.Equal(0, entry.ActiveCount);
    }

    /// <summary>反射取 ScriptBuildService 私有静态 Cache 中某哈希的编译条目（CompiledEntry 经 InternalsVisibleTo 可见）。</summary>
    private static ScriptBuildService.CompiledEntry ReadCacheEntry(string hash)
    {
        var field = typeof(ScriptBuildService).GetField("Cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var cache = field!.GetValue(null)!;
        var lazy = ((System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<ScriptBuildService.CompiledEntry>>)cache)[hash];
        return lazy.Value;
    }

    // ==================================================================== 执行器（进程内）

    [Fact]
    public async Task ExecTask_HelloTask_LogsToDiskAndClearsLiveBuffer()
    {
        var script = WriteScript("engine_hello.cs", """
            using Quantum.Plugins;
            public class HelloTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    ctx.Log("hello from in-process task");
                    ctx.Log($"task name = {ctx.TaskName}");
                    return Task.CompletedTask;
                }
            }
            """);
        var logPath = await RunStep(script, "Hello任务", forceEndSeconds: 60);
        Assert.False(TaskExcuteService.Logs.ContainsKey(logPath), "执行结束后实时日志缓冲应清除");
        var logText = File.ReadAllText(LatestLog(script));
        Assert.Contains("hello from in-process task", logText);
        Assert.Contains("task name = Hello任务", logText);
        Assert.Contains("执行任务用时", logText);
    }

    [Fact]
    public async Task ExecTask_Variables_MergedWithAmpersandAndCtxFlagsExposed()
    {
        var script = WriteScript("engine_vars.cs", """
            using Quantum.Plugins;
            public class VarsTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    ctx.Log("A=" + ctx.Variables["A"]);
                    ctx.Log("B=" + (ctx.Variables.TryGetValue("B", out var b) ? b : "(none)"));
                    ctx.Log($"proxy={ctx.EnableProxy} push={ctx.EnablePush}");
                    return Task.CompletedTask;
                }
            }
            """);
        var step = NewStep(script, "变量测试", forceEndSeconds: 60);
        step.Task.EnableProxy = true;
        step.Envs =
        [
            new EnvModel { Name = "A", Value = "1" },
            new EnvModel { Name = "A", Value = "3" },
            new EnvModel { Name = "B", Value = "2" }
        ];
        await step.Run();
        var logText = File.ReadAllText(LatestLog(script));
        Assert.Contains("A=1&3", logText);
        Assert.Contains("B=2", logText);
        Assert.Contains("proxy=True push=False", logText);
    }

    [Fact]
    public async Task ExecTask_TaskException_IsolatedFromNextTask()
    {
        var thrower = WriteScript("engine_throw.cs", """
            using Quantum.Plugins;
            public class ThrowTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                    => throw new InvalidOperationException("boom-from-task");
            }
            """);
        await RunStep(thrower, "异常任务", forceEndSeconds: 30);
        var throwLog = File.ReadAllText(LatestLog(thrower));
        Assert.Contains("boom-from-task", throwLog);

        var greeter = WriteScript("engine_after_throw.cs", """
            using Quantum.Plugins;
            public class AfterThrowTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    ctx.Log("still alive");
                    return Task.CompletedTask;
                }
            }
            """);
        await RunStep(greeter, "后续任务", forceEndSeconds: 30);
        Assert.Contains("still alive", File.ReadAllText(LatestLog(greeter)));
    }

    [Fact]
    public async Task ExecTask_UnresponsiveTask_CancelSignalLoggedThenCompletesAlone()
    {
        // 任务不检查 ct：到期仅记录「已发出协作取消信号」，线程保留至任务自行结束（协作取消边界语义）
        var script = WriteScript("engine_unresponsive.cs", """
            using Quantum.Plugins;
            public class UnresponsiveTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        await Task.Delay(250); // 故意不携带 ct
                    }
                    ctx.Log("self completed");
                }
            }
            """);
        await RunStep(script, "不响应任务", forceEndSeconds: 1);
        var logText = File.ReadAllText(LatestLog(script));
        Assert.Contains("已发出协作取消信号", logText);
        Assert.Contains("self completed", logText);
    }

    [Fact]
    public async Task ExecTask_BlockedSourceOnDisk_RefusedAtExecutionSide()
    {
        // 绕过保存 API 手写到磁盘的危险源码：执行侧二次校验拒绝（历史脏数据兜底）
        var script = WriteScript("engine_blocked.cs", """
            using System.IO;
            using Quantum.Plugins;
            public class BlockedTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    File.ReadAllText("c:/windows/win.ini");
                    return Task.CompletedTask;
                }
            }
            """);
        await RunStep(script, "危险任务", forceEndSeconds: 30);
        var logText = File.ReadAllText(LatestLog(script));
        Assert.Contains("未通过安全门禁", logText);
        Assert.Contains("文件系统访问被禁止", logText);
    }

    [Fact]
    public async Task ExecTask_LegacyJsScript_RefusedWithMigrationGuidance()
    {
        var script = WriteScript("engine_legacy.js", "console.log('old world');\n");
        await RunStep(script, "遗留任务", forceEndSeconds: 30);
        var logText = File.ReadAllText(LatestLog("engine_legacy.js"));
        Assert.Contains("仅支持 .cs 源码任务", logText);
        Assert.Contains("停止支持", logText);
    }

    [Fact]
    public async Task ExecTask_EchoFacadeEnvQueryThroughCtx_EngineScopeWorks()
    {
        // 执行链路经 TaskPluginHost 独立 DI scope 解析 EnvService（模块初始化器装配）
        var script = WriteScript("engine_env_echo.cs", """
            using Quantum.Plugins;
            public class EnvEchoTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    var list = await ctx.Env.QueryAsync("engine_echo_none", null, ct);
                    ctx.Log($"env count = {list.Count}");
                }
            }
            """);
        await RunStep(script, "门面任务", forceEndSeconds: 30);
        Assert.Contains("env count = 0", File.ReadAllText(LatestLog(script)));
    }

    // ==================================================================== 门面直调

    [Fact]
    public async Task EnvFacade_SaveQueryToggleDelete_RoundTrip()
    {
        var seeded = new EnvModel { Id = "E1", Name = "engine_round_trip", Value = "v1", Enable = true, CreateTime = DateTime.Now, UpdateTime = DateTime.Now };
        _db.Envs.Add(seeded);
        await _db.SaveChangesAsync();
        CacheManager.Set(new List<EnvModel> { seeded });
        var facade = new QuantumEnvFacade(new EnvService(_db));

        // Query（缓存）
        var found = await facade.QueryAsync(name: "engine_round_trip");
        Assert.Single(found);
        Assert.Equal("v1", found[0].Value);
        Assert.True(found[0].Enabled);

        // SetEnabled（写库断言）
        await facade.SetEnabledAsync("engine_round_trip", enabled: false);
        CacheManager.Set(new List<EnvModel> { await _db.Envs.AsNoTracking().SingleAsync(n => n.Id == "E1") });
        Assert.False((await _db.Envs.AsNoTracking().SingleAsync(n => n.Id == "E1")).Enable);

        // Save（新增分支，写库断言）
        await facade.SaveAsync("engine_round_trip_new", "v2", "remark", enabled: true);
        Assert.True(await _db.Envs.AnyAsync(n => n.Name == "engine_round_trip_new" && n.Value == "v2"));

        // 镜像生产写后缓存回填（AddOrUpdateEnvs 按缓存中的 Id 合并；生产由 QueryData 回读真库完成）。
        // 清 ChangeTracker：生产是每次请求新 scoped DbContext，测试复用实例需解除旧跟踪避免主键冲突
        CacheManager.Set(await _db.Envs.AsNoTracking().ToListAsync());
        _db.ChangeTracker.Clear();

        // Save（同名更新分支：复用 Id，不产生新行）
        await facade.SaveAsync("engine_round_trip_new", "v3");
        var rows = await _db.Envs.AsNoTracking().Where(n => n.Name == "engine_round_trip_new").ToListAsync();
        Assert.Single(rows);
        Assert.Equal("v3", rows[0].Value);

        // DeleteByName（缓存命中→删库）
        CacheManager.Set(await _db.Envs.AsNoTracking().ToListAsync());
        await facade.DeleteByNameAsync("engine_round_trip");
        Assert.False(await _db.Envs.AnyAsync(n => n.Name == "engine_round_trip"));
    }

    [Fact]
    public async Task NotifyFacade_EmptyContent_RejectedByServiceContract()
    {
        var facade = new QuantumNotifyFacade(new NotifyService(), "test-task", "test-task");
        // 空消息在 NotifyService 入口即拒绝（直调链路与 HTTP 入口同一份业务校验）
        await Assert.ThrowsAsync<BusinessException>(() => facade.SendAsync("标题", ""));
        // 富媒体同契约：message 即媒体地址，空地址同样拒绝
        await Assert.ThrowsAsync<BusinessException>(() => facade.SendImageAsync("", "配文"));
        await Assert.ThrowsAsync<BusinessException>(() => facade.SendVideoAsync("", null));
    }

    [Fact]
    public async Task NotifyFacade_RichMedia_MapsTypeAndCaption()
    {
        // 换发送接缝捕获 DTO（只留本测试哨兵内容，隔离其他测试可能入队的消息）；
        // 静态泵可能抢先出队但走同一 Sender，故按哨兵过滤 + 轮询等待
        var captured = new List<MessageProccessDTO>();
        var original = SendMessageHelper.Sender;
        SendMessageHelper.Sender = m =>
        {
            if (m.message != null && m.message.Contains("facade-rich-test", StringComparison.Ordinal))
            {
                captured.Add(m);
            }
            return Task.CompletedTask;
        };
        try
        {
            var facade = new QuantumNotifyFacade(new NotifyService(), "test-task", "test-task");
            await facade.SendImageAsync("https://img/facade-rich-test.jpg", "图片配文");
            await facade.SendVideoAsync("https://vid/facade-rich-test.mp4", null);
            for (var i = 0; captured.Count < 2 && i < 100; i++)
            {
                await Task.Delay(10);
            }

            Assert.Equal(2, captured.Count);
            Assert.Equal(MessageType.图片, captured[0].MessageType);
            Assert.Equal("https://img/facade-rich-test.jpg", captured[0].message);
            Assert.Equal("图片配文", captured[0].message_text);
            Assert.Equal(MessageType.视频, captured[1].MessageType);
            Assert.Equal("https://vid/facade-rich-test.mp4", captured[1].message);
            Assert.Null(captured[1].message_text);
        }
        finally
        {
            SendMessageHelper.Sender = original;
        }
    }

    [Fact]
    public async Task CustomDataFacade_AddQueryDelete_RoundTrip()
    {
        var facade = new QuantumCustomDataFacade(new CustomDataService(_db), new CustomDataTitleService(_db));
        await facade.AddAsync(
        [
            new QuantumCustomDataValue { Type = "engine_cd", Data1 = "abc", Data2 = "secret", Data4 = "u1" }
        ]);

        // 包含匹配 + 多条件与关系（与数据管理页查询语义一致）
        var hit = await facade.QueryAsync("engine_cd", data1: "b", data4: "u1");
        var item = Assert.Single(hit);
        Assert.Equal("secret", item.Data2);
        Assert.False(string.IsNullOrEmpty(item.Id));
        Assert.NotEqual(default, item.CreateTime);

        // 条件不命中
        Assert.Empty(await facade.QueryAsync("engine_cd", data4: "other"));
        Assert.Empty(await facade.QueryAsync("engine_cd_other"));

        // 按 Id 删除
        await facade.DeleteAsync(hit.Select(n => n.Id).ToList());
        Assert.Empty(await facade.QueryAsync("engine_cd"));
    }

    [Fact]
    public async Task CustomDataFacade_FullColumnsFilterUpdateAndTitle_RoundTrip()
    {
        var facade = new QuantumCustomDataFacade(new CustomDataService(_db), new CustomDataTitleService(_db));

        // 全列写入（健康/项目类脚本按 Data11 日期去重、Data6-Data12 承载指标）
        await facade.AddAsync(
        [
            new QuantumCustomDataValue
            {
                Type = "engine_cd_full",
                Data1 = "70.5", Data6 = "9", Data7 = "52.3", Data11 = "2026-09-17 08", Data12 = "rec-1"
            },
            new QuantumCustomDataValue
            {
                Type = "engine_cd_full",
                Data1 = "70.1", Data6 = "9", Data7 = "52.0", Data11 = "2026-09-18 08", Data12 = "rec-2"
            }
        ]);

        // Data11（高级列）过滤：替代原脚本 getCustomData(type, null, null, { Data11: date }) 的去重判断
        var byDate = await facade.QueryAsync("engine_cd_full", new QuantumCustomDataFilter { Data11 = "2026-09-17 08" });
        var row = Assert.Single(byDate);
        Assert.Equal("rec-1", row.Data12);
        Assert.Equal("9", row.Data6);

        // 创建时间范围过滤（闭区间）
        var inRange = await facade.QueryAsync("engine_cd_full", new QuantumCustomDataFilter
        {
            CreateTimeStart = DateTime.Now.AddDays(-1),
            CreateTimeEnd = DateTime.Now.AddDays(1)
        });
        Assert.Equal(2, inRange.Count);
        Assert.Empty(await facade.QueryAsync("engine_cd_full", new QuantumCustomDataFilter
        {
            CreateTimeStart = DateTime.Now.AddDays(1)
        }));

        // 按 Id 更新（保 Id 改列，行数不变）。
        // 生产每次任务执行是独立 scoped DbContext；测试复用实例须先解除旧跟踪，否则同主键双实例冲突
        _db.ChangeTracker.Clear();
        await facade.UpdateAsync(
        [
            new QuantumCustomDataValue
            {
                Id = row.Id, Type = "engine_cd_full",
                Data1 = "70.5", Data6 = "9", Data7 = "51.8", Data11 = "2026-09-17 08", Data12 = "rec-1-upd"
            }
        ]);
        var updated = Assert.Single(await facade.QueryAsync("engine_cd_full", new QuantumCustomDataFilter { Data12 = "rec-1-upd" }));
        Assert.Equal("51.8", updated.Data7);
        Assert.Equal(row.Id, updated.Id);
        Assert.Equal(2, (await facade.QueryAsync("engine_cd_full")).Count);

        // 缺 Id 的更新拒绝（否则 UpdateRange 会按空主键插入重复行）
        await Assert.ThrowsAsync<BusinessException>(() => facade.UpdateAsync(
            [new QuantumCustomDataValue { Type = "engine_cd_full", Data1 = "x" }]));

        // 表头（数据管理页列名）：新增后可按类型回读，再更新不产生重复行
        await facade.SaveTitleAsync("engine_cd_full", "引擎测试数据", ["体重（kg）", "BMI", "体脂率（%）"]);
        var title = await _db.CustomDataTitles.AsNoTracking().SingleAsync(n => n.Type == "engine_cd_full");
        Assert.Equal("引擎测试数据", title.TypeName);
        Assert.Equal("体重（kg）", title.Title1);
        Assert.Equal("体脂率（%）", title.Title3);
        Assert.Null(title.Title4);
        // 同上：二次写入前解除上一次的跟踪（生产每次执行独立 scoped DbContext）
        _db.ChangeTracker.Clear();
        await facade.SaveTitleAsync("engine_cd_full", "引擎测试数据2", ["a"]);
        var titles = await _db.CustomDataTitles.AsNoTracking().Where(n => n.Type == "engine_cd_full").ToListAsync();
        Assert.Single(titles);
        Assert.Equal("引擎测试数据2", titles[0].TypeName);
        Assert.Equal("a", titles[0].Title1);
        Assert.Null(titles[0].Title3);
    }

    [Fact]
    public async Task Converted_PasswordScripts_PassPipelineAndExecuteEndToEnd()
    {
        // 由 quantum_password.cs / quantum_password_query.cs 转换的两个任务：门禁+编译全过，端到端可执行
        // （10adb15 删除 scripts/quantum 旧目录后移至 scripts/ 根目录；生成器后更名为正确拼写 quantum_password.cs）
        var genPath = Path.Combine(FindRepoRoot(), "Quantum.API", "Quantum.Web", "scripts", "quantum_password.cs");
        var queryPath = Path.Combine(FindRepoRoot(), "Quantum.API", "Quantum.Web", "scripts", "quantum_password_query.cs");
        foreach (var path in new[] { genPath, queryPath })
        {
            var build = ScriptBuildService.Build(File.ReadAllText(path), path);
            Assert.True(build.Success, $"{Path.GetFileName(path)} 未通过流水线："
                + string.Join(";", build.Blocked.Select(b => b.Message))
                + string.Join(";", build.Errors.Select(e => e.Message)));
        }
        var generator = await File.ReadAllTextAsync(genPath);
        var query = await File.ReadAllTextAsync(queryPath);

        // 「随机密码测试-1」：不落库，仅日志+通知
        var noSave = WriteScript("engine_pwd_nosave.cs", generator);
        await RunStepWithVars(noSave, "随机密码", [("command", "随机密码测试-1")]);
        var noSaveLog = File.ReadAllText(LatestLog(noSave));
        Assert.Contains("生成密码：", noSaveLog);

        // 「随机密码演示」：写入引擎宿主库（TaskPluginHost 的进程内存库），Data1/ Data4 语义正确
        var save = WriteScript("engine_pwd_save.cs", generator);
        await RunStepWithVars(save, "随机密码",
        [
            ("command", "随机密码演示"), ("CommunicationUserId", "u-engine-test"), ("CommunicationUserName", "引擎测试者")
        ]);
        var saveLog = File.ReadAllText(LatestLog(save));
        Assert.Contains("生成密码：", saveLog);
        using (var scope = TaskPluginHost.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<CustomDataService>()
                .Get("quantum_password", new CustomDataModel { Data1 = "演示", Data4 = "u-engine-test" }, null, null, null);
            var row = Assert.Single(rows.Data);
            Assert.Equal("随机密码演示", row.Data3);
            Assert.Equal("引擎测试者", row.Data5);
            Assert.Matches(@"^[0-9A-Za-z!@#$%^&*()\-=_+,.;':]{18}$", row.Data2);

            // 「我的密码演示」查询任务：命中即发通知（静态队列无消费者，执行不抛错即通过）
            var queryScript = WriteScript("engine_pwd_query.cs", query);
            await RunStepWithVars(queryScript, "我的密码",
                [("command", "我的密码演示"), ("CommunicationUserId", "u-engine-test")]);
            Assert.Contains("任务结束时间", File.ReadAllText(LatestLog(queryScript)));

            // 清理引擎测试数据
            var all = await scope.ServiceProvider.GetRequiredService<CustomDataService>()
                .Get("quantum_password", new CustomDataModel(), null, null, null);
            await scope.ServiceProvider.GetRequiredService<CustomDataService>()
                .DeleteAsync(all.Data.Select(r => r.Id).ToList());
        }
    }

    [Fact]
    public void TaskPluginHost_ScopesResolveFreshScopedServices()
    {
        using var scope1 = TaskPluginHost.CreateScope();
        using var scope2 = TaskPluginHost.CreateScope();
        var env1 = scope1.ServiceProvider.GetRequiredService<EnvService>();
        var env2 = scope2.ServiceProvider.GetRequiredService<EnvService>();
        Assert.NotSame(env1, env2);
        Assert.NotNull(scope1.ServiceProvider.GetRequiredService<NotifyService>());
    }

    // ==================================================================== 白名单与保存流水线

    [Fact]
    public async Task UploadScripts_RejectsLegacyExtensions()
    {
        var service = new UploadService(new ScriptVersionService(_db));
        foreach (var legacy in new[] { "a.js", "b.py", "c.bat" })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                service.UploadScripts(Files(legacy, "console.log(1)"), null));
            Assert.Contains(".cs", ex.Message);
        }
    }

    [Fact]
    public async Task UploadScripts_BlockedCsContent_RejectedBeforeDisk()
    {
        var service = new UploadService(new ScriptVersionService(_db));
        var blocked = """
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
            """;
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.UploadScripts(Files("evil.cs", blocked), null));
        Assert.Contains("未通过保存检查", ex.Message);
        Assert.False(File.Exists(Path.Combine("scripts", "evil.cs")));
    }

    [Fact]
    public async Task UploadScripts_ValidCs_WritesFile()
    {
        var service = new UploadService(new ScriptVersionService(_db));
        var result = await service.UploadScripts(Files("engine_upload_ok.cs", ValidTaskSource), "engine_upload");
        Assert.NotNull(result);
        Assert.True(File.Exists(Path.Combine("scripts", "engine_upload", "engine_upload_ok.cs")));
    }

    [Fact]
    public async Task UpdateScript_BlockedContent_NotSavedWithDiagnostics()
    {
        var path = Path.Combine("scripts", "quantum", "engine_update_blocked.cs");
        Directory.CreateDirectory("scripts/quantum");
        await File.WriteAllTextAsync(path, ValidTaskSource);
        var service = new TaskService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance), new ScriptVersionService(_db));
        var result = await service.UpdateScriptAsync(new SaveQLFile
        {
            path = "engine_update_blocked.cs",
            content = """
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
                """
        });
        Assert.False(result.Success);
        Assert.NotEmpty(result.Blocked);
        Assert.Equal(ValidTaskSource, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UpdateScript_ValidContent_SavedWithSuccessResult()
    {
        var path = Path.Combine("scripts", "quantum", "engine_update_ok.cs");
        Directory.CreateDirectory("scripts/quantum");
        await File.WriteAllTextAsync(path, ValidTaskSource);
        var service = new TaskService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance), new ScriptVersionService(_db));
        var result = await service.UpdateScriptAsync(new SaveQLFile { path = "engine_update_ok.cs", content = ValidTaskSource });
        Assert.True(result.Success);
        Assert.Empty(result.Blocked);
        Assert.Empty(result.Errors);
    }

    // ==================================================================== 测试工具

    private const string ValidTaskSource = """
        using Quantum.Plugins;
        public class ValidTask : IQuantumTask
        {
            public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct) => Task.CompletedTask;
        }
        """;

    /// <summary>用与生产一致的引用集构造 Compilation 后跑门禁（「扫的就是编的」）。</summary>
    private static ScriptSecurityGate.GateResult BuildWrapped(string source)
    {
        var compilation = ScriptBuildService.CreateCompilation(source);
        var tree = compilation.SyntaxTrees[0];
        return ScriptSecurityGate.Scan(tree, compilation);
    }

    private static IFormFileCollection Files(string fileName, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var collection = new FormFileCollection
        {
            new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        };
        return collection;
    }

    private static string WriteScript(string fileName, string content)
    {
        var dir = Path.Combine("scripts", "quantum");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static TaskCommandStep NewStep(string scriptPath, string taskName, int forceEndSeconds)
    {
        return new TaskCommandStep
        {
            Task = new TaskModel { FileName = Path.GetFileName(scriptPath), Name = taskName, Manager = false },
            CreateTime = DateTime.Now,
            ForceEndTime = DateTime.Now.AddSeconds(forceEndSeconds),
            Envs = []
        };
    }

    private static async Task<string> RunStep(string scriptPath, string taskName, int forceEndSeconds)
    {
        var step = NewStep(scriptPath, taskName, forceEndSeconds);
        await step.Run();
        return LatestLog(scriptPath);
    }

    /// <summary>带指令变量执行（模拟 MessageProcess 注入 command/CommunicationUser* 等）。</summary>
    private static async Task<string> RunStepWithVars(string scriptPath, string taskName,
        IEnumerable<(string Name, string Value)> vars, int forceEndSeconds = 60)
    {
        var step = NewStep(scriptPath, taskName, forceEndSeconds);
        step.Envs = vars.Select(v => new EnvModel { Name = v.Name, Value = v.Value }).ToList();
        await step.Run();
        return LatestLog(scriptPath);
    }

    private static string LatestLog(string scriptPath)
    {
        var dir = $"./logs/{TaskExcuteService.LogDirNameFrom(Path.GetFileName(scriptPath))}";
        return Directory.GetFiles(dir).OrderByDescending(n => n).First();
    }

    /// <summary>仓库根：以解决方案文件为锚，避免脚本目录调整（如 scripts/quantum → scripts/demo）连带失效。</summary>
    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Quantum.API", "Quantum.API.sln")))
        {
            current = current.Parent;
        }
        return current!.FullName;
    }

    /// <summary>示例任务目录（随发布产物分发，见 Quantum.Web.csproj 的 Content Include）。</summary>
    private static string FindDemoDir()
    {
        return Path.Combine(FindRepoRoot(), "Quantum.API", "Quantum.Web", "scripts", "demo");
    }
}
