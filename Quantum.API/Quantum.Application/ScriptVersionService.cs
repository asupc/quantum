using System.Security.Cryptography;
using System.Text;
using log4net;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 脚本版本管理（2026-09-20 新增，AI 脚本修复 Agent 计划阶段一）：
/// 每次脚本落盘（在线编辑保存 / 上传 / Agent 应用 / 回滚）在 t_script_version 记一条内容快照，
/// 供版本页查看、对比与回滚。当前版本始终以磁盘文件为准，本表只存历史。
/// 版本记录是旁路能力：写库异常绝不能让脚本保存本身失败（调用方一律用 RecordQuietlyAsync）。
/// </summary>
public class ScriptVersionService
{
    private static readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(ScriptVersionService));

    /// <summary>
    /// 每个脚本保留的版本数上限：超出按时间清理最旧的（IsPinned 的版本不清理）。
    /// 现状 32 个脚本最大 67KB，30 版最坏约 1.5MB/文件，longtext/TEXT 均无压力。
    /// </summary>
    public const int KeepPerFile = 30;

    readonly IQuantumDbContext _dbContext;

    public ScriptVersionService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 内容规范化：尾部换行不参与哈希与存储（与 TaskService.GetScriptAsync 的 TrimEnd('\r','\n') 同口径）。
    /// </summary>
    public static string Normalize(string content) => (content ?? string.Empty).TrimEnd('\r', '\n');

    /// <summary>内容哈希（SHA256 大写十六进制），与编译缓存的源码哈希同款口径。</summary>
    public static string HashOf(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(content))));

    /// <summary>
    /// 把「相对 scripts 根」的路径换算为「相对 scripts/quantum 根」的版本键；
    /// 不在 quantum 目录下（如 db/import、scripts/demo）返回 null——那些文件不参与脚本版本管理。
    /// </summary>
    public static string ToQuantumRelative(string pathRelativeToScriptsRoot)
    {
        var normalized = (pathRelativeToScriptsRoot ?? string.Empty).Replace('\\', '/').TrimStart('/');
        const string prefix = "quantum/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : null;
    }

    /// <summary>
    /// 记录版本快照：内容与「该文件最新版本」完全一致则跳过（避免重复保存产生噪音版本）；
    /// 写入后按 <see cref="KeepPerFile"/> 清理该文件最旧的非锁定版本。
    /// </summary>
    /// <param name="force">true 时绕过哈希去重强行落一条（AI 删除留底等「动作本身需要溯源行」的场景）</param>
    /// <returns>新增的版本行；内容未变（且未 force）时返回 null。</returns>
    public async Task<ScriptVersionModel> RecordAsync(string fileName, string content, string source,
        string remark = null, string creator = null, string taskId = null, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new BusinessException("脚本版本文件名不能为空！");
        }
        var normalized = Normalize(content);
        var hash = HashOf(normalized);

        if (!force)
        {
            var latest = await _dbContext.ScriptVersions.AsNoTracking()
                .Where(n => n.FileName == fileName)
                .OrderByDescending(n => n.CreateTime).ThenByDescending(n => n.Id)
                .FirstOrDefaultAsync();
            if (latest != null && string.Equals(latest.Hash, hash, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var version = new ScriptVersionModel
        {
            FileName = fileName,
            Hash = hash,
            Content = normalized,
            Size = Encoding.UTF8.GetByteCount(normalized),
            LineCount = normalized.Length == 0 ? 0 : normalized.Split('\n').Length,
            Source = source,
            Remark = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim(),
            Creator = creator,
            TaskId = taskId,
            CreateTime = DateTime.Now
        };
        _dbContext.ScriptVersions.Add(version);
        await _dbContext.SaveChangesAsync();
        await PruneAsync(fileName);
        return version;
    }

    /// <summary>
    /// 记录版本（吞异常版）：版本历史是旁路能力，写库失败只记日志，绝不影响脚本保存/上传本身。
    /// 返回新增的版本行（内容未变且未 force 时为 null，吞异常时也为 null）——
    /// AI 删除留底等「先留底后动作」的调用方以 null 判定留底失败并拒绝后续删除。
    /// </summary>
    public async Task<ScriptVersionModel> RecordQuietlyAsync(string fileName, string content, string source,
        string remark = null, string creator = null, string taskId = null, bool force = false)
    {
        try
        {
            return await RecordAsync(fileName, content, source, remark, creator, taskId, force);
        }
        catch (Exception e)
        {
            _log.Error($"记录脚本版本失败（{fileName}）", e);
            return null;
        }
    }

    /// <summary>清理超出保留数的旧版本（不清理锁定版本）。</summary>
    private async Task PruneAsync(string fileName)
    {
        var stale = await _dbContext.ScriptVersions
            .Where(n => n.FileName == fileName && !n.IsPinned)
            .OrderByDescending(n => n.CreateTime).ThenByDescending(n => n.Id)
            .Skip(KeepPerFile)
            .ToListAsync();
        if (stale.Count == 0)
        {
            return;
        }
        _dbContext.ScriptVersions.RemoveRange(stale);
        await _dbContext.SaveChangesAsync();
    }

    /// <summary>分页查询版本列表（不含正文）。fileName 为空则查全部文件。</summary>
    public async Task<PageResult<ScriptVersionItem>> GetPageAsync(string fileName, int pageIndex = 1, int pageSize = 20)
    {
        pageIndex = Math.Max(1, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _dbContext.ScriptVersions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            query = query.Where(n => n.FileName == fileName);
        }
        return new PageResult<ScriptVersionItem>
        {
            TotalCount = await query.CountAsync(),
            Data = await query
                .OrderByDescending(n => n.CreateTime).ThenByDescending(n => n.Id)
                .Skip((pageIndex - 1) * pageSize).Take(pageSize)
                .Select(n => new ScriptVersionItem
                {
                    Id = n.Id,
                    FileName = n.FileName,
                    Hash = n.Hash,
                    Size = n.Size,
                    LineCount = n.LineCount,
                    Source = n.Source,
                    Remark = n.Remark,
                    Creator = n.Creator,
                    TaskId = n.TaskId,
                    IsPinned = n.IsPinned,
                    CreateTime = n.CreateTime
                })
                .ToListAsync(),
            Page = pageIndex,
            PageSize = pageSize
        };
    }

    /// <summary>有版本记录的脚本文件清单（版本页左侧筛选）。</summary>
    public async Task<List<ScriptVersionFileItem>> GetFilesAsync()
    {
        return await _dbContext.ScriptVersions.AsNoTracking()
            .GroupBy(n => n.FileName)
            .Select(g => new ScriptVersionFileItem
            {
                FileName = g.Key,
                Count = g.Count(),
                LastTime = g.Max(n => n.CreateTime)
            })
            .OrderByDescending(n => n.LastTime)
            .ToListAsync();
    }

    /// <summary>取版本详情（含正文）。</summary>
    public async Task<ScriptVersionModel> GetAsync(string id)
    {
        var version = await _dbContext.ScriptVersions.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id);
        if (version == null)
        {
            throw new BusinessException("脚本版本不存在，请刷新后重试！");
        }
        return version;
    }

    /// <summary>
    /// 回滚到指定版本：历史内容同样要过保存流水线（门禁规则收紧后旧版可能已不合规），
    /// 不通过即拒绝且不落盘——与在线保存同口径，编辑页凭返回的三类诊断重试。
    /// </summary>
    public async Task<ScriptBuildService.ScriptSaveResult> RollbackAsync(string id, string creator = null)
    {
        var version = await GetAsync(id);
        var scriptFile = SafeFile.Resolve("./scripts/quantum", version.FileName);
        if (scriptFile == null)
        {
            throw new BusinessException("版本记录的文件路径非法，已拒绝回滚！");
        }
        var build = ScriptBuildService.Build(version.Content, scriptFile);
        if (!build.Success)
        {
            return new ScriptBuildService.ScriptSaveResult
            {
                Success = false,
                Blocked = build.Blocked,
                Warnings = build.Warnings,
                Errors = build.Errors
            };
        }
        var dir = Path.GetDirectoryName(scriptFile);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // 与在线保存同款落盘（内容 + 一个换行）
        await File.WriteAllTextAsync(scriptFile, Normalize(version.Content) + Environment.NewLine);
        var shortHash = string.IsNullOrEmpty(version.Hash) ? "?" : version.Hash[..Math.Min(8, version.Hash.Length)];
        await RecordQuietlyAsync(version.FileName, version.Content, ScriptVersionSource.Rollback,
            $"回滚至 {version.CreateTime:yyyy-MM-dd HH:mm:ss} 版本（{shortHash}）", creator, version.TaskId);
        return new ScriptBuildService.ScriptSaveResult { Success = true, Warnings = build.Warnings };
    }

    /// <summary>锁定/解锁版本（锁定版本不被保留数清理）。</summary>
    public async Task<bool> SetPinnedAsync(string id, bool pinned)
    {
        var version = await _dbContext.ScriptVersions.FirstOrDefaultAsync(n => n.Id == id)
            ?? throw new BusinessException("脚本版本不存在，请刷新后重试！");
        version.IsPinned = pinned;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    /// <summary>删除版本记录（只删历史快照，不触碰磁盘上的当前脚本文件）。</summary>
    public async Task<bool> DeleteAsync(List<string> ids)
    {
        if (ids == null || ids.Count == 0)
        {
            return false;
        }
        var versions = await _dbContext.ScriptVersions.Where(n => ids.Contains(n.Id)).ToListAsync();
        if (versions.Count == 0)
        {
            return false;
        }
        _dbContext.ScriptVersions.RemoveRange(versions);
        await _dbContext.SaveChangesAsync();
        return true;
    }
}
