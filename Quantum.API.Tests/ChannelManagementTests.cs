using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quantum.Application.Channels;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>通道默认拒绝、挑战码/单绑定、换绑废止旧路由及独立密钥约束。</summary>
public sealed class ChannelManagementTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly ChannelSecretProtector _secret = new(RandomNumberGenerator.GetBytes(32));
    private readonly ChannelManagementService _service;

    public ChannelManagementTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new ChannelManagementService(_db, _secret);
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    [Fact]
    public void ProtectedSecrets_DoNotLeak_AndAreBoundToPurpose()
    {
        var cipher = _secret.Protect("TEST-SECRET-DO-NOT-LOG", "account1", "qq-credentials");
        Assert.DoesNotContain("TEST-SECRET", cipher);
        Assert.Equal("TEST-SECRET-DO-NOT-LOG", _secret.Unprotect(cipher, "account1", "qq-credentials"));
        Assert.Throws<BusinessException>(() => _secret.Unprotect(cipher, "account2", "qq-credentials"));
        Assert.Throws<BusinessException>(() => _secret.Unprotect(cipher, "account1", "weixin-credentials"));
        Assert.False(_secret.VerifyChallenge("account1", "wrong", _secret.HashChallenge("account1", "right")));
    }

    [Fact]
    public async Task Qq_ConfiguredBot_IsImmediatelyAvailableWithoutPrivatePeerBinding()
    {
        var status = await _service.ConfigureQqAsync(new QqChannelSaveDto { AppId = "test-app", AppSecret = "test-secret" });
        Assert.True(status.Configured);
        Assert.True(status.Enabled);
        Assert.False(await _db.ChannelBindings.AnyAsync());
        Assert.Null((await _db.ChannelAccounts.SingleAsync()).ChallengeHash);
    }

    [Fact]
    public async Task Reconfigure_CancelsOutstandingMessagesAndInvalidatesRoute()
    {
        await _service.ConfigureQqAsync(new QqChannelSaveDto { AppId = "old-app", AppSecret = "old-secret" });
        var account = await _db.ChannelAccounts.SingleAsync();
        var oldVersion = account.BindingVersion;
        var route = new ChannelReplyRouteModel { AccountId = account.Id, PeerId = "old-peer", BindingVersion = oldVersion,
            ReplyMessageId = "id", ContextTokenCiphertext = "encrypted", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) };
        _db.ChannelReplyRoutes.Add(route);
        _db.ChannelOutboxes.Add(new ChannelOutboxModel { AccountId = account.Id, ReplyRouteId = route.Id, PeerId = "old-peer",
            BindingVersion = oldVersion, MessageSequence = 1, Status = "Pending", Purpose = "Reply", Content = "test", ClientId = "test-client" });
        await _db.SaveChangesAsync();
        var status = await _service.ConfigureQqAsync(new QqChannelSaveDto { AppId = "new-app", AppSecret = "new-secret" });
        Assert.True(status.Enabled);
        _db.ChangeTracker.Clear();
        Assert.Equal("Cancelled", (await _db.ChannelOutboxes.SingleAsync()).Status);
        Assert.Null((await _db.ChannelReplyRoutes.SingleAsync()).ContextTokenCiphertext);
        Assert.True((await _db.ChannelAccounts.SingleAsync()).BindingVersion > oldVersion);
    }

    [Fact]
    public async Task WeixinScanWithoutUserId_IsImmediatelyAvailable()
    {
        var code = await _service.RegisterWeixinScanAsync("bot", "dummy-token", null, "https://ilinkai.weixin.qq.com/");
        Assert.Null(code);
        var account = await _db.ChannelAccounts.SingleAsync();
        Assert.True(account.Enabled);
        Assert.Null(account.CandidatePeerId);
        Assert.Null(account.ChallengeHash);
        Assert.Empty(await _db.ChannelBindings.ToListAsync());
    }

    [Fact]
    public async Task UnknownReply_RequiresManagerConfirmationAndPreservesIdempotency()
    {
        await _service.ConfigureQqAsync(new QqChannelSaveDto { AppId = "test-app", AppSecret = "test-secret" });
        var account = await _db.ChannelAccounts.SingleAsync();
        var route = new ChannelReplyRouteModel { AccountId = account.Id, PeerId = "peer", BindingVersion = account.BindingVersion,
            ReplyMessageId = "platform-message-id", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) };
        _db.ChannelReplyRoutes.Add(route);
        var outbox = new ChannelOutboxModel { AccountId = account.Id, ReplyRouteId = route.Id,
            PeerId = "peer", BindingVersion = account.BindingVersion, Status = "Unknown", Purpose = "Reply",
            MessageSequence = 1, ClientId = "stable-id", Content = "test" };
        _db.ChannelOutboxes.Add(outbox);
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessException>(() => _service.RetryDeliveryAsync(ChannelManagementService.Qq, outbox.Id, false));
        var queued = await _service.RetryDeliveryAsync(ChannelManagementService.Qq, outbox.Id, true);
        Assert.Equal("Pending", queued.Status);
        Assert.Equal("stable-id", (await _db.ChannelOutboxes.SingleAsync()).ClientId);
        Assert.Equal(1, (await _db.ChannelOutboxes.SingleAsync()).MessageSequence);
        await _service.UnbindAsync(ChannelManagementService.Qq, true);
        await Assert.ThrowsAsync<BusinessException>(() => _service.RetryDeliveryAsync(ChannelManagementService.Qq, outbox.Id, true));
    }

    [Fact]
    public async Task DatabaseRejectsSecondAccountWithSamePlatform()
    {
        _db.ChannelAccounts.Add(new ChannelAccountModel { Platform = ChannelManagementService.Qq, BotId = "test-bot" });
        await _db.SaveChangesAsync();
        _db.ChannelAccounts.Add(new ChannelAccountModel { Platform = ChannelManagementService.Qq, BotId = "test-bot" });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task TestSend_WithoutAnyRoute_FailsWithActionableReasonAndQueuesNothing()
    {
        await _service.ConfigureQqAsync(new QqChannelSaveDto { AppId = "test-app", AppSecret = "test-secret" });

        // 三家平台都只能按入站消息原路回复；没有路由时必须把原因说清，而不是静默入队一条发不出去的
        var error = await Assert.ThrowsAsync<BusinessException>(() => _service.TestSendAsync(ChannelManagementService.Qq));

        Assert.Contains("请先用手机给机器人发一条消息", error.Message);
        Assert.Empty(await _db.ChannelOutboxes.ToListAsync());
    }

    [Fact]
    public async Task TestSend_TargetsOnlyTheMostRecentValidRoute_IgnoringExpiredOnes()
    {
        await _service.ConfigureQqAsync(new QqChannelSaveDto { AppId = "test-app", AppSecret = "test-secret" });
        var account = await _db.ChannelAccounts.FirstAsync(x => x.Platform == ChannelManagementService.Qq);
        _db.ChannelReplyRoutes.AddRange(
            new ChannelReplyRouteModel { AccountId = account.Id, PeerId = "PEER_OLD", BindingVersion = account.BindingVersion, ReplyMessageId = "m-old", CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) },
            new ChannelReplyRouteModel { AccountId = account.Id, PeerId = "PEER_LATEST", BindingVersion = account.BindingVersion, ReplyMessageId = "m-new", CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) },
            new ChannelReplyRouteModel { AccountId = account.Id, PeerId = "PEER_EXPIRED", BindingVersion = account.BindingVersion, ReplyMessageId = "m-dead", CreatedAtUtc = DateTime.UtcNow.AddHours(-2), ExpiresAtUtc = DateTime.UtcNow.AddHours(-1) });
        await _db.SaveChangesAsync();

        var sent = await _service.TestSendAsync(ChannelManagementService.Qq);

        Assert.Equal("Pending", sent.Status);
        // 自检消息绝不允许由调用方指定收件人：目标必须来自最近一条有效路由
        var row = await _db.ChannelOutboxes.SingleAsync();
        Assert.Equal("PEER_LATEST", row.PeerId);
        Assert.Equal(account.Id, row.AccountId);
    }
}
