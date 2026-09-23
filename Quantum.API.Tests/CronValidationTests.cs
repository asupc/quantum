using Quartz;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// Quartz 4 升级后的 Cron 校验链：TaskController/JobHelper 均以 CronExpression.TryParse 替代已删除的 IsValidExpression。
/// 这里锁定升级时确认过的行为，防止回退。
/// </summary>
public class CronValidationTests
{
    [Theory]
    [InlineData("0 * * * * ?")]       // 每分钟（前端常用 6 段格式）
    [InlineData("0/15 * * * * ?")]    // 每 15 秒（M1 冒烟用例）
    [InlineData("0 0 6 * * ?")]       // 每天 6 点
    [InlineData("0 0 12 1 * ?")]      // 每月 1 号 12 点
    [InlineData("0 11 11 11 11 ?")]   // 每年 11 月 11 日 11:11
    public void TryParse_AcceptsValidCronExpressions(string cron)
    {
        Assert.True(CronExpression.TryParse(cron, out _));
    }

    [Theory]
    [InlineData("not-a-cron")]
    [InlineData("0 * * *")]           // 段数不足
    [InlineData("")]
    public void TryParse_RejectsInvalidCronExpressions(string cron)
    {
        Assert.False(CronExpression.TryParse(cron, out _));
    }

    [Fact]
    public void TryParse_OutOfRangeSeconds_AcceptedByLooseValidation()
    {
        // 已知行为（锁定）：Quartz 4 的 TryParse 对 "99 99 99 99 99 ?" 这类越界值不做数值范围校验，
        // 宽松通过、调度时才报错。TaskController 的入参防线因此主要挡住"明显乱写"的表达式。
        Assert.True(CronExpression.TryParse("99 99 99 99 99 ?", out _));
    }

    [Fact]
    public void TryParse_OutExpression_IsNullWhenInvalid()
    {
        var ok = CronExpression.TryParse("bad", out var expr);
        Assert.False(ok);
        Assert.Null(expr);
    }
}
