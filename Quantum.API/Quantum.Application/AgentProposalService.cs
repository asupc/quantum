using log4net;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 修复提案（2026-09-20 新增，AI 脚本修复 Agent 计划阶段四）：
/// Agent 的唯一产出形态——提案只入库不落盘，用户点「应用」时服务端**重新复校**（基线哈希 + 门禁 + 编译）后才写文件并记版本。
/// 基线校验的意义：Agent 生成提案后用户可能又手改过脚本，直接落盘会静默覆盖人工改动。
/// </summary>
public class AgentProposalService
{
    private static readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(AgentProposalService));

    readonly IQuantumDbContext _db;
    readonly ScriptVersionService _versionService;
    readonly AgentTestRunService _testRunService;

    public AgentProposalService(IQuantumDbContext db, ScriptVersionService versionService, AgentTestRunService testRunService)
    {
        _db = db;
        _versionService = versionService;
        _testRunService = testRunService;
    }

    /// <summary>
    /// 生成提案（不落盘）：记录基线哈希、候选源码、生成时的三类诊断；
    /// 若该份内容刚试运行过（内容哈希命中），顺带带上试运行结论。
    /// </summary>
    public async Task<AiProposalModel> CreateAsync(string conversationId, string messageId, string fileName, string source,
        string summary, string diagnostics, CancellationToken ct = default)
    {
        var scriptFile = SafeFile.Resolve("./scripts/quantum", fileName);
        if (scriptFile == null)
        {
            throw new BusinessException("脚本路径非法，已拒绝生成提案！");
        }
        var currentContent = File.Exists(scriptFile)
            ? ScriptVersionService.Normalize(await File.ReadAllTextAsync(scriptFile, ct))
            : string.Empty;
        var recent = AgentTestRunService.GetRecent(ScriptVersionService.HashOf(source));
        var proposal = new AiProposalModel
        {
            ConversationId = conversationId,
            MessageId = messageId,
            FileName = fileName,
            BaseHash = ScriptVersionService.HashOf(currentContent),
            NewContent = ScriptVersionService.Normalize(source),
            NewHash = ScriptVersionService.HashOf(source),
            Summary = Truncate(summary, 2000),
            Diagnostics = diagnostics,
            TestStatus = recent?.Status ?? AiTestStatus.None,
            TestRunLogId = recent?.LogId,
            Status = AiProposalStatus.Pending,
            CreateTime = DateTime.Now
        };
        _db.AiProposals.Add(proposal);
        await _db.SaveChangesAsync(ct);
        return proposal;
    }

    public async Task<AiProposalModel> GetAsync(string id)
    {
        var proposal = await _db.AiProposals.FirstOrDefaultAsync(n => n.Id == id);
        if (proposal == null)
        {
            throw new BusinessException("提案不存在，请刷新后重试！");
        }
        return proposal;
    }

    /// <summary>取提案的差异对照：基线内容（当前磁盘为准）+ 候选内容。</summary>
    public async Task<object> GetContentAsync(string id, CancellationToken ct = default)
    {
        var proposal = await GetAsync(id);
        var scriptFile = SafeFile.Resolve("./scripts/quantum", proposal.FileName);
        var baseContent = scriptFile != null && File.Exists(scriptFile)
            ? ScriptVersionService.Normalize(await File.ReadAllTextAsync(scriptFile, ct))
            : string.Empty;
        return new
        {
            proposal.FileName,
            proposal.Summary,
            proposal.Status,
            proposal.TestStatus,
            proposal.NewContent,
            BaseContent = baseContent
        };
    }

    public static AiProposalView ToView(AiProposalModel proposal) => new()
    {
        Id = proposal.Id,
        FileName = proposal.FileName,
        Summary = proposal.Summary,
        Status = proposal.Status,
        TestStatus = proposal.TestStatus,
        TestRunLogId = proposal.TestRunLogId,
        BaseHash = proposal.BaseHash,
        NewHash = proposal.NewHash,
        Diagnostics = proposal.Diagnostics,
        AppliedVersionId = proposal.AppliedVersionId,
        CreateTime = proposal.CreateTime
    };

    /// <summary>页面「再试运行一次」：写影子文件跑一遍，回填试运行状态并追加一张结果卡。</summary>
    public async Task<AiTestRunResult> TestRunAsync(string id, AiSettingModel setting, CancellationToken ct = default)
    {
        var proposal = await GetAsync(id);
        if (proposal.Status is AiProposalStatus.Applied or AiProposalStatus.Discarded)
        {
            throw new BusinessException("该提案已结束，无法再试运行！");
        }
        proposal.TestStatus = AiTestStatus.Running;
        await SyncProposalMessageAsync(proposal, ct);
        await _db.SaveChangesAsync(ct);
        var notifyMode = setting?.TestRunNotifyMode ?? AiTestRunNotifyMode.Task;
        var result = await _testRunService.RunAsync(proposal.FileName, proposal.NewContent, notifyMode, ct);
        proposal.TestStatus = result.Status;
        proposal.TestRunLogId = result.LogId;
        await SyncProposalMessageAsync(proposal, ct);
        await _db.SaveChangesAsync(ct);

        var payload = JsonConvert.SerializeObject(new
        {
            ProposalId = proposal.Id,
            result.Status,
            result.DurationMs,
            result.HasException,
            result.LogId,
            result.Message,
            LogTail = Truncate(result.LogTail, 4000)
        });
        // Seq 走 AgentService 统一分配（锁内含未提交行取大）：本消息与「run 已 WaitingConfirm 期间
        // 用户在别的入口点试运行/应用/忽略」或下一个 run 的收尾写可能并发，裸 max+1 会重号
        var resultMessage = new AiMessageModel
        {
            ConversationId = proposal.ConversationId,
            Role = AiMessageRole.Assistant,
            Kind = AiMessageKind.TestResult,
            Content = $"{proposal.FileName} 试运行：{result.Message}",
            Payload = payload,
            CreateTime = DateTime.Now
        };
        AgentService.AllocateSeq(_db, resultMessage);
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// 应用提案：基线哈希比对 → 门禁/编译复校 → 写文件 → 记脚本版本 → 置已应用 → 站内通知。
    /// 任一步不过即拒绝且不改磁盘（失败原因原样返回给页面展示诊断清单）。
    /// </summary>
    public async Task<ScriptBuildService.ScriptSaveResult> ApplyAsync(string id, string operatorName, CancellationToken ct = default)
    {
        var proposal = await GetAsync(id);
        if (proposal.Status == AiProposalStatus.Applied)
        {
            throw new BusinessException("该提案已经应用过了！");
        }
        if (proposal.Status == AiProposalStatus.Discarded)
        {
            throw new BusinessException("该提案已被忽略，无法应用！");
        }
        var scriptFile = SafeFile.Resolve("./scripts/quantum", proposal.FileName);
        if (scriptFile == null)
        {
            throw new BusinessException("脚本路径非法，已拒绝应用！");
        }
        var currentContent = File.Exists(scriptFile)
            ? ScriptVersionService.Normalize(await File.ReadAllTextAsync(scriptFile, ct))
            : string.Empty;
        if (ScriptVersionService.HashOf(currentContent) != proposal.BaseHash)
        {
            proposal.Status = AiProposalStatus.Stale;
            await SyncProposalMessageAsync(proposal, ct);
            await _db.SaveChangesAsync(ct);
            throw new BusinessException("脚本在提案生成后已被改动（人工编辑或其他 Agent 应用），已拒绝覆盖。请让 AI 基于最新脚本重新生成提案。");
        }

        var build = ScriptBuildService.Build(proposal.NewContent, scriptFile);
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
        await File.WriteAllTextAsync(scriptFile, proposal.NewContent + Environment.NewLine, ct);
        var taskId = await _db.Tasks.AsNoTracking()
            .Where(n => n.FileName == proposal.FileName)
            .Select(n => n.Id).FirstOrDefaultAsync(ct);
        var version = await _versionService.RecordAsync(proposal.FileName, proposal.NewContent,
            ScriptVersionSource.AgentApply, $"AI 提案应用：{Truncate(proposal.Summary, 200)}", operatorName, taskId);

        proposal.Status = AiProposalStatus.Applied;
        proposal.AppliedVersionId = version?.Id;
        proposal.AppliedTime = DateTime.Now;
        await SyncProposalMessageAsync(proposal, ct);
        await _db.SaveChangesAsync(ct);

        await AppendAsync(proposal.ConversationId, AiMessageKind.Text,
            $"已应用 AI 提案并写入脚本 {proposal.FileName}（版本历史可回滚）。");
        Notify($"AI 已更新脚本 {proposal.FileName}", $"AI 提案已应用（版本历史可回滚）。{Truncate(proposal.Summary, 200)}");
        return new ScriptBuildService.ScriptSaveResult { Success = true, Warnings = build.Warnings };
    }

    /// <summary>忽略提案（只改状态，不留痕到脚本）。</summary>
    public async Task<bool> DiscardAsync(string id, CancellationToken ct = default)
    {
        var proposal = await GetAsync(id);
        proposal.Status = AiProposalStatus.Discarded;
        await SyncProposalMessageAsync(proposal, ct);
        await _db.SaveChangesAsync(ct);
        await AppendAsync(proposal.ConversationId, AiMessageKind.Text, $"已忽略关于 {proposal.FileName} 的提案。");
        return true;
    }

    /// <summary>供 AgentService 在 propose_fix 之后写提案卡消息。</summary>
    public async Task<AiProposalModel> AttachMessageAsync(string proposalId, string conversationId, CancellationToken ct = default)
    {
        var proposal = await GetAsync(proposalId);
        var message = new AiMessageModel
        {
            ConversationId = conversationId,
            Role = AiMessageRole.Assistant,
            Kind = AiMessageKind.Proposal,
            Content = $"修复提案：{proposal.FileName}",
            Payload = JsonConvert.SerializeObject(ToView(proposal)),
            CreateTime = DateTime.Now
        };
        AgentService.AllocateSeq(_db, message);
        proposal.MessageId = message.Id;
        await _db.SaveChangesAsync(ct);
        return proposal;
    }

    /// <summary>提案保留期清理：已结束（应用/忽略/过期）的提案保留 90 天后删除记录。</summary>
    public async Task<int> CleanupAsync(int keepDays = 90, CancellationToken ct = default)
    {
        var deadline = DateTime.Now.AddDays(-keepDays);
        var stale = await _db.AiProposals
            .Where(n => n.Status != AiProposalStatus.Pending && n.CreateTime < deadline)
            .Take(500).ToListAsync(ct);
        if (stale.Count == 0)
        {
            return 0;
        }
        _db.AiProposals.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
        return stale.Count;
    }

    private async Task AppendAsync(string conversationId, string kind, string content)
    {
        var message = new AiMessageModel
        {
            ConversationId = conversationId,
            Role = AiMessageRole.Assistant,
            Kind = kind,
            Content = content,
            CreateTime = DateTime.Now
        };
        AgentService.AllocateSeq(_db, message);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 提案状态变化后回写提案卡消息的 Payload：Payload 是创建时的一次性快照，
    /// 不回写则页面（App/Web）重拉后卡片仍显示旧状态（如已应用仍显示待确认可再点）。
    /// 与调用处的 SaveChanges 合并在同一事务里生效。
    /// </summary>
    private async Task SyncProposalMessageAsync(AiProposalModel proposal, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(proposal.MessageId))
        {
            return;
        }
        var message = await _db.AiMessages.FirstOrDefaultAsync(n => n.Id == proposal.MessageId, ct);
        if (message != null && message.Kind == AiMessageKind.Proposal)
        {
            message.Payload = JsonConvert.SerializeObject(ToView(proposal));
        }
    }

    /// <summary>
    /// 站内通知（App 侧）：落通知表 + 镜像进会话流，App 无需新页面即可收到「AI 已更新脚本」。
    /// fire-and-forget，不阻塞应用动作。
    /// </summary>
    private static void Notify(string title, string content)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await AppPushDispatcher.SendNotificationAsync("AI助手", content, "ai", "quantum://ai");
            }
            catch (Exception e)
            {
                _log.Warn($"AI 通知发送失败：{e.Message}");
            }
        });
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}
