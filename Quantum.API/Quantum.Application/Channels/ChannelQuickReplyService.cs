using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application.Channels;

/// <summary>仅允许 Manager 显式勾选、已启用、非正则、纯文本快捷回复；不会触碰 MessageProcess/任务脚本。</summary>
public sealed class ChannelQuickReplyService
{
    private readonly IQuantumDbContext _db;
    private readonly ChannelAccountGate _gate;

    public ChannelQuickReplyService(IQuantumDbContext db, ChannelAccountGate gate)
    {
        _db = db; _gate = gate;
    }

    private IQueryable<CommandModel> Eligible(string platform)
    {
        // 不再按平台→通讯类型隔离（CommandModel.CommunicationType 已删除）：单管理员模式下各来源等价，
        // 仍只放行「已启用 + 非正则 + 纯文本」，避免把正则指令当快捷回复展示
        return _db.Commands.Where(x => x.Enable && !x.EnableRegex && x.MessageType == MessageType.文本 &&
            x.Key != null && x.Message != null);
    }

    public async Task<List<ChannelQuickReplyDto>> ListAsync(string platform)
    {
        platform = ChannelManagementService.RequirePlatform(platform);
        var accountId = await _db.ChannelAccounts.AsNoTracking().Where(x => x.Platform == platform).Select(x => x.Id).FirstOrDefaultAsync();
        var selected = accountId is null ? [] : await _db.ChannelAllowedCommands.AsNoTracking()
            .Where(x => x.AccountId == accountId).Select(x => x.CommandId).ToListAsync();
        return (await Eligible(platform).AsNoTracking().OrderBy(x => x.Key).Take(200).ToListAsync())
            .Select(x => new ChannelQuickReplyDto { Id = x.Id, Key = x.Key, Selected = selected.Contains(x.Id) }).ToList();
    }

    public async Task<List<ChannelQuickReplyDto>> SetAllowedAsync(string platform, IReadOnlyCollection<string> commandIds)
    {
        platform = ChannelManagementService.RequirePlatform(platform);
        var selected = (commandIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count > 50 || selected.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64))
            throw new BusinessException("每个平台最多允许 50 条安全纯文本快捷回复");
        using var held = await _gate.AcquireAsync(platform);
        var account = await _db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Platform == platform);
        if (account is null) throw new BusinessException("先配置该平台机器人");
        var valid = await Eligible(platform).AsNoTracking().Where(x => selected.Contains(x.Id))
            .Select(x => x.Id).ToListAsync();
        if (valid.Count != selected.Count) throw new BusinessException("存在不可用或非纯文本快捷回复，已拒绝保存");
        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.ChannelAllowedCommands.Where(x => x.AccountId == account.Id).ExecuteDeleteAsync();
        foreach (var id in selected)
            _db.ChannelAllowedCommands.Add(new ChannelAllowedCommandModel { AccountId = account.Id, CommandId = id });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return await ListAsync(platform);
    }

    public async Task<string> ResolveAsync(string accountId, string platform, string text)
    {
        if (text is null) return null;
        platform = ChannelManagementService.RequirePlatform(platform);
        var allowed = await _db.ChannelAllowedCommands.AsNoTracking().Where(x => x.AccountId == accountId)
            .Select(x => x.CommandId).ToListAsync();
        if (allowed.Count == 0) return null;
        var commands = await Eligible(platform).AsNoTracking().Where(x => allowed.Contains(x.Id)).Take(50).ToListAsync();
        var match = commands.FirstOrDefault(x => string.Equals(x.Key, text.Trim(), StringComparison.Ordinal));
        if (match is null) return null;
        return match.Message.Length <= 1500 ? match.Message : "快捷回复过长，请在管理端缩短内容";
    }
}
