using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quantum.Application.Channels;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>飞书扫码绑定：设备码 poll 结果解析、凭据加密落库、候选登记与 Manager 二次确认。</summary>
public sealed class FeishuQrScanTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly ChannelSecretProtector _secret = new(RandomNumberGenerator.GetBytes(32));
    private readonly ChannelManagementService _service;

    public FeishuQrScanTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new ChannelManagementService(_db, _secret);
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ParsePoll_MapsDeviceCodeStates()
    {
        var success = FeishuQrLoginService.ParsePoll(Json(
            "{\"client_id\":\"cli_a\",\"client_secret\":\"sec\",\"user_info\":{\"open_id\":\"ou_user\"}}"));
        Assert.Equal("success", success.Status);
        Assert.Equal("cli_a", success.AppId);
        Assert.Equal("sec", success.AppSecret);
        Assert.Equal("ou_user", success.OpenId);
        // 扫码人 open_id 是可选字段：缺失不得影响凭据受理，只影响后续走挑战码回退。
        Assert.Null(FeishuQrLoginService.ParsePoll(Json(
            "{\"client_id\":\"cli_a\",\"client_secret\":\"sec\"}")).OpenId);
        Assert.Equal("failed", FeishuQrLoginService.ParsePoll(Json("{\"error\":\"access_denied\"}")).Status);
        Assert.Equal("expired", FeishuQrLoginService.ParsePoll(Json("{\"error\":\"expired_token\"}")).Status);
        // pending 等待态：飞书以 4xx + error=authorization_pending 返回，未识别 error 一律继续等待。
        Assert.Equal("waiting", FeishuQrLoginService.ParsePoll(Json("{\"error\":\"authorization_pending\"}")).Status);
        Assert.Equal("waiting", FeishuQrLoginService.ParsePoll(Json("{}")).Status);
    }

    [Fact]
    public void ParseBegin_RequiresDeviceCodeAndQrUrl_AndClampsTiming()
    {
        var (device, url, interval, expire) = FeishuQrLoginService.ParseBegin(Json(
            "{\"device_code\":\"dc\",\"verification_uri_complete\":\"https://accounts.feishu.cn/qa?u=1\"," +
            "\"interval\":1,\"expire_in\":99999}"));
        Assert.Equal("dc", device);
        Assert.Equal("https://accounts.feishu.cn/qa?u=1", url);
        Assert.Equal(2, interval);
        Assert.Equal(1800, expire);
        Assert.Throws<ChannelProtocolException>(() =>
            FeishuQrLoginService.ParseBegin(Json("{\"device_code\":\"dc\"}")));
        Assert.Throws<ChannelProtocolException>(() => FeishuQrLoginService.ParseBegin(Json("{}")));
    }

    [Fact]
    public void Init_MustAdvertiseClientSecret()
    {
        Assert.True(FeishuQrLoginService.SupportsClientSecret(Json("{\"supported_auth_methods\":[\"client_secret\"]}")));
        Assert.False(FeishuQrLoginService.SupportsClientSecret(Json("{\"supported_auth_methods\":[\"public_key\"]}")));
        Assert.False(FeishuQrLoginService.SupportsClientSecret(Json("{}")));
    }

    [Theory]
    [InlineData("ou_scanner")]
    [InlineData(null)]
    public async Task Scan_RegistersEncryptedCredentialsAndStartsWithoutConfirmation(string? openId)
    {
        Assert.Null(await _service.RegisterFeishuScanAsync("cli_a", "super-secret-value", openId));
        var account = await _db.ChannelAccounts.SingleAsync();
        Assert.Equal("cli_a", account.BotId);
        Assert.True(account.Enabled);
        Assert.Null(account.CandidatePeerId);
        Assert.Null(account.ChallengeHash);
        Assert.Empty(await _db.ChannelBindings.ToListAsync());
        Assert.DoesNotContain("super-secret-value", account.CredentialCiphertext);
        var plain = JsonDocument.Parse(_secret.Unprotect(account.CredentialCiphertext, account.Id, "feishu-credentials")).RootElement;
        Assert.Equal("cli_a", plain.GetProperty("AppId").GetString());
    }

    [Fact]
    public async Task Rescan_CancelsPendingOutboxAndRestartsImmediately()
    {
        await _service.RegisterFeishuScanAsync("cli_old", "old-secret", null);
        var account = await _db.ChannelAccounts.SingleAsync();
        var oldVersion = account.BindingVersion;
        var route = new ChannelReplyRouteModel { AccountId = account.Id, PeerId = "ou_old", BindingVersion = oldVersion,
            ReplyMessageId = "id", ContextTokenCiphertext = "encrypted", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) };
        _db.ChannelReplyRoutes.Add(route);
        _db.ChannelOutboxes.Add(new ChannelOutboxModel { AccountId = account.Id, ReplyRouteId = route.Id, PeerId = "ou_old",
            BindingVersion = oldVersion, MessageSequence = 1, Status = "Pending", Purpose = "Reply", Content = "test",
            ClientId = "test-client" });
        await _db.SaveChangesAsync();
        Assert.Null(await _service.RegisterFeishuScanAsync("cli_new", "new-secret", null));
        _db.ChangeTracker.Clear();
        Assert.Equal("Cancelled", (await _db.ChannelOutboxes.SingleAsync()).Status);
        Assert.Null((await _db.ChannelReplyRoutes.SingleAsync()).ContextTokenCiphertext);
        Assert.Equal("cli_new", (await _db.ChannelAccounts.SingleAsync()).BotId);
        Assert.True((await _db.ChannelAccounts.SingleAsync()).Enabled);
        Assert.True((await _db.ChannelAccounts.SingleAsync()).BindingVersion > oldVersion);
    }

    [Fact]
    public async Task RegisterScan_RejectsInvalidInput()
    {
        await Assert.ThrowsAsync<BusinessException>(() => _service.RegisterFeishuScanAsync("", "sec", "ou_x"));
        await Assert.ThrowsAsync<BusinessException>(() => _service.RegisterFeishuScanAsync("cli", "", "ou_x"));
        await Assert.ThrowsAsync<BusinessException>(() => _service.RegisterFeishuScanAsync("cli", "sec", new string('o', 129)));
    }
}
