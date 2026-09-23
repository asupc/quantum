using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 日志服务：业务操作日志/任务日志/系统错误日志的查询、删除、清空与详情读取。
/// </summary>
public class LogsService
{
    private readonly IQuantumDbContext _dbContext;

    public LogsService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 分页查询日志
    /// </summary>
    public async Task<PageResult<LogModel>> GetPageAsync(LogQuery query)
    {
        if (query.EndTime.HasValue)
        {
            query.EndTime = query.EndTime.Value.AddDays(1);
        }

        if (!string.IsNullOrEmpty(query.Key))
        {
            query.Key = query.Key.ToLower();
        }

        var logs = _dbContext.Logs.AsNoTracking().Where(n => (string.IsNullOrEmpty(query.Key) || n.Title.ToLower().Contains(query.Key) || n.Remark.ToLower().Contains(query.Key) || n.Operator.ToLower().Contains(query.Key))
       && (query.StartTime == null || n.CreateTime >= query.StartTime.Value)
       && (query.EndTime == null || n.CreateTime <= query.EndTime.Value)
       && (query.LogType == null || n.LogType == query.LogType.Value)
       && (query.LogTypes == null || query.LogTypes.Count == 0 || query.LogTypes.Contains(n.LogType))
       && (query.Severity == null || n.Severity == query.Severity.Value)
       && (string.IsNullOrEmpty(query.Module) || n.Module == query.Module)
       && (query.FailedOnly == null || !query.FailedOnly.Value || n.Success == false));

        return new PageResult<LogModel>
        {
            Data = await logs.OrderByDescending(n => n.CreateTime).ThenBy(n => n.Id).Skip(query.Skip).Take(query.PageSize).ToListAsync(),
            TotalCount = await logs.CountAsync(),
            Page = query.PageIndex,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 批量删除日志（同时清理对应日志文件）
    /// </summary>
    public async Task<bool> DeleteAsync(string ids)
    {
        var idList = string.IsNullOrEmpty(ids) ? [] : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var logs = await _dbContext.Logs.AsNoTracking().Where(n => idList.Contains(n.Id)).ToListAsync();
        var files = logs.Where(n => !string.IsNullOrEmpty(n.LogPath)).Select(n => n.LogPath);
        foreach (var item in files)
        {
            if (System.IO.File.Exists("./logs/" + item))
            {
                System.IO.File.Delete("./logs/" + item);
            }
        }
        _dbContext.Logs.RemoveRange(logs);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 清空全部日志
    /// </summary>
    public async Task<bool> ClearAsync()
    {
        try
        {
            await _dbContext.Logs.ExecuteDeleteAsync();
            // 只删 ./logs 的内容、保留根目录本身：Docker 部署中该目录是挂载点，
            // 删除挂载点会报 EBUSY(Device or resource busy)；当前日志文件可能被
            // log4net 占用（Windows 下不可删），故逐项尽力删除、失败跳过
            if (Directory.Exists("./logs"))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries("./logs"))
                {
                    try
                    {
                        if (Directory.Exists(entry))
                        {
                            Directory.Delete(entry, true);
                        }
                        else
                        {
                            File.Delete(entry);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception e)
        {
            throw new BusinessException($"清理日志失败：{e.Message}");
        }
        return true;
    }

    /// <summary>
    /// 日志统计：按级别计数 + 近 N 天按日计数
    /// </summary>
    public async Task<LogStatisticsDto> StatisticsAsync(int days = 7)
    {
        days = Math.Max(1, days);
        var since = DateTime.Now.Date.AddDays(-(days - 1));
        // §2-6：按 (日桶, 级别) 的计数下推到数据库聚合，避免把整个日期区间的日志行拉进内存。
        // CreateTime 存的是本地墙钟（DateTime.Now），日桶取文本日期与旧「CreateTime.Date」口径一致，结果不变。
        var isSqlite = SystemConfigHelper.GetSetting().DBType.Equals("SQLite", StringComparison.OrdinalIgnoreCase);
        var dayBucket = isSqlite ? "strftime('%Y-%m-%d', CreateTime)" : "DATE_FORMAT(CreateTime, '%Y-%m-%d')";
        var sql = $"SELECT {dayBucket} AS Day, Severity AS Severity, COUNT(*) AS Cnt FROM t_log WHERE CreateTime >= {{0}} GROUP BY Day, Severity";
        var rows = await _dbContext.Database.SqlQueryRaw<LogStatRow>(sql, since).ToListAsync();

        var result = new LogStatisticsDto();
        var daily = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result.TotalCount += row.Cnt;
            switch (row.Severity)
            {
                case (int)LogSeverity.Info: result.InfoCount += row.Cnt; break;
                case (int)LogSeverity.Warn: result.WarnCount += row.Cnt; break;
                case (int)LogSeverity.Error: result.ErrorCount += row.Cnt; break;
            }
            if (!string.IsNullOrEmpty(row.Day))
            {
                daily[row.Day] = daily.TryGetValue(row.Day, out var acc) ? acc + row.Cnt : row.Cnt;
            }
        }
        result.Daily = daily
            .Select(kv => new LogStatisticsDto.DailyCount
            {
                Date = DateTime.ParseExact(kv.Key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Count = kv.Value
            })
            .OrderBy(n => n.Date)
            .ToList();
        return result;
    }

    /// <summary>SqlQueryRaw 结果行：日桶文本 + 级别 + 计数（列名与属性名大小写不敏感映射）。</summary>
    private sealed class LogStatRow
    {
        public string Day { get; set; }
        public int Severity { get; set; }
        public int Cnt { get; set; }
    }

    /// <summary>
    /// 读取日志元数据（详情权限判定用：非管理员只能查看非受限类型的日志详情）
    /// </summary>
    public Task<LogModel> GetMetaAsync(string id)
    {
        return _dbContext.Logs.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id);
    }

    /// <summary>
    /// 获取详细日志内容
    /// </summary>
    public async Task<string> GetDetailsAsync(string id)
    {
        var log = await _dbContext.Logs.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id);
        if (log == null || string.IsNullOrEmpty(log.LogPath))
        {
            throw new BusinessException("日志文件不存在，任务未执行完或文件丢失！");
        }
        // 读取侧兜底：DirectoryName/LogPath 均为落库值，存量脏数据可能含路径分隔符/穿越片段，
        // 经 SafeFile 限定在 ./logs 根内，杜绝借日志详情端点读任意文件。
        // 执行中日志字典（TaskExcuteService.Logs）以相对路径为键，查找仍用拼接后的相对键
        var relativePath = $"./logs/{log.DirectoryName}/{log.LogPath}";
        var logPath = SafeFile.Resolve("./logs", $"{log.DirectoryName}/{log.LogPath}");
        if (logPath == null)
        {
            throw new BusinessException("日志路径非法！");
        }
        try
        {
            // §1-9 读取侧：执行中（Logs 命中该相对路径）优先取环形缓冲尾览快照（线程安全），
            // 未命中说明任务已结束 → 读已逐行增量落盘的完整文件。
            if (TaskExcuteService.Logs.TryGetValue(relativePath, out var live))
            {
                return live.Snapshot() + "\r当前任务正在执行中。。。";
            }
            if (System.IO.File.Exists(logPath))
            {
                using (StreamReader streamReader = new(logPath))
                {
                    return await streamReader.ReadToEndAsync();
                }
            }
        }
        catch (Exception)
        {
            throw new BusinessException("读取日志数据错误！");
        }
        throw new BusinessException("日志文件不存在，任务未执行完或文件丢失！");
    }
}
