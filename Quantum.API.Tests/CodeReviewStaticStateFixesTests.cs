using System.Diagnostics;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 触及进程级静态状态的修复回归（与其它静态态用例同 Collection 串行）：
/// B10 登录退避按「IP|账号」记账（他人登录成功不清目标账号的爆破退避）、
/// B5 消息泵单条发送异常隔离、H6 挂死脚本按 ForceEndTime 强制终止。
/// </summary>
[Collection("ConstsState")]
public class CodeReviewStaticStateFixesTests
{
    // ---------- B10：登录退避按账号维度记账 ----------

    [Fact]
    public void LoginBackoff_OtherAccountSuccess_DoesNotClearTargetAccountFailures()
    {
        ResetLimitState();
        MemoryObjectCache.AddLoginFailed("1.1.1.1", "admin");
        MemoryObjectCache.AddLoginFailed("1.1.1.1", "admin");
        MemoryObjectCache.IpLimit["1.1.1.1"] = DateTime.Now;

        // 攻击者持有的普通账号在同一 IP 登录成功：只清自己的失败记录，管理员口令的退避保持
        MemoryObjectCache.ResetLoginLimit("1.1.1.1", "attacker");
        Assert.Equal(2, MemoryObjectCache.GetLoginFailed("1.1.1.1"));
        Assert.True(MemoryObjectCache.IpLimit.ContainsKey("1.1.1.1"));

        // 目标账号自身登录成功才清零并解除 IP 限流
        MemoryObjectCache.ResetLoginLimit("1.1.1.1", "admin");
        Assert.Equal(0, MemoryObjectCache.GetLoginFailed("1.1.1.1"));
        Assert.False(MemoryObjectCache.IpLimit.ContainsKey("1.1.1.1"));
    }

    [Fact]
    public void LoginBackoff_FailuresAcrossAccountsOnSameIp_AccumulateForIpLevelDelay()
    {
        ResetLimitState();
        MemoryObjectCache.AddLoginFailed("2.2.2.2", "alice");
        MemoryObjectCache.AddLoginFailed("2.2.2.2", "bob");
        MemoryObjectCache.AddLoginFailed("2.2.2.2", MemoryObjectCache.AppKeyAuthAccount);

        // IP 维度取总和：同 IP 多账号爆破同样面临线性增长的退避
        Assert.Equal(3, MemoryObjectCache.GetLoginFailed("2.2.2.2"));
        Assert.Equal(0, MemoryObjectCache.GetLoginFailed("3.3.3.3"));
    }

    private static void ResetLimitState()
    {
        MemoryObjectCache.LoginFailedCounts = new();
        MemoryObjectCache.IpLimit = new();
    }

    // ---------- B5：消息泵单条发送异常隔离 ----------

    [Fact]
    public async Task MessagePump_SenderThrows_ExceptionIsSwallowedNotPropagated()
    {
        var original = SendMessageHelper.Sender;
        try
        {
            var delivered = false;
            SendMessageHelper.Sender = _ => throw new InvalidOperationException("db down");
            // 修复前该异常会逃出唯一的泵循环，此后全部下行消息静默丢失
            await SendMessageHelper.SendSafeAsync(new MessageProccessDTO());

            SendMessageHelper.Sender = _ => { delivered = true; return Task.CompletedTask; };
            await SendMessageHelper.SendSafeAsync(new MessageProccessDTO());
            Assert.True(delivered);
        }
        finally
        {
            SendMessageHelper.Sender = original;
        }
    }

    // ---------- H6：ForceEndTime 协作取消（2026-09-16 执行引擎改造后语义） ----------

    [Fact]
    public async Task TaskRun_CtRespectingLoop_IsCancelledAtForceEndTime()
    {
        // 协作式取消：循环检查 ct、延迟携带 token、捕获 OCE 后收尾——到期即退（不再有进程强杀）
        Directory.CreateDirectory("scripts/quantum");
        var scriptPath = Path.Combine("scripts", "quantum", "crfix_cancel_test.cs");
        await File.WriteAllTextAsync(scriptPath, """
            using Quantum.Plugins;
            public class CrfixCancelTask : IQuantumTask
            {
                public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    ctx.Log("循环开始");
                    while (!ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(200, ct); }
                        catch (OperationCanceledException) { break; }
                    }
                    ctx.Log("收到取消信号退出");
                }
            }
            """);
        try
        {
            // 预热编译缓存：冷进程首次编译可达秒级，会吃掉 1 秒的 ForceEndTime 窗口导致误入「已过期」分支
            var warm = ScriptBuildService.Build(await File.ReadAllTextAsync(scriptPath), scriptPath);
            Assert.True(warm.Success);

            var step = new TaskCommandStep
            {
                Task = new TaskModel { FileName = "crfix_cancel_test.cs", Name = "取消测试", Manager = false },
                CreateTime = DateTime.Now,
                ForceEndTime = DateTime.Now.AddSeconds(2),
                Envs = []
            };
            var stopwatch = Stopwatch.StartNew();
            await step.Run();
            stopwatch.Stop();

            // Run 把执行日志写盘：从日志目录读最新文件断言协作取消生效
            var logDir = $"./logs/{TaskExcuteService.LogDirNameFrom("crfix_cancel_test.cs")}";
            var logText = File.ReadAllText(Directory.GetFiles(logDir).OrderByDescending(n => n).First());
            Assert.Contains("已发出协作取消信号", logText);
            Assert.Contains("收到取消信号退出", logText);
            Assert.Contains("取消信号后自行结束", logText);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"协作取消应在 ForceEndTime 附近生效，实际耗时 {stopwatch.Elapsed}");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }
}
