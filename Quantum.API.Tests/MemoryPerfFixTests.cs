using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using Quartz;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 2026-09-19 内存/性能修复批次的回归测试：有界队列溢出留痕、出站克隆与 JSON 深拷贝等价、
/// TaskJob 防同任务并发堆叠特性、会话步骤过期清扫（§1-5）、指令正则缓存（§1-6）。
/// 并入 ConstsState 全局静态串行集合（§5-3）：SweepExpiredSteps 会全局清扫 TaskCommandSteps，须与同类静态态测试串行。
/// </summary>
[Collection("ConstsState")]
public class MemoryPerfFixTests
{
    [Fact]
    public void BoundedConcurrentQueue_OverCapacity_DropsOldestAndNotifies()
    {
        var dropped = new List<int>();
        var queue = new BoundedConcurrentQueue<int>(3) { OnDropped = dropped.Add };

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        queue.Enqueue(4); // 超容量丢最旧

        Assert.Equal(new List<int> { 2, 3, 4 }, queue.Snapshot());
        Assert.Equal(new List<int> { 1 }, dropped);
        Assert.Equal(3, queue.Count);
        Assert.True(queue.TryDequeue(out var head));
        Assert.Equal(2, head);
    }

    [Fact]
    public void BoundedConcurrentQueue_ZeroCapacity_ClampedToOne_NoHang()
    {
        // §1-14：capacity=0 曾使 Enqueue 的驱逐循环永远不满足退出条件而死循环；现钳到 ≥1
        var queue = new BoundedConcurrentQueue<int>(0);
        queue.Enqueue(1);
        queue.Enqueue(2); // 超容量丢最旧，只保留最新
        Assert.Equal(1, queue.Count);
        Assert.Equal(new List<int> { 2 }, queue.Snapshot());
    }

    [Fact]
    public void BoundedConcurrentQueue_ThrowingOnDropped_DoesNotBubble()
    {
        // §1-14：丢弃回调抛异常不应冒泡打断生产者
        var queue = new BoundedConcurrentQueue<int>(1)
        {
            OnDropped = _ => throw new InvalidOperationException("boom")
        };
        var ex = Record.Exception(() =>
        {
            queue.Enqueue(1);
            queue.Enqueue(2);
        });
        Assert.Null(ex);
        Assert.Equal(new List<int> { 2 }, queue.Snapshot());
    }

    [Fact]
    public void MessageProccessDTO_Clone_EqualsJsonDeepClone_AndIsIndependent()
    {
        var source = new MessageProccessDTO
        {
            CommunicationType = default,
            user_id = "u1",
            user_name = "用户",
            group_id = "g1",
            message = "原文",
            message_id = "m1",
            message_text = "配文",
            textToPic = true,
            MessageType = MessageType.图片,
            SessionKey = "session-a",
            TargetTaskId = "task-9",
            Payload = "{\"options\":[]}"
        };

        // 字段全为 string/值类型：手写克隆序列化形态应与 JSON 深拷贝完全一致
        var manual = source.Clone();
        Assert.Equal(
            Newtonsoft.Json.JsonConvert.SerializeObject(source),
            Newtonsoft.Json.JsonConvert.SerializeObject(manual));

        // 克隆体可独立修改（出站 SendMessage 会改 message/TargetTaskId，不得回写源对象）
        manual.message = "改写";
        manual.TargetTaskId = null;
        Assert.Equal("原文", source.message);
        Assert.Equal("task-9", source.TargetTaskId);
    }

    [Fact]
    public void TaskJob_HasDisallowConcurrentExecution()
    {
        // 不检查取消令牌的挂死脚本曾按 cron 频率并发堆叠；JobKey 按任务隔离（JobHelper），
        // 特性只防同一任务并发，不影响不同任务并行
        Assert.True(typeof(TaskJob).IsDefined(typeof(DisallowConcurrentExecutionAttribute), false));
    }

    [Fact]
    public void SweepExpiredSteps_RemovesOnlyForceEndTimeElapsed()
    {
        var expiredKey = Guid.NewGuid().ToString("N");
        var aliveKey = Guid.NewGuid().ToString("N");
        try
        {
            // 过期判据 = ForceEndTime，而非「最后活动 + 固定窗口」：等待窗口内不误清、超窗即回收
            MemoryObjectCache.TaskCommandSteps[expiredKey] = new TaskCommandStep
            {
                ThreadId = expiredKey,
                ForceEndTime = DateTime.Now.AddMinutes(-1)
            };
            MemoryObjectCache.TaskCommandSteps[aliveKey] = new TaskCommandStep
            {
                ThreadId = aliveKey,
                // UpdateTime 很久以前，但 ForceEndTime 仍在未来 → 不应被清扫
                UpdateTime = DateTime.Now.AddHours(-5),
                ForceEndTime = DateTime.Now.AddMinutes(3)
            };

            var removed = MemoryObjectCache.SweepExpiredSteps();

            Assert.True(removed >= 1);
            Assert.False(MemoryObjectCache.TaskCommandSteps.ContainsKey(expiredKey));
            Assert.True(MemoryObjectCache.TaskCommandSteps.ContainsKey(aliveKey));
        }
        finally
        {
            MemoryObjectCache.TaskCommandSteps.TryRemove(expiredKey, out _);
            MemoryObjectCache.TaskCommandSteps.TryRemove(aliveKey, out _);
        }
    }

    [Fact]
    public void CommandReg_IllegalRegex_ReturnsFalseWithoutThrowing()
    {
        var process = new MessageProcess();
        // 未闭合方括号是非法正则：旧实现 new Regex 抛异常冒泡到总 try 丢弃整条消息，
        // 新实现按不匹配处理（记日志后返回 false，由上层循环继续下一条指令）
        Assert.False(process.commandReg("[", "任意消息", true));
        // 同一条非法指令被再次调用仍稳定返回 false（非法结果不入缓存、不毒化）
        Assert.False(process.commandReg("[", "另一条消息", true));
    }

    [Fact]
    public void CommandReg_ValidRegex_MatchesAndReusesCachedPattern()
    {
        var process = new MessageProcess();
        var pattern = @"^\d{3}$";
        Assert.True(process.commandReg(pattern, "123", true));
        Assert.False(process.commandReg(pattern, "12", true));
        // 第二次调用命中缓存路径，结果一致（缓存正确性）
        Assert.True(process.commandReg(pattern, "456", true));
    }

    [Fact]
    public void UploadSlot_GrantsUpToPerMinuteLimitThenDenies()
    {
        var userId = Guid.NewGuid().ToString("N");
        for (var i = 0; i < MemoryObjectCache.AppUploadLimitPerMinute; i++)
        {
            Assert.True(MemoryObjectCache.TryAcquireAppUploadSlot(userId), $"第 {i + 1} 次应放行");
        }
        Assert.False(MemoryObjectCache.TryAcquireAppUploadSlot(userId));
    }

    [Fact]
    public async Task ExecutionGate_SerializesTicketsAndPassesThroughOnTimeout()
    {
        TaskExcuteService.SetGateCapacityForTest(1);
        var originalTimeout = TaskExcuteService.ExecutionSlotWaitTimeout;
        try
        {
            // 容量 1：第一个稳定拿到真实槽位（不触发超时告警）
            TaskExcuteService.ExecutionSlotWaitTimeout = TimeSpan.FromMilliseconds(500);
            string warn1 = null;
            var slot1 = await TaskExcuteService.AcquireExecutionSlotAsync(m => warn1 = m);
            Assert.Null(warn1);

            // 第二个在槽位被占时必须等待而非并发穿透；释放后才在超时前拿到票
            string warn2 = null;
            var pending = TaskExcuteService.AcquireExecutionSlotAsync(m => warn2 = m);
            await Task.Delay(100);
            Assert.False(pending.IsCompleted);
            slot1.Dispose();
            var slot2 = await pending;
            Assert.Null(warn2);

            // 主兜底：槽位再被占满 + 短超时 → 记告警并直通放行（NoopSlot，不占额度）
            TaskExcuteService.ExecutionSlotWaitTimeout = TimeSpan.FromMilliseconds(80);
            string warn3 = null;
            var slot3 = await TaskExcuteService.AcquireExecutionSlotAsync(m => warn3 = m);
            Assert.NotNull(warn3);
            Assert.Contains("放行执行", warn3);

            slot2.Dispose();
            slot3.Dispose();
        }
        finally
        {
            TaskExcuteService.ExecutionSlotWaitTimeout = originalTimeout;
            TaskExcuteService.ResetGateForTest();
        }
    }
}
