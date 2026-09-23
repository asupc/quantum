using log4net;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 业务操作日志：进程内缓冲 + 后台线程批量落库。
/// 每批使用独立短生命周期 DbContext（原实现持有根容器解析的长寿命上下文，
/// 变更跟踪器随日志无限增长）；队列有界（丢弃最旧），单批失败重试一次后丢弃，
/// 防止 DB 长期不可用时无限膨胀/无限重入队。
/// </summary>
public static class LogServiceHelper
{
    private static readonly ILog Log = LogManager.GetLogger("NETCoreRepository", typeof(LogServiceHelper));

    private const int MaxQueueLength = 50_000;

    public static BoundedConcurrentQueue<LogModel> Logs { get; set; }

    static LogServiceHelper()
    {
        Logs = new BoundedConcurrentQueue<LogModel>(MaxQueueLength);
    }

    /// <summary>
    /// 入队一条日志（保留给既有调用点；新代码建议用 Info/Warn/Error 帮助方法）
    /// </summary>
    public static void Enqueue(LogModel log)
    {
        Logs.Enqueue(log);
    }

    /// <summary>记录 Info 级别系统日志（logType 可选：AI 助手等业务类型，默认系统日志）</summary>
    public static void Info(string title, string remark, string module = null, string operatorName = "System",
        LogType logType = LogType.系统日志)
    {
        Logs.Enqueue(BuildLog(LogSeverity.Info, title, remark, module, operatorName, logType));
    }

    /// <summary>记录 Warn 级别系统日志</summary>
    public static void Warn(string title, string remark, string module = null, string operatorName = "System",
        LogType logType = LogType.系统日志)
    {
        Logs.Enqueue(BuildLog(LogSeverity.Warn, title, remark, module, operatorName, logType));
    }

    /// <summary>记录 Error 级别系统日志（可带异常详情与请求路径）</summary>
    public static void Error(string title, string remark, string module = null, string exception = null, string requestPath = null,
        string operatorName = "System", LogType logType = LogType.系统日志)
    {
        var log = BuildLog(LogSeverity.Error, title, remark, module, operatorName, logType);
        log.Success = false;
        log.Exception = exception;
        log.RequestPath = requestPath;
        Logs.Enqueue(log);
    }

    private static LogModel BuildLog(LogSeverity severity, string title, string remark, string module,
        string operatorName = "System", LogType logType = LogType.系统日志)
    {
        return new LogModel
        {
            CreateTime = DateTime.Now,
            LogType = logType,
            Operator = operatorName,
            Remark = remark,
            Success = severity != LogSeverity.Error,
            Severity = severity,
            Module = module,
            Title = title
        };
    }

    public static void AddLogs()
    {
        Thread addLogThread = new(() =>
        {
            while (true)
            {
                Thread.Sleep(3000);
                var datas = new List<LogModel>();
                while (Logs.TryDequeue(out LogModel data))
                {
                    datas.Add(data);
                }
                if (datas.Count == 0)
                {
                    continue;
                }
                SaveInBatches(datas);
            }
        });
        addLogThread.IsBackground = true;
        addLogThread.Start();
    }

    /// <summary>
    /// 分块落库（§1-13）：排空后的整批按 500/块切分，各块独立 DbContext + SaveChanges，
    /// 单块失败重试一次后仅丢弃该块并留痕——避免一次巨型 SaveChanges，也避免个别坏块连带丢掉整队列。
    /// </summary>
    private static void SaveInBatches(List<LogModel> datas)
    {
        const int batchSize = 500;
        for (var i = 0; i < datas.Count; i += batchSize)
        {
            var batch = datas.GetRange(i, Math.Min(batchSize, datas.Count - i));
            if (TrySave(batch) || TrySave(batch))
            {
                continue;
            }
            Log.Error($"日志批量落库失败已丢弃（{batch.Count}条）：最后一条错误见上一条日志。");
        }
    }

    private static bool TrySave(List<LogModel> datas)
    {
        try
        {
            // 接口 IQuantumDbContext 未继承 IDisposable，落盘统一转 DbContext using
            using DbContext dbContext = (DbContext)QuantumDbContextFactory.Create();
            dbContext.Set<LogModel>().AddRange(datas);
            dbContext.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"日志批量落库失败（{datas.Count}条），稍后重试：{ex.Message}");
            return false;
        }
    }
}
