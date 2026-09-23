using Quantum.Entities.Model;

namespace Quantum.Entities.DTOs;

public class LogQuery : BaseQuery
{
    public LogType? LogType { get; set; }

    /// <summary>
    /// 多类型过滤（与 LogType 任一命中即可）：非管理员访问日志中心时由控制器强制收敛为可见类型
    /// </summary>
    public List<LogType> LogTypes { get; set; }

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 日志级别过滤
    /// </summary>
    public LogSeverity? Severity { get; set; }

    /// <summary>
    /// 业务模块过滤（精确匹配）
    /// </summary>
    public string Module { get; set; }

    /// <summary>
    /// 仅看失败（Success=false）
    /// </summary>
    public bool? FailedOnly { get; set; }
}

/// <summary>
/// 日志统计结果：按级别计数 + 近 N 天按日计数
/// </summary>
public class LogStatisticsDto
{
    public int TotalCount { get; set; }

    public int InfoCount { get; set; }

    public int WarnCount { get; set; }

    public int ErrorCount { get; set; }

    public List<DailyCount> Daily { get; set; } = new();

    public class DailyCount
    {
        public DateTime Date { get; set; }

        public int Count { get; set; }
    }
}
