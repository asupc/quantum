using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// G-Push 外部受限富文本推送回归：专用凭据鉴权隔离、富文本与标题校验、
/// external: 会话命名空间归组与跨接入方隔离、幂等去重与配额。
/// 覆盖审核项 R-06（提交后 best-effort）、R-07（幂等窗口口径）、R-10（纯文本 URL 绕过）、
/// R-11（三处字段名一致）、R-12（标题归一化）。
/// </summary>
public class ExternalPushTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly ExternalPushCredentialService _credentials;
    private readonly ExternalPushService _push;

    public ExternalPushTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _credentials = new ExternalPushCredentialService(_db);
        var messages = new AppMessageService(_db, NullLogger<AppMessageService>.Instance);
        var appPush = new AppPushService(_db, messages,
            new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance),
            NullLogger<AppPushService>.Instance);
        _push = new ExternalPushService(_db, appPush, NullLogger<ExternalPushService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<(ExternalPushCredentialModel Credential, string Secret)> NewCredential(bool enabled = true,
        string name = "机房监控", int perMinute = 60)
    {
        var created = await _credentials.CreateAsync(name, perMinute, 1000, null, null);
        if (enabled)
        {
            await _credentials.SetEnabledAsync(created.Credential.Id, true, "test");
            created.Credential.Enabled = true;
        }

        return (created.Credential, created.Secret);
    }

    private string Header(ExternalPushCredentialModel credential, string secret)
        => $"PushKey {credential.Id}.{secret}";

    // ------------------------------------------------------------ 凭据

    [Fact]
    public async Task Create_KeepsOnlyHash_AndDefaultsToDisabled()
    {
        var created = await _credentials.CreateAsync("接入方A", 0, 0, null, null);
        var credential = created.Credential;
        var secret = created.Secret;

        Assert.False(credential.Enabled);
        Assert.Equal(60, credential.RateLimitPerMinute);
        Assert.Equal(1000, credential.DailyQuota);
        // 库里存的是 64 位十六进制摘要，绝不存明文
        Assert.Equal(64, credential.SecretHash.Length);
        Assert.DoesNotContain(secret, credential.SecretHash);
        var stored = await _db.ExternalPushCredentials.AsNoTracking().SingleAsync(n => n.Id == credential.Id);
        Assert.DoesNotContain(secret, stored.SecretHash);
        Assert.DoesNotContain(secret, stored.DisplayName);
        Assert.DoesNotContain(secret, stored.Remark ?? "");
    }

    [Fact]
    public async Task Verify_AcceptsOnlyValidLivePushKey()
    {
        var (credential, secret) = await NewCredential();

        var ok = await _credentials.VerifyAsync(Header(credential, secret));
        Assert.NotNull(ok);
        Assert.Equal(credential.Id, ok.Id);

        Assert.Null(await _credentials.VerifyAsync(null));
        Assert.Null(await _credentials.VerifyAsync(""));
        // 旧 Open AppKey / JWT 形态一律不认
        Assert.Null(await _credentials.VerifyAsync("Bearer eyJhbGciOi.something.else"));
        Assert.Null(await _credentials.VerifyAsync($"PushKey {credential.Id}.wrong-secret-wrong-secret-xx"));
        Assert.Null(await _credentials.VerifyAsync("PushKey nodotseparator"));
        Assert.Null(await _credentials.VerifyAsync($"PushKey absent-credential.{secret}"));
    }

    [Fact]
    public async Task Verify_RejectsDisabledAndExpired()
    {
        var (disabled, disabledSecret) = await NewCredential(enabled: false);
        Assert.Null(await _credentials.VerifyAsync(Header(disabled, disabledSecret)));

        await _credentials.SetEnabledAsync(disabled.Id, true, "test");
        Assert.NotNull(await _credentials.VerifyAsync(Header(disabled, disabledSecret)));

        var expiring = await _credentials.CreateAsync("临时接入", 60, 100, null, DateTime.UtcNow.AddMinutes(-1));
        await _credentials.SetEnabledAsync(expiring.Credential.Id, true, "test");
        Assert.Null(await _credentials.VerifyAsync(Header(expiring.Credential, expiring.Secret)));
    }

    [Fact]
    public async Task Rotate_OldSecretStopsWorking()
    {
        var (credential, oldSecret) = await NewCredential();
        var newSecret = await _credentials.RotateAsync(credential.Id);

        Assert.NotEqual(oldSecret, newSecret);
        Assert.Null(await _credentials.VerifyAsync(Header(credential, oldSecret)));
        Assert.NotNull(await _credentials.VerifyAsync(Header(credential, newSecret)));
    }

    // ------------------------------------------------------------ 富文本校验（R-10）

    [Theory]
    [InlineData("纯文本没有标记")]
    [InlineData("{{red|紧急}} 温度过高")]
    [InlineData("{{tag:orange|已恢复}} 全部节点正常")]
    [InlineData("{{link:查看详情|https://example.com/status}}")]
    [InlineData("第一行\n第二行 {{blue|普通}}")]
    public async Task Content_AcceptsProjectRichTextGrammar(string content)
    {
        var (credential, secret) = await NewCredential();
        _ = secret;

        var outcome = await _push.SendAsync(credential, "标题", "会话", content, "abcdef123456", 64);

        Assert.False(outcome.Duplicate);
    }

    [Theory]
    [InlineData("裸链接 http://example.com/x")]
    [InlineData("裸链接 https://example.com/x")]
    [InlineData("访问 www.example.com 看看")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("{{link:x|http://example.com}}")]
    [InlineData("{{link:x|javascript:alert(1)}}")]
    [InlineData("{{link:x|data:text/html;base64,AA}}")]
    [InlineData("{{link:x|https://user:pw@example.com}}")]
    [InlineData("{{rainbow|彩}}")]
    [InlineData("{{red|未闭合")]
    [InlineData("{{red}}")]
    public async Task Content_RejectsBypassAndUnknownForms(string content)
    {
        var (credential, secret) = await NewCredential();
        _ = secret;

        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, "标题", "会话", content, "abcdef123456", 64));
    }

    [Fact]
    public async Task Content_RejectsOverMarkerAndLengthLimits()
    {
        var (credential, _) = await NewCredential();
        var tooMany = string.Join(' ', Enumerable.Range(0, ExternalPushService.MaxMarkers + 1)
            .Select(n => "{{red|" + n + "}}"));
        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, "标题", "会话", tooMany, "key-12345678", 64));

        var tooLong = new string('x', ExternalPushService.MaxContentLength + 1);
        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, "标题", "会话", tooLong, "key-12345678", 64));
    }

    [Theory]
    [InlineData("含\n换行的标题")]
    [InlineData("含\r回车的标题")]
    public async Task Title_RejectsControlCharsAndOverlong(string bad)
    {
        var (credential, _) = await NewCredential();

        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, bad, "会话", "正文", "key-12345678", 64));
    }

    [Fact]
    public async Task Title_RejectsOver100Chars()
    {
        var (credential, _) = await NewCredential();

        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, new string('标', 101), "会话", "正文", "key-12345678", 64));
    }

    [Fact]
    public async Task SessionTitle_RejectsTooLongAndBlankOnly()
    {
        var (credential, _) = await NewCredential();

        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, "标题", new string('标', 81), "正文", "key-12345678", 64));
        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, "", "会话", "正文", "key-12345678", 64));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("含 空 格 的 键")]
    [InlineData("含中文键key")]
    public async Task IdempotencyKey_RejectsBadShapes(string key)
    {
        var (credential, _) = await NewCredential();

        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, "标题", "会话", "正文", key, 64));
    }

    // ------------------------------------------------------------ 会话命名空间

    [Fact]
    public void SessionKey_IsStableNormalized_AndIsolatedAcrossCredentials()
    {
        var same = ExternalPushService.BuildSessionKey("C1", "RoomA");
        var trimmed = ExternalPushService.BuildSessionKey("C1", "  RoomA  ");
        var other = ExternalPushService.BuildSessionKey("C2", "RoomA");
        var cased = ExternalPushService.BuildSessionKey("C1", "rooma");

        Assert.Equal(same, trimmed);
        Assert.NotEqual(same, other);
        Assert.StartsWith(ExternalPushService.SessionPrefix, same);
        // 标题明文不进会话键
        Assert.DoesNotContain("RoomA", same);
        // 大小写敏感：不同标题即不同会话（NFKC 只折叠兼容形态，不做大小写归一）
        Assert.NotEqual(same, cased);
        Assert.Equal(ExternalPushService.NormalizeTitle("Ｒｏｏｍ"), ExternalPushService.NormalizeTitle("Room"));
    }

    [Fact]
    public async Task SameTitle_SameCredential_LandsInOneSession()
    {
        var (credential, _) = await NewCredential();

        var first = await _push.SendAsync(credential, "告警A", "机房监控", "正文一", "key-aaaaaaaa", 64);
        var second = await _push.SendAsync(credential, "告警B", "机房监控", "正文二", "key-bbbbbbbb", 64);
        var third = await _push.SendAsync(credential, "告警C", "另一个会话", "正文三", "key-cccccccc", 64);

        Assert.Equal(first.SessionKey, second.SessionKey);
        Assert.NotEqual(first.SessionKey, third.SessionKey);
        var session = await _db.ChatSessions.AsNoTracking().SingleAsync(n => n.SessionKey == first.SessionKey);
        Assert.Equal("机房监控", session.DisplayTitle);
    }

    [Fact]
    public async Task MessageCarriesSessionTitleSnapshot_ForOfflineCatchUp()
    {
        var (credential, _) = await NewCredential();

        var outcome = await _push.SendAsync(credential, "标题", "机房监控", "正文", "key-dddddddd", 64);

        // REST 增量补拉直接序列化实体：字段名必须就是 SessionTitle（R-11）
        var message = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.MsgId == outcome.MsgId);
        Assert.Equal("机房监控", message.SessionTitle);
        Assert.Equal(ExternalPushService.NormalizeTitle("机房监控"), message.SessionTitle);
        var notification = await _db.AppNotifications.AsNoTracking().SingleAsync(n => n.MsgId == outcome.MsgId);
        Assert.Equal("system", notification.Category);
        // 通知标题不折叠会话标题（否则会话列表与通知横幅双重前缀）
        Assert.Equal("标题", notification.Title);
    }

    [Fact]
    public async Task PrefixConflict_IsDetectedBeforeGoLive()
    {
        _db.Tasks.Add(new TaskModel { Id = "T1", Name = "n", FileName = "a.cs", SessionName = "external:abc" });
        await _db.SaveChangesAsync();

        var conflicts = await _push.FindSessionPrefixConflictsAsync();

        Assert.Contains("T1", conflicts);
    }

    [Fact]
    public async Task TaskSessionName_CannotOccupyExternalPrefix()
    {
        var service = new TaskService(_db,
            new AppMessageService(_db, NullLogger<AppMessageService>.Instance),
            new ScriptVersionService(_db));

        await Assert.ThrowsAsync<BusinessException>(() => service.ValidateSessionNameAsync("external:任务串台"));
        await service.ValidateSessionNameAsync("普通会话名");
    }

    // ------------------------------------------------------------ 幂等与配额

    [Fact]
    public async Task SameKeySameContent_ReturnsOriginalResult_WithoutSecondBroadcast()
    {
        var (credential, _) = await NewCredential();

        var first = await _push.SendAsync(credential, "标题", "会话", "正文", "key-eeeeeeee", 64);
        var replay = await _push.SendAsync(credential, "标题", "会话", "正文", "key-eeeeeeee", 64);

        Assert.True(replay.Duplicate);
        Assert.Equal(first.MsgId, replay.MsgId);
        Assert.Equal(first.SessionKey, replay.SessionKey);
        Assert.Equal(1, await _db.AppNotifications.CountAsync());
    }

    [Fact]
    public async Task SameKeyDifferentContent_IsRejected()
    {
        var (credential, _) = await NewCredential();
        await _push.SendAsync(credential, "标题", "会话", "正文", "key-ffffffff", 64);

        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, "标题", "会话", "另一个正文", "key-ffffffff", 64));
    }

    [Fact]
    public async Task Quota_EnforcedPerMinute()
    {
        var (credential, _) = await NewCredential(perMinute: 2);

        await _push.SendAsync(credential, "标题1", "会话", "正文1", "key-gggggggg1", 64);
        await _push.SendAsync(credential, "标题2", "会话", "正文2", "key-gggggggg2", 64);

        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, "标题3", "会话", "正文3", "key-gggggggg3", 64));
    }

    [Fact]
    public async Task RequestBytes_OverGate_IsRejected()
    {
        var (credential, _) = await NewCredential();

        await Assert.ThrowsAsync<BusinessException>(() =>
            _push.SendAsync(credential, "标题", "会话", "正文", "key-hhhhhhhh", ExternalPushService.MaxRequestBytes + 1));
    }

    [Fact]
    public async Task IdempotencyRow_SurvivesUntilPruned_ThenReusable()
    {
        var (credential, _) = await NewCredential();
        await _push.SendAsync(credential, "标题", "会话", "正文", "key-iiiiiiii", 64);

        Assert.Equal(0, await _push.PruneIdempotencyRecordsAsync());
        await _db.ExternalPushRequests.Where(n => n.CredentialId == credential.Id)
            .ExecuteUpdateAsync(n => n.SetProperty(p => p.CreatedAtUtc, DateTime.UtcNow.AddHours(-80)));

        Assert.Equal(1, await _push.PruneIdempotencyRecordsAsync());
        // 物理清理之后，同键才允许按新请求受理
        var outcome = await _push.SendAsync(credential, "标题", "会话", "正文", "key-iiiiiiii", 64);
        Assert.False(outcome.Duplicate);
    }
}
