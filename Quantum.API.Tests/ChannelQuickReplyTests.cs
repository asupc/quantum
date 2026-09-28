using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Application.Channels;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;

namespace Quantum.API.Tests;

/// <summary>指令回复不依赖单独快捷回复白名单，每条只发回自己的私聊来源。</summary>
public sealed class ChannelQuickReplyTests
{
    [Fact]
    public async Task RepliesUseIncomingPeerWithoutAllowlist_AndRevokedRouteCannotSend()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var gate = new ChannelAccountGate();
            var secret = new ChannelSecretProtector(RandomNumberGenerator.GetBytes(32));
            var admin = new ChannelManagementService(db, secret, gate);
            await admin.ConfigureQqAsync(new QqChannelSaveDto { AppId = "bot", AppSecret = "secret" });
            var account = await db.ChannelAccounts.SingleAsync();
            var inbox = new ChannelInboxService(db, new AppMessageService(db, NullLogger<AppMessageService>.Instance),
                new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance), secret);
            await inbox.AcceptBatchAsync(account.Id, ChannelManagementService.Qq,
            [
                new ChannelIncoming("C2C_MESSAGE_CREATE", "m1", "i1", "peer-a", "帮助"),
                new ChannelIncoming("C2C_MESSAGE_CREATE", "m2", "i2", "peer-b", "帮助")
            ]);
            var routes = await db.ChannelReplyRoutes.OrderBy(x => x.PeerId).ToListAsync();
            var replies = new ChannelReplyService(db, gate);
            await replies.QueueAsync(routes[0].Id, "给 A 的回复");
            await replies.QueueAsync(routes[1].Id, "给 B 的回复");
            var outgoing = await db.ChannelOutboxes.OrderBy(x => x.PeerId).ToListAsync();
            Assert.Equal(2, outgoing.Count);
            Assert.Equal("peer-a", outgoing[0].PeerId);
            Assert.Equal("给 A 的回复", outgoing[0].Content);
            Assert.Equal("peer-b", outgoing[1].PeerId);
            Assert.Equal("给 B 的回复", outgoing[1].Content);
            await admin.UnbindAsync(ChannelManagementService.Qq, true);
            db.ChangeTracker.Clear(); // ExecuteUpdate 绕过已跟踪的 Pending 实例。
            await replies.QueueAsync(routes[0].Id, "旧路由");
            Assert.Equal(2, await db.ChannelOutboxes.CountAsync());
            Assert.All(await db.ChannelOutboxes.ToListAsync(), x => Assert.Equal("Cancelled", x.Status));
        }
    }
}
