using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.Model;

namespace Quantum.Application.Channels;

/// <summary>把指令文字回复写入原发件人的平台出站账本；不接受外部请求指定目标。</summary>
public sealed class ChannelReplyService(IQuantumDbContext db, ChannelAccountGate gate)
{
    /// <summary>入队一条原路回复，返回出站记录 Id；被窗口/上限/绑定等条件拒绝时返回 null（调用方据此给出可读原因）。</summary>
    public async Task<string> QueueAsync(string routeId, string content)
    {
        if (string.IsNullOrWhiteSpace(routeId) || string.IsNullOrWhiteSpace(content)) return null;
        var route = await db.ChannelReplyRoutes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == routeId);
        if (route is null) return null;
        var account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == route.AccountId);
        if (account is null) return null;
        using var held = await gate.AcquireAsync(account.Platform);
        await using var tx = await db.Database.BeginTransactionAsync();
        route = await db.ChannelReplyRoutes.FirstOrDefaultAsync(x => x.Id == routeId);
        account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == account.Id);
        if (route is null || account is null || !account.Enabled ||
            route.AccountId != account.Id || route.BindingVersion != account.BindingVersion ||
            route.ExpiresAtUtc <= DateTime.UtcNow) return null;
        // QQ 被动回复对同一消息最多支持四条；超限只保留 App/Web 会话内完整消息。
        if (account.Platform == ChannelManagementService.Qq && route.NextMessageSequence > 4) return null;
        var reply = content.Length <= 1500 ? content : content[..1470] + "…（完整内容请在 App/Web 查看）";
        var outbox = new ChannelOutboxModel
        {
            AccountId = account.Id, ReplyRouteId = route.Id, PeerId = route.PeerId,
            BindingVersion = account.BindingVersion, Purpose = "Command",
            MessageSequence = route.NextMessageSequence++, Status = "Pending", Content = reply,
            ClientId = Guid.NewGuid().ToString("N"), CreatedAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = DateTime.UtcNow
        };
        db.ChannelOutboxes.Add(outbox);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return outbox.Id;
    }
}
