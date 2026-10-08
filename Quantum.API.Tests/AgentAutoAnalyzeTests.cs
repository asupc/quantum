using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 失败自动分析防重（2026-10-08 修复）回归：
/// AI 修复提案未被应用/忽略前任务每次执行仍会失败，若无防重，同一脚本会反复发起自动分析
/// （重复烧模型调用、堆重复提案）。锁定：Pending 提案或运行中 run 拦截，提案终结后放行。
/// </summary>
[Collection("ConstsState")]
public class AgentAutoAnalyzeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;

    private const string ConversationId = "conv-auto-analyze";

    public AgentAutoAnalyzeTests()
    {
        (_connection, _db) = AppTestDb.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task SkipReason_NoProposalNoRunningRun_Passes()
    {
        Assert.Null(await AgentAutoAnalyze.SkipReasonAsync(_db, ConversationId, "a.cs"));
    }

    [Fact]
    public async Task SkipReason_PendingProposalForSameFile_Blocks()
    {
        _db.AiProposals.Add(new AiProposalModel
        {
            ConversationId = ConversationId,
            FileName = "a.cs",
            Status = AiProposalStatus.Pending,
            CreateTime = DateTime.Now
        });
        await _db.SaveChangesAsync();

        var reason = await AgentAutoAnalyze.SkipReasonAsync(_db, ConversationId, "a.cs");
        Assert.NotNull(reason);
        Assert.Contains("待确认", reason);
        // 其他脚本不受影响
        Assert.Null(await AgentAutoAnalyze.SkipReasonAsync(_db, ConversationId, "b.cs"));
    }

    [Fact]
    public async Task SkipReason_ProposalInOtherConversationAlsoBlocks()
    {
        // 手动会话里生成的待确认提案同样代表「修复在等决定」，自动分析也不应重复起跑
        _db.AiProposals.Add(new AiProposalModel
        {
            ConversationId = "conv-manual",
            FileName = "a.cs",
            Status = AiProposalStatus.Pending,
            CreateTime = DateTime.Now
        });
        await _db.SaveChangesAsync();

        Assert.NotNull(await AgentAutoAnalyze.SkipReasonAsync(_db, ConversationId, "a.cs"));
    }

    [Fact]
    public async Task SkipReason_ResolvedProposal_Passes()
    {
        foreach (var status in new[] { AiProposalStatus.Applied, AiProposalStatus.Discarded, AiProposalStatus.Stale })
        {
            _db.AiProposals.Add(new AiProposalModel
            {
                ConversationId = ConversationId,
                FileName = "a.cs",
                Status = status,
                CreateTime = DateTime.Now
            });
        }
        await _db.SaveChangesAsync();

        // 已应用后任务若仍失败，应允许重新分析；已忽略/已过期同理
        Assert.Null(await AgentAutoAnalyze.SkipReasonAsync(_db, ConversationId, "a.cs"));
    }

    [Fact]
    public async Task SkipReason_RunningRunInConversation_Blocks()
    {
        _db.AiRuns.Add(new AiRunModel
        {
            ConversationId = ConversationId,
            Status = AiRunStatus.Running,
            CreateTime = DateTime.Now
        });
        await _db.SaveChangesAsync();

        Assert.NotNull(await AgentAutoAnalyze.SkipReasonAsync(_db, ConversationId, "any.cs"));
    }

    [Fact]
    public async Task SkipReason_FinishedRun_Passes()
    {
        // WaitingConfirm / Succeeded / Failed 的 run 本身不拦截——挂起态由 Pending 提案表达
        foreach (var status in new[] { AiRunStatus.WaitingConfirm, AiRunStatus.Succeeded, AiRunStatus.Failed, AiRunStatus.Cancelled })
        {
            _db.AiRuns.Add(new AiRunModel
            {
                ConversationId = ConversationId,
                Status = status,
                CreateTime = DateTime.Now
            });
        }
        await _db.SaveChangesAsync();

        Assert.Null(await AgentAutoAnalyze.SkipReasonAsync(_db, ConversationId, "any.cs"));
    }
}
