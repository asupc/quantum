using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application.Channels;

/// <summary>消费已落库的私聊消息；先声明处理再执行，避免平台重放重复执行脚本。</summary>
public sealed class ChannelCommandWorker(IServiceScopeFactory scopes, ILogger<ChannelCommandWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 先完成历史消息清理；失败时不消费旧入站，避免升级后意外执行旧脚本。
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await CleanupLegacyAsync(scope.ServiceProvider.GetRequiredService<IQuantumDbContext>(), stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                logger.LogError(e, "消息通道历史入站清理失败，暂缓执行平台指令");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
                var item = await db.ChannelInboxes.AsNoTracking().Where(x => x.Status == "Received")
                    .OrderBy(x => x.ReceivedAtUtc).FirstOrDefaultAsync(stoppingToken);
                if (item is null)
                {
                    await Task.Delay(300, stoppingToken);
                    continue;
                }
                var claimed = await db.ChannelInboxes.Where(x => x.Id == item.Id && x.Status == "Received")
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Processing")
                        .SetProperty(x => x.ClaimedAtUtc, DateTime.UtcNow)
                        .SetProperty(x => x.Attempt, x => x.Attempt + 1), stoppingToken);
                if (claimed == 0) continue;
                var account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.AccountId, stoppingToken);
                var route = await db.ChannelReplyRoutes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.ReplyRouteId, stoppingToken);
                if (account is null || !account.Enabled || route is null || route.AccountId != account.Id ||
                    route.BindingVersion != account.BindingVersion || route.PeerId != item.PeerId ||
                    route.ExpiresAtUtc <= DateTime.UtcNow)
                {
                    await FinishAsync(db, item.Id, "Cancelled", stoppingToken);
                    continue;
                }
                try
                {
                    var message = new MessageProccessDTO
                    {
                        CommunicationType = CommunicationType.App,
                        user_id = SystemConfigHelper.GetSetting()?.UserName ?? "admin",
                        message = item.Content,
                        message_id = item.Id,
                        SessionKey = "channel:" + account.Platform.ToLowerInvariant(),
                        ChannelReplyRouteId = route.Id,
                        ChannelActorKey = ResolveActorKey(account, item.PeerId)
                    };
                    await scope.ServiceProvider.GetRequiredService<MessageProcess>().MessageAsync(message);
                    await FinishAsync(db, item.Id, "Processed", stoppingToken);
                }
                catch
                {
                    await FinishAsync(db, item.Id, "Unknown", stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch
            {
                try { await Task.Delay(2000, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    /// <summary>同一私聊的后续消息复用等待步骤；换平台、换发件人或账户版本变化必须隔离。</summary>
    internal static string ResolveActorKey(ChannelAccountModel account, string peerId)
        => $"channel:{account.Platform}:{account.Id}:{account.BindingVersion}:{peerId}";

    internal static async Task CleanupLegacyAsync(IQuantumDbContext db, CancellationToken ct)
    {
        // 升级前已收消息只有固定回执/白名单回复，不可在新版本重放为高权限脚本指令。
        await db.ChannelInboxes.Where(x => x.Status == "Received" && db.ChannelOutboxes.Any(o =>
                o.ReplyRouteId == x.ReplyRouteId && (o.Purpose == "Receipt" || o.Purpose == "QuickReply")))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "Processed"), ct);
        await db.ChannelOutboxes.Where(x => (x.Purpose == "Receipt" || x.Purpose == "QuickReply") &&
                (x.Status == "Pending" || x.Status == "Retry" || x.Status == "Sending"))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "Cancelled"), ct);
        await db.ChannelInboxes.Where(x => x.Status == "Processing")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "Unknown"), ct);
        // 2026-10-12 机器人消息不同步 Web/App：清理历史通道会话（channel:* 会话行与消息行）。
        // 一次性数据迁移，幂等——入站/出站链路已不再产生 channel:* 行，清理后不再增长。
        await db.ChatSessions.Where(x => x.SessionKey.StartsWith("channel:"))
            .ExecuteDeleteAsync(ct);
        await db.ChatMessages.Where(x => x.SessionKey.StartsWith("channel:"))
            .ExecuteDeleteAsync(ct);
    }

    private static Task<int> FinishAsync(IQuantumDbContext db, string id, string status, CancellationToken ct)
        => db.ChannelInboxes.Where(x => x.Id == id && x.Status == "Processing")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status)
                .SetProperty(x => x.FinishedAtUtc, DateTime.UtcNow), ct);
}

/// <summary>仅在当前平台指令执行的异步上下文内传递回复目标，不影响全局通知。</summary>
public static class ChannelReplyContext
{
    private static readonly AsyncLocal<string> CurrentRoute = new();
    public static string Current => CurrentRoute.Value;
    public static IDisposable Use(string routeId)
    {
        var previous = CurrentRoute.Value;
        CurrentRoute.Value = routeId;
        return new Restore(() => CurrentRoute.Value = previous);
    }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}
