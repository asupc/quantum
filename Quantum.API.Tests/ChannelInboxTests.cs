using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Quantum.Application.Channels;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;

namespace Quantum.API.Tests;

/// <summary>单用户通道：所有合法私聊均接收，按来源分路由，重复事件不重复执行。</summary>
public sealed class ChannelInboxTests
{
    [Fact]
    public async Task ConfiguredQq_AcceptsAllPrivatePeersWithIsolatedRoutes_AndDeduplicates()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var protector = new ChannelSecretProtector(RandomNumberGenerator.GetBytes(32));
            var admin = new ChannelManagementService(db, protector);
            var status = await admin.ConfigureQqAsync(new QqChannelSaveDto { AppId = "bot-id", AppSecret = "secret" });
            Assert.True(status.Enabled);
            var account = await db.ChannelAccounts.SingleAsync();
            var inbox = new ChannelInboxService(db, protector);
            var first = new ChannelIncoming("C2C_MESSAGE_CREATE", "id1", "idx1", "peer1", "帮助");
            var second = new ChannelIncoming("C2C_MESSAGE_CREATE", "id2", null, "peer2", "运行任务");
            await inbox.AcceptBatchAsync(account.Id, ChannelManagementService.Qq, [first, second],
                qqSequence: 7, qqSessionId: "session1");
            Assert.Equal(2, await db.ChannelInboxes.CountAsync(x => x.Status == "Received"));
            // 2026-10-12 机器人消息不同步 Web/App：入站不落会话回显
            Assert.Empty(await db.ChatMessages.ToListAsync());
            Assert.Empty(await db.ChannelOutboxes.ToListAsync()); // 回复由统一指令链产生，不发固定回执。
            Assert.Equal(7, (await db.ChannelCursors.SingleAsync()).QqSequence);
            var routes = await db.ChannelReplyRoutes.ToListAsync();
            Assert.Contains(routes, x => x.PeerId == "peer1");
            Assert.Contains(routes, x => x.PeerId == "peer2");
            Assert.Contains(await db.ChannelInboxes.ToListAsync(), x => x.MessageId == "id2" && x.MessageIndex == "0");
            await inbox.AcceptBatchAsync(account.Id, ChannelManagementService.Qq, [first], qqSequence: 7);
            Assert.Equal(2, await db.ChannelInboxes.CountAsync(x => x.Status == "Received"));
            Assert.Empty(await db.ChatMessages.ToListAsync());
        }
    }

    [Fact]
    public async Task DisabledAndGroupMessages_AreRejectedWithoutExposingContent()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var protector = new ChannelSecretProtector(RandomNumberGenerator.GetBytes(32));
            var admin = new ChannelManagementService(db, protector);
            await admin.ConfigureQqAsync(new QqChannelSaveDto { AppId = "bot", AppSecret = "secret" });
            var account = await db.ChannelAccounts.SingleAsync();
            var inbox = new ChannelInboxService(db, protector);
            await inbox.AcceptBatchAsync(account.Id, ChannelManagementService.Qq,
                [new ChannelIncoming("GROUP_AT_MESSAGE_CREATE", "event1", "idx1", "peer", "SECRET")], qqSequence: 9);
            await admin.UnbindAsync(ChannelManagementService.Qq, true);
            await inbox.AcceptBatchAsync(account.Id, ChannelManagementService.Qq,
                [new ChannelIncoming("C2C_MESSAGE_CREATE", "event2", "idx2", "peer", "SECRET")], qqSequence: 10);
            Assert.All(await db.ChannelInboxes.ToListAsync(), x => Assert.Null(x.Content));
            Assert.Empty(await db.ChannelReplyRoutes.ToListAsync());
            Assert.Empty(await db.ChatMessages.ToListAsync());
            Assert.Equal(10, (await db.ChannelCursors.SingleAsync()).QqSequence);
        }
    }
    [Fact]
    public async Task Upgrade_DoesNotExecuteLegacyReceivedCommands()
    {
        var (connection, db) = AppTestDb.Create();
        using (connection)
        using (db)
        {
            var account = new ChannelAccountModel { Platform = ChannelManagementService.Qq, BotId = "bot", Enabled = true };
            db.ChannelAccounts.Add(account);
            var oldRoute = new ChannelReplyRouteModel { AccountId = account.Id, PeerId = "old", ReplyMessageId = "old-id" };
            var newRoute = new ChannelReplyRouteModel { AccountId = account.Id, PeerId = "new", ReplyMessageId = "new-id" };
            db.ChannelReplyRoutes.AddRange(oldRoute, newRoute);
            var oldMessage = new ChannelInboxModel { AccountId = account.Id, EventType = "C2C_MESSAGE_CREATE",
                MessageId = "old-id", MessageIndex = "0", PeerId = "old", Content = "运行旧脚本",
                Status = "Received", ReplyRouteId = oldRoute.Id };
            var newMessage = new ChannelInboxModel { AccountId = account.Id, EventType = "C2C_MESSAGE_CREATE",
                MessageId = "new-id", MessageIndex = "0", PeerId = "new", Content = "运行新脚本",
                Status = "Received", ReplyRouteId = newRoute.Id };
            db.ChannelInboxes.AddRange(oldMessage, newMessage);
            db.ChannelOutboxes.Add(new ChannelOutboxModel { AccountId = account.Id, ReplyRouteId = oldRoute.Id,
                PeerId = "old", Status = "Pending", Purpose = "Receipt", Content = "旧回执", ClientId = "old-client" });
            // 2026-10-12 存量清理：channel:* 会话行与消息行删除，任务会话/默认会话不受影响
            db.ChatSessions.Add(new ChatSessionModel { SessionKey = "channel:feishubot", CreateTime = DateTime.Now, LastSeq = 9 });
            db.ChatSessions.Add(new ChatSessionModel { SessionKey = "task-keep", CreateTime = DateTime.Now, LastSeq = 8 });
            db.ChatMessages.Add(new ChatMessageModel { Id = "legacy-1", Seq = 9, MsgId = "legacy-m1",
                Direction = ChatMessageDirection.接收, Content = "旧指令", SessionKey = "channel:feishubot", CreateTime = DateTime.Now });
            db.ChatMessages.Add(new ChatMessageModel { Id = "legacy-2", Seq = 8, MsgId = "legacy-m2",
                Direction = ChatMessageDirection.发送, Content = "任务消息保留", SessionKey = "task-keep", CreateTime = DateTime.Now });
            await db.SaveChangesAsync();
            await ChannelCommandWorker.CleanupLegacyAsync(db, CancellationToken.None);
            db.ChangeTracker.Clear();
            Assert.Equal("Processed", (await db.ChannelInboxes.SingleAsync(x => x.Id == oldMessage.Id)).Status);
            Assert.Equal("Received", (await db.ChannelInboxes.SingleAsync(x => x.Id == newMessage.Id)).Status);
            Assert.Equal("Cancelled", (await db.ChannelOutboxes.SingleAsync()).Status);
            Assert.Equal("task-keep", (await db.ChatSessions.SingleAsync()).SessionKey);
            Assert.Equal("task-keep", (await db.ChatMessages.SingleAsync()).SessionKey);
        }
    }

}
