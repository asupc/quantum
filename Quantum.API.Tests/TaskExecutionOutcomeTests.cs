using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 一期 G1「执行结果真实性」回归：脚本没跑起来 / 跑挂 / 被取消 / 跑成功 四类终态必须有确定结论，
/// 且 t_log 行的 Success 与终态一致——修复前这四类全部被记成成功（日志创建即 Success=true）。
/// 与 ConstsState 同 Collection：本类会写 scripts/quantum 与 logs/ 下的真实文件并触碰执行并发闸静态态。
/// </summary>
[Collection("ConstsState")]
public class TaskExecutionOutcomeTests : IDisposable
{
    private readonly List<string> _scriptFiles = [];

    public TaskExecutionOutcomeTests()
    {
        Directory.CreateDirectory("scripts/quantum");
        // 环境变量缓存置空：脱敏用例自行 Set，避免回源开库
        CacheManager.Set(new List<EnvModel>());
    }

    public void Dispose()
    {
        foreach (var file in _scriptFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 用例失败时清理不参与断言
            }
        }
        CacheManager.Set(new List<EnvModel>());
    }

    private async Task<TaskCommandStep> RunScriptAsync(string fileName, string source,
        int forceEndSeconds = 60, CancellationToken ct = default)
    {
        var scriptPath = Path.Combine("scripts", "quantum", fileName);
        _scriptFiles.Add(scriptPath);
        await File.WriteAllTextAsync(scriptPath, source);
        // 预热编译缓存：冷进程首次编译可达秒级，会吃掉 ForceEndTime 窗口导致误入「已过期」分支
        var warm = ScriptBuildService.Build(source, scriptPath);
        Assert.True(warm.Success || warm.Blocked.Count > 0 || warm.Errors.Count > 0);

        return new TaskCommandStep
        {
            Task = new TaskModel { FileName = fileName, Name = Path.GetFileNameWithoutExtension(fileName), Manager = false },
            CreateTime = DateTime.Now,
            ForceEndTime = DateTime.Now.AddSeconds(forceEndSeconds),
            Envs = []
        };
    }

    private static string ReadLatestLog(string fileName)
    {
        var dir = $"./logs/{TaskExcuteService.LogDirNameFrom(fileName)}";
        return File.ReadAllText(Directory.GetFiles(dir).OrderByDescending(n => n).First());
    }

    // ---------------------------------------------------------------- Rejected：脚本根本没跑起来

    [Fact]
    public async Task Run_ScriptFileMissing_ReturnsRejected_NotSuccess()
    {
        var step = new TaskCommandStep
        {
            Task = new TaskModel { FileName = "g1out_definitely_absent.cs", Name = "缺文件", Manager = false },
            CreateTime = DateTime.Now,
            ForceEndTime = DateTime.Now.AddMinutes(1),
            Envs = []
        };

        var result = await step.Run();

        Assert.Equal(TaskExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal(TaskFailureCode.ScriptMissing, result.FailureCode);
        Assert.False(result.IsSuccess);
        Assert.Contains("不存在", result.SafeSummary);
    }

    [Fact]
    public async Task Run_TraversalScriptPath_ReturnsRejectedWithInvalidPath()
    {
        var step = new TaskCommandStep
        {
            Task = new TaskModel { FileName = "../../../Windows/win.ini", Name = "穿越", Manager = false },
            CreateTime = DateTime.Now,
            ForceEndTime = DateTime.Now.AddMinutes(1),
            Envs = []
        };

        var result = await step.Run();

        Assert.Equal(TaskExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal(TaskFailureCode.InvalidScriptPath, result.FailureCode);
    }

    [Fact]
    public async Task Run_UnsupportedExtension_ReturnsRejected()
    {
        var step = await RunScriptAsync("g1out_legacy.js", "console.log(1)");

        var result = await step.Run();

        Assert.Equal(TaskExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal(TaskFailureCode.UnsupportedScriptExtension, result.FailureCode);
    }

    [Fact]
    public async Task Run_GateBlockedScript_ReturnsRejectedWithGateCode()
    {
        var step = await RunScriptAsync("g1out_gate.cs", """
            using Quantum.Plugins;
            public class G1OutGateTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    System.IO.File.WriteAllText("pwn.txt", ctx.Name);
                    return Task.CompletedTask;
                }
            }
            """);

        var result = await step.Run();

        Assert.Equal(TaskExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal(TaskFailureCode.GateBlocked, result.FailureCode);
        Assert.Contains("门禁", result.SafeSummary);
    }

    [Fact]
    public async Task Run_CompilationFailedScript_ReturnsRejectedWithCompileCode()
    {
        var step = await RunScriptAsync("g1out_compile.cs", """
            using Quantum.Plugins;
            public class G1OutCompileTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    return Task.CompletedTask   // 少了分号
                }
            }
            """);

        var result = await step.Run();

        Assert.Equal(TaskExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal(TaskFailureCode.CompileFailed, result.FailureCode);
    }

    /// <summary>早退分支同样要从实时日志字典摘键：修复前直接 return 会永久残留死键。</summary>
    [Fact]
    public async Task Run_EarlyExitDoesNotLeakLiveLogKey()
    {
        var createTime = DateTime.Now;
        var step = new TaskCommandStep
        {
            Task = new TaskModel { FileName = "g1out_leakcheck_absent.cs", Name = "缺文件", Manager = false },
            CreateTime = createTime,
            ForceEndTime = DateTime.Now.AddMinutes(1),
            Envs = []
        };

        await step.Run();

        var key = $"./logs/{TaskExcuteService.LogDirNameFrom(step.Task.FileName)}/{createTime:yyyyMMddHHmmssfff}.log";
        Assert.False(TaskExcuteService.Logs.ContainsKey(key));
    }

    // ---------------------------------------------------------------- Failed：脚本抛异常

    [Fact]
    public async Task Run_ScriptThrows_ReturnsFailedAndKeepsStackOutOfSummary()
    {
        var step = await RunScriptAsync("g1out_throw.cs", """
            using Quantum.Plugins;
            public class G1OutThrowTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    throw new InvalidOperationException("boom 连接失败 password=SuperSecret123");
                }
            }
            """);

        var result = await step.Run();

        Assert.Equal(TaskExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(TaskFailureCode.ScriptException, result.FailureCode);
        Assert.True(result.IsFinalFailure);
        Assert.Contains("InvalidOperationException", result.SafeSummary);
        // 堆栈留在日志文件里，入库摘要不带
        Assert.DoesNotContain("at G1Out", result.SafeSummary);
        Assert.Contains("at G1Out", ReadLatestLog("g1out_throw.cs"));
    }

    // ---------------------------------------------------------------- Canceled：取消不得记成成功

    [Fact]
    public async Task Run_CancellationHonored_ReturnsCanceled()
    {
        var step = await RunScriptAsync("g1out_cancel.cs", """
            using Quantum.Plugins;
            public class G1OutCancelTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(100, ct).ContinueWith(t => { });
                    }
                }
            }
            """, forceEndSeconds: 2);

        var result = await step.Run();

        Assert.Equal(TaskExecutionOutcome.Canceled, result.Outcome);
        Assert.Equal(TaskFailureCode.CanceledByForceEndTime, result.FailureCode);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsFinalFailure);
    }

    /// <summary>脚本无视取消信号跑到底、正常返回：终态仍是 Canceled，不能因为「没抛异常」记成成功。</summary>
    [Fact]
    public async Task Run_CancellationIgnoredButReturnedStillCanceled()
    {
        var step = await RunScriptAsync("g1out_ignore.cs", """
            using Quantum.Plugins;
            public class G1OutIgnoreTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    await Task.Delay(2500);
                }
            }
            """, forceEndSeconds: 2);

        var result = await step.Run();

        Assert.Equal(TaskExecutionOutcome.Canceled, result.Outcome);
        Assert.Equal(TaskFailureCode.CanceledByForceEndTime, result.FailureCode);
    }

    [Fact]
    public async Task Run_ShutdownCancellationToken_ReturnsCanceledByShutdown()
    {
        var step = await RunScriptAsync("g1out_shutdown.cs", """
            using Quantum.Plugins;
            public class G1OutShutdownTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    try { await Task.Delay(5000, ct); }
                    catch (OperationCanceledException) { throw; }
                }
            }
            """);
        using var cts = new CancellationTokenSource(300);

        var result = await step.Run(cts.Token);

        Assert.Equal(TaskExecutionOutcome.Canceled, result.Outcome);
        Assert.Equal(TaskFailureCode.CanceledByShutdown, result.FailureCode);
    }

    // ---------------------------------------------------------------- Succeeded

    [Fact]
    public async Task Run_CleanScript_ReturnsSucceededAndFooterOutcome()
    {
        var step = await RunScriptAsync("g1out_ok.cs", """
            using Quantum.Plugins;
            public class G1OutOkTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    ctx.Log("一切正常");
                    return Task.CompletedTask;
                }
            }
            """);

        var result = await step.Run();

        Assert.True(result.IsSuccess);
        Assert.Equal(TaskExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(TaskFailureCode.None, result.FailureCode);
        Assert.True(result.HasLogLocation);
        Assert.Equal(LogSeverity.Info, result.Severity);
        var footer = ReadLatestLog("g1out_ok.cs");
        Assert.Contains("执行结果：成功", footer);
    }

    // ---------------------------------------------------------------- t_log 与终态一致（R-01 前置）

    [Fact]
    public void ApplyToLog_FailedOutcome_MarksLogUnsuccessfulWithCode()
    {
        var log = new LogModel
        {
            LogType = LogType.任务日志,
            Remark = "执行脚本任务",
            Success = true
        };
        var result = TaskExecutionResult.Failed(TaskFailureCode.ScriptException, "脚本抛异常",
            DateTime.UtcNow.AddSeconds(-3), DateTime.UtcNow, "dir", "1.log");

        result.ApplyToLog(log);

        Assert.False(log.Success);
        Assert.Equal(LogSeverity.Error, log.Severity);
        Assert.Equal("Task", log.Module);
        Assert.Contains("Failed/ScriptException", log.Remark);
        Assert.NotNull(log.ElapsedMs);
        Assert.Equal("1.log", log.LogPath);
    }

    [Fact]
    public void ApplyToLog_SuccessKeepsRemarkAndInfoSeverity()
    {
        var log = new LogModel { Remark = "执行脚本任务", Success = false };
        var result = TaskExecutionResult.Succeeded(DateTime.UtcNow, DateTime.UtcNow, "dir", "2.log");

        result.ApplyToLog(log);

        Assert.True(log.Success);
        Assert.Equal(LogSeverity.Info, log.Severity);
        Assert.Equal("执行脚本任务", log.Remark);
    }

    // ---------------------------------------------------------------- 摘要脱敏（§3.1.3）

    [Fact]
    public void SanitizeSummary_TruncatesAndStripsControlChars()
    {
        var raw = "第一行\r\n第二行" + new string('x', 2000);

        var clean = TaskExecutionResult.SanitizeSummary(raw);

        Assert.Equal(TaskExecutionResult.MaxSummaryLength, clean.Length);
        Assert.DoesNotContain("\r", clean);
        Assert.DoesNotContain("\n", clean);
    }

    [Fact]
    public void SanitizeSummary_MasksSecretsAndEnvValues()
    {
        CacheManager.Set(new List<EnvModel>
        {
            new() { Name = "MYSQL_PWD", Value = "Tr0ub4dor&3xyz", Enable = true }
        });

        var clean = TaskExecutionResult.SanitizeSummary(
            "连接失败 Tr0ub4dor&3xyz，password=Admin@123，Authorization: Bearer eyJhbGciOi.some.token");

        Assert.DoesNotContain("Tr0ub4dor&3xyz", clean);
        Assert.DoesNotContain("Admin@123", clean);
        Assert.DoesNotContain("eyJhbGciOi", clean);
        Assert.Contains("***", clean);
    }

    [Fact]
    public void SanitizeSummary_DisabledEnvValueIsNotRedacted()
    {
        CacheManager.Set(new List<EnvModel>
        {
            new() { Name = "OFF_SITE", Value = "offsite-value-1", Enable = false }
        });

        Assert.Contains("offsite-value-1",
            TaskExecutionResult.SanitizeSummary("见 offsite-value-1"));
    }

    [Fact]
    public void InterruptedOutcome_IsNotSuccessAndNotFinalFailure()
    {
        var result = TaskExecutionResult.Interrupted("进程重启，结果不可知",
            DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-4));

        Assert.Equal(TaskExecutionOutcome.Interrupted, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsFinalFailure);
        Assert.Equal("中断/未知", result.OutcomeLabel);
    }
}
