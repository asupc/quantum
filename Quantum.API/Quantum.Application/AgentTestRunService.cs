using System.Collections.Concurrent;
using System.Text;
using log4net;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 一次试运行的结果（落库 + 返回给页面/模型）
/// </summary>
public sealed class AiTestRunResult
{
    public string Status { get; init; } = AiTestStatus.Failed;

    public long DurationMs { get; init; }

    /// <summary>日志尾部</summary>
    public string LogTail { get; init; }

    public bool HasException { get; init; }

    /// <summary>t_log 行 Id（页面走既有日志详情端点看全文）</summary>
    public string LogId { get; init; }

    /// <summary>结论文案</summary>
    public string Message { get; init; }
}

/// <summary>
/// 影子试运行（2026-09-20 新增，AI 脚本修复 Agent 计划阶段四）：
/// 候选源码写到 scripts/quantum/agent-tmp/ 影子文件，然后**完整复用真实执行链**
/// （TaskCommandStep.Run：真实环境变量、真实通知门面、真实并发闸与协作取消），跑完删影子文件。
/// 因此试运行会真实产生副作用（通知/签到/写自定义数据）——这是刻意的：用户要在真实条件下看到结果。
/// 影子文件不在脚本树展示（Traversal 排除 agent-tmp），也不能被任务绑定（ValidateScriptFileName 拒绝）。
/// </summary>
public class AgentTestRunService
{
    private static readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(AgentTestRunService));

    /// <summary>影子目录（相对 scripts/quantum 根；不能以点开头——LogDirNameFrom 用 Split(".")[0] 取日志目录名）</summary>
    public const string StagingDir = "agent-tmp";

    /// <summary>试运行时长的兜底上限（分钟），与任务 WaitTime 取较小值</summary>
    private const int MaxTestRunMinutes = 10;

    /// <summary>最近试运行结果（按内容哈希索引）：propose_fix 据此带上「这份内容试过没有」的结论</summary>
    private static readonly ConcurrentDictionary<string, AiTestRunResult> RecentByHash = new();

    private readonly IQuantumDbContext _db;

    public AgentTestRunService(IQuantumDbContext db)
    {
        _db = db;
    }

    /// <summary>取某份内容最近的试运行结果（没有则 null）。</summary>
    public static AiTestRunResult GetRecent(string contentHash)
        => string.IsNullOrWhiteSpace(contentHash) ? null : RecentByHash.GetValueOrDefault(contentHash);

    /// <summary>
    /// 试运行一份候选源码。fileName 用**真实脚本路径**（决定日志归属、通知会话与任务配置），
    /// 落盘时改写为影子路径执行。
    /// </summary>
    public async Task<AiTestRunResult> RunAsync(string fileName, string content, string notifyMode, CancellationToken ct = default)
    {
        var scriptFile = SafeFile.Resolve("./scripts/quantum", fileName);
        if (scriptFile == null)
        {
            throw new BusinessException("脚本路径非法，已拒绝试运行！");
        }
        var task = await _db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(n => n.FileName == fileName, ct);
        var hash = ScriptVersionService.HashOf(content);
        var stagingRel = $"{StagingDir}/{Path.GetFileNameWithoutExtension(fileName)}-{hash[..Math.Min(8, hash.Length)]}.cs";
        var stagingPath = SafeFile.Resolve("./scripts/quantum", stagingRel);
        if (stagingPath == null)
        {
            throw new BusinessException("试运行路径非法！");
        }
        var stagingDir = Path.GetDirectoryName(stagingPath)!;
        if (!Directory.Exists(stagingDir))
        {
            Directory.CreateDirectory(stagingDir);
        }
        await File.WriteAllTextAsync(stagingPath, ScriptVersionService.Normalize(content) + Environment.NewLine, ct);

        var dateTime = DateTime.Now;
        var result = new AiTestRunResult { Status = AiTestStatus.Failed };
        try
        {
            // 目标任务的内存副本：只改 FileName 指向影子文件，其余（名称/会话/推送/代理）保持真实
            var shadowTask = new TaskModel
            {
                Id = task?.Id ?? string.Empty,
                Name = task?.Name ?? Path.GetFileNameWithoutExtension(fileName),
                FileName = stagingRel,
                SessionName = task?.SessionName,
                EnableProxy = task?.EnableProxy ?? false,
                EnablePush = notifyMode switch
                {
                    AiTestRunNotifyMode.Silent => false,
                    AiTestRunNotifyMode.Force => true,
                    _ => task?.EnablePush ?? true
                },
                Command = task?.Command,
                Remark = task?.Remark
            };
            var envs = (await _db.Envs.AsNoTracking().Where(n => n.Enable).ToListAsync(ct));
            envs.Add(new EnvModel { Name = "IsSystem", Value = "true" });
            var waitMinutes = task?.WaitTime is > 0 and var wait ? Math.Min(wait, MaxTestRunMinutes) : MaxTestRunMinutes;
            var step = new TaskCommandStep
            {
                Task = shadowTask,
                CreateTime = dateTime,
                ForceEndTime = DateTime.Now.AddMinutes(waitMinutes),
                HasChildTask = false,
                UpdateTime = DateTime.Now,
                Envs = envs
            };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await step.Run(ct);
            watch.Stop();

            var logDir = TaskExcuteService.LogDirNameFrom(stagingRel);
            var logFile = Path.Combine(Directory.GetCurrentDirectory(), "logs", logDir,
                $"{dateTime:yyyyMMddHHmmssfff}.log");
            var (tail, hasException) = ReadTail(logFile, 200);
            var status = ct.IsCancellationRequested
                ? AiTestStatus.Timeout
                : hasException ? AiTestStatus.Failed : AiTestStatus.Passed;
            var logId = await RecordLogAsync(shadowTask.Name, fileName, logDir, Path.GetFileName(logFile), status, tail, ct);
            result = new AiTestRunResult
            {
                Status = status,
                DurationMs = watch.ElapsedMilliseconds,
                LogTail = tail,
                HasException = hasException,
                LogId = logId,
                Message = status switch
                {
                    AiTestStatus.Passed => $"试运行通过（耗时 {watch.Elapsed.TotalSeconds:F1} 秒，无异常）",
                    AiTestStatus.Timeout => $"试运行超时中断（{waitMinutes} 分钟上限）",
                    _ => "试运行出现异常，见日志"
                }
            };
        }
        catch (Exception e)
        {
            _log.Error($"试运行失败（{fileName}）", e);
            result = new AiTestRunResult { Status = AiTestStatus.Failed, Message = $"试运行失败：{e.Message}", LogTail = e.ToString() };
        }
        finally
        {
            // 影子文件不留在脚本树里（日志与 t_log 记录保留）
            try
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
                ScriptBuildService.Remove(stagingRel);
            }
            catch (Exception e)
            {
                _log.Warn($"清理影子文件失败（{stagingRel}）：{e.Message}");
            }
            RecentByHash[hash] = result;
            if (RecentByHash.Count > 32)
            {
                foreach (var key in RecentByHash.Keys.Take(RecentByHash.Count - 16).ToList())
                {
                    RecentByHash.TryRemove(key, out _);
                }
            }
        }
        return result;
    }

    /// <summary>启动清理：影子目录里残留的文件（进程崩在试运行中途）。</summary>
    public static void CleanStaging()
    {
        try
        {
            var path = SafeFile.Resolve("./scripts/quantum", StagingDir);
            if (path != null && Directory.Exists(path))
            {
                var files = Directory.GetFiles(path, "*.cs");
                foreach (var file in files)
                {
                    File.Delete(file);
                }
                if (files.Length > 0)
                {
                    _log.Warn($"启动清理：删除 {files.Length} 个试运行影子文件");
                }
            }
        }
        catch (Exception e)
        {
            _log.Error("清理试运行影子目录失败", e);
        }
    }

    private async Task<string> RecordLogAsync(string title, string fileName, string dirName, string logPath,
        string status, string tail, CancellationToken ct)
    {
        var log = new LogModel
        {
            Title = $"{title}（AI 试运行）",
            Operator = "AI",
            CreateTime = DateTime.Now,
            Remark = $"AI 试运行 {fileName}：{(status == AiTestStatus.Passed ? "通过" : status == AiTestStatus.Timeout ? "超时" : "失败")}",
            Success = status == AiTestStatus.Passed,
            LogType = LogType.AI试运行,
            Severity = status == AiTestStatus.Passed ? LogSeverity.Info : LogSeverity.Warn,
            DirectoryName = dirName,
            LogPath = logPath,
            Exception = status == AiTestStatus.Failed ? tail : null
        };
        _db.Logs.Add(log);
        await _db.SaveChangesAsync(ct);
        return log.Id;
    }

    private static (string Tail, bool HasException) ReadTail(string logFile, int lines)
    {
        if (!File.Exists(logFile))
        {
            return ("(未生成日志)", false);
        }
        var all = File.ReadAllLines(logFile);
        var start = Math.Max(0, all.Length - lines);
        var sb = new StringBuilder();
        for (var i = start; i < all.Length; i++)
        {
            sb.AppendLine(all[i]);
        }
        var text = sb.ToString();
        var hasException = text.Contains("exception information", StringComparison.OrdinalIgnoreCase)
            || text.Contains("MessageAsync：", StringComparison.Ordinal)
            || text.Contains("未通过安全门禁", StringComparison.Ordinal)
            || text.Contains("编译失败", StringComparison.Ordinal);
        return (text, hasException);
    }
}
