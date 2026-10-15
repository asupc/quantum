using System.Reflection;
using Quantum.Web.Middleware;

namespace Quantum.API.Tests;

/// <summary>
/// 对抗性加固回归（2026-10-11）：/ws/app 无凭证升级后的回收。
///
/// 现场实测：不带 token 的 upgrade 会拿到 101，且该连接不入注册表（心跳巡检只扫注册表），
/// 单条无凭证连接常驻 357s+、43 条并发无凭证连接无一被回收——「等首帧 auth」被做成了无限期等待。
/// 修复后未认证连接的等待预算是「升级时刻 + PreAuthTimeout」的绝对期限，不随收帧续期。
/// </summary>
public class AppWebSocketPreAuthTests
{
    [Fact]
    public void ReceiveWaitBudget_Unauthenticated_ShrinksToDeadline()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var deadline = now.AddSeconds(30);

        var budget = Invoke(authenticated: false, deadline, now);

        Assert.Equal(TimeSpan.FromSeconds(30), budget);
    }

    [Fact]
    public void ReceiveWaitBudget_Unauthenticated_PastDeadline_IsZero()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        // 循环发 ping 也续不了期：期限是绝对的，过期即 0（调用方据此断连）
        var budget = Invoke(authenticated: false, now.AddSeconds(-1), now);

        Assert.Equal(TimeSpan.Zero, budget);
    }

    [Fact]
    public void ReceiveWaitBudget_Authenticated_UsesNormalTimeout()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        // 已认证连接不受首帧期限影响（DateTime.MaxValue 期限），仍用常规 10 分钟预算
        var budget = Invoke(authenticated: true, DateTime.MaxValue, now);

        Assert.Equal(TimeSpan.FromMinutes(10), budget);
    }

    [Fact]
    public void PreAuthTimeout_IsBoundedAndShorterThanReceiveTimeout()
    {
        // 首帧期限必须明显短于常规收帧超时，否则「有界」等于没界
        var preAuth = PreAuthTimeout();
        Assert.True(preAuth > TimeSpan.Zero);
        Assert.True(preAuth <= TimeSpan.FromMinutes(1));
        Assert.True(preAuth < TimeSpan.FromMinutes(10));
    }

    private static TimeSpan PreAuthTimeout()
        => (TimeSpan)typeof(AppWebSocketMiddleware)
            .GetProperty("PreAuthTimeout", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static TimeSpan Invoke(bool authenticated, DateTime deadline, DateTime now)
        => (TimeSpan)typeof(AppWebSocketMiddleware)
            .GetMethod("ReceiveWaitBudget", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [authenticated, deadline, now])!;
}
