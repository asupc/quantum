using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 日志级别
/// </summary>
public enum LogSeverity
{
    /// <summary>
    /// 信息
    /// </summary>
    Info = 0,

    /// <summary>
    /// 警告
    /// </summary>
    Warn = 1,

    /// <summary>
    /// 错误
    /// </summary>
    Error = 2
}

[Table("t_log")]
public class LogModel : BaseModel
{
    public string Title { get; set; }

    public string Operator { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public string Remark { get; set; }

    public bool Success { get; set; }

    public string UserIP { get; set; }

    public LogType LogType { get; set; }

    /// <summary>
    /// 文件夹名称
    /// </summary>
    public string DirectoryName { get; set; }

    /// <summary>
    /// log 文件路径
    /// </summary>
    public string LogPath { get; set; }

    /// <summary>
    /// 日志级别（Info/Warn/Error），默认 Info
    /// </summary>
    public LogSeverity Severity { get; set; } = LogSeverity.Info;

    /// <summary>
    /// 业务模块（Auth/Task/Env/Communication/System...），用于按模块检索
    /// </summary>
    public string Module { get; set; }

    /// <summary>
    /// 触发请求的路径（HTTP 入口日志填写）
    /// </summary>
    public string RequestPath { get; set; }

    /// <summary>
    /// 异常详情（Error 级别填写，含堆栈）
    /// </summary>
    public string Exception { get; set; }

    /// <summary>
    /// 耗时（毫秒），可测量的动作填写
    /// </summary>
    public long? ElapsedMs { get; set; }
}
