using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quantum.Data;
using Quantum.Entities.Model;

namespace Quantum.Application.Channels;

/// <summary>原路文本回复 Outbox；单活 DB Claim + 绑定版本检查；未知回执永不自动重发。</summary>
public sealed class ChannelDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ChannelAccountGate _gate;
    private readonly ChannelSecretProtector _secret;
    private readonly QqChannelClient _qq;
    private readonly FeishuChannelClient _feishu;
    private readonly ChannelNetwork _network;

    public ChannelDeliveryWorker(IServiceScopeFactory scopes, ChannelAccountGate gate,
        ChannelSecretProtector secret, QqChannelClient qq, FeishuChannelClient feishu, ChannelNetwork network)
    {
        _scopes = scopes; _gate = gate; _secret = secret; _qq = qq; _feishu = feishu; _network = network;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动恢复不可独占主应用启动生命期：如果迁移/DB 暂时不可用，定时扫描会继续兜底。
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
            await db.ChannelOutboxes.Where(x => x.Status == "Sending")
                .ExecuteUpdateAsync(x => x.SetProperty(n => n.Status, "Unknown"), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch { /* 无数据库时保持 App 可用；下一轮继续退避。 */ }
        var lastUnknownSweep = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
                var now = DateTime.UtcNow;
                if (now - lastUnknownSweep > TimeSpan.FromMinutes(1))
                {
                    await db.ChannelOutboxes.Where(x => x.Status == "Sending" && x.ClaimedAtUtc < now.AddMinutes(-3))
                        .ExecuteUpdateAsync(x => x.SetProperty(n => n.Status, "Unknown"), stoppingToken);
                    lastUnknownSweep = now;
                }
                var candidate = await db.ChannelOutboxes.AsNoTracking()
                    .Where(x => (x.Status == "Pending" || x.Status == "Retry") && x.NextAttemptAtUtc <= now)
                    .OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(stoppingToken);
                if (candidate is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                var claimed = await db.ChannelOutboxes.Where(x => x.Id == candidate.Id &&
                    (x.Status == "Pending" || x.Status == "Retry"))
                    .ExecuteUpdateAsync(x => x.SetProperty(n => n.Status, "Sending")
                                              .SetProperty(n => n.ClaimedAtUtc, now)
                                              .SetProperty(n => n.Attempts, n => n.Attempts + 1), stoppingToken);
                if (claimed == 0) continue;
                await DeliverAsync(candidate.Id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch
            {
                // 工作器不能因单条异常退出：已 Claim 行按 Unknown 等待人工核查。
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task DeliverAsync(string id, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
        var outbox = await db.ChannelOutboxes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (outbox is null || outbox.Status != "Sending") return;
        var account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == outbox.AccountId, ct);
        if (account is null) { await Finish("Cancelled", "ACCOUNT_MISSING"); return; }
        using var held = await _gate.AcquireAsync(account.Platform, ct); // 管理端解绑/换绑在同平台实际发送期间等待。
        // 取得锁后重新查库：获锁前账户可能已换绑，绝不可沿用旧目标。
        account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == outbox.AccountId, ct);
        var route = await db.ChannelReplyRoutes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == outbox.ReplyRouteId, ct);
        if (account is null || !account.Enabled || route is null || route.AccountId != account.Id ||
            outbox.BindingVersion != account.BindingVersion || route.BindingVersion != account.BindingVersion ||
            outbox.PeerId != route.PeerId)
        {
            await Finish("Cancelled", "BINDING_CHANGED"); return;
        }
        if (route.ExpiresAtUtc <= DateTime.UtcNow)
        {
            await Finish("Expired", "REPLY_EXPIRED"); return;
        }
        try
        {
            string platformId;
            if (account.Platform == ChannelManagementService.Qq)
            {
                using var credentials = JsonDocument.Parse(_secret.Unprotect(account.CredentialCiphertext, account.Id, "qq-credentials"));
                var appId = QqChannelClient.Text(credentials.RootElement, "AppId");
                var appSecret = QqChannelClient.Text(credentials.RootElement, "AppSecret");
                var apiBase = QqChannelClient.Text(credentials.RootElement, "ApiBase");
                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret))
                    throw new ChannelProtocolException("QQ_CREDENTIAL_INVALID");
                var token = await _qq.TokenAsync(appId, appSecret, apiBase, ct);
                platformId = await _qq.SendReplyAsync(token, route.PeerId, outbox.Content,
                    route.ReplyMessageId, outbox.MessageSequence, apiBase, ct);
            }
            else if (account.Platform == ChannelManagementService.Weixin)
            {
                if (string.IsNullOrEmpty(route.ContextTokenCiphertext))
                    throw new ChannelProtocolException("WEIXIN_CONTEXT_MISSING");
                using var credentials = JsonDocument.Parse(_secret.Unprotect(account.CredentialCiphertext, account.Id, "weixin-credentials"));
                var token = QqChannelClient.Text(credentials.RootElement, "BotToken");
                var apiBase = QqChannelClient.Text(credentials.RootElement, "ApiBase");
                var context = _secret.Unprotect(route.ContextTokenCiphertext, account.Id, "context-token:" + route.Id);
                // 与扫码/收消息同一口径解析；缺失时用内置默认值，不再要求运行前必须配好环境变量。
                var (version, channelVersion) = WeixinQrLoginService.ResolveVersions(
                    QqChannelClient.Text(credentials.RootElement, "ClientVersion"),
                    QqChannelClient.Text(credentials.RootElement, "ChannelVersion"));
                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(apiBase))
                    throw new ChannelProtocolException("WEIXIN_SEND_CONFIG_INVALID");
                var msg = new
                {
                    from_user_id = "", to_user_id = route.PeerId, client_id = outbox.ClientId,
                    message_type = 2, message_state = 2, context_token = context,
                    item_list = new[] { new { type = 1, text_item = new { text = outbox.Content } } }
                };
                var response = await _network.PostAsync(ChannelNetwork.WeixinUri("ilink/bot/sendmessage", apiBase),
                    new { msg, base_info = new { channel_version = channelVersion, bot_agent = "Quantum/1" } },
                    token: token, version: version, ct: ct);
                var ret = QqChannelClient.Int(response, "ret");
                var err = QqChannelClient.Int(response, "errcode");
                // 与长轮询同一口径：sendmessage 成功也不带 ret，字段缺失不等于失败（见 WeixinChannelWorker.EnsurePollAccepted）
                if (ret is not null and not 0 || err is not null and not 0)
                    throw new ChannelProtocolException(ret == -14 || err == -14 ? "WEIXIN_SESSION_EXPIRED" : "WEIXIN_RET_" + (ret?.ToString() ?? err?.ToString()));
                platformId = null; // 该接口不保证返回最终用户可见的消息 Id。
            }
            else if (account.Platform == ChannelManagementService.Feishu)
            {
                using var credentials = JsonDocument.Parse(_secret.Unprotect(account.CredentialCiphertext, account.Id, "feishu-credentials"));
                var appId = FeishuChannelClient.Text(credentials.RootElement, "AppId");
                var appSecret = FeishuChannelClient.Text(credentials.RootElement, "AppSecret");
                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret))
                    throw new ChannelProtocolException("FEISHU_CREDENTIAL_INVALID");
                var token = await _feishu.TokenAsync(appId, appSecret, ct);
                // uuid 复用 ClientId：飞书同 uuid 一小时内至多成功一条，正好当投递幂等键
                var card = FeishuRichText.BuildCard("Quantum", FeishuRichText.ToLarkMd(outbox.Content));
                try
                {
                    platformId = await _feishu.SendCardAsync(token, route.PeerId, card, outbox.ClientId, ct);
                }
                catch (ChannelProtocolException)
                {
                    // 卡片构造/投递失败时降级为纯文本，绝不静默丢弃这条消息
                    platformId = await _feishu.SendTextAsync(token, route.PeerId, outbox.Content, outbox.ClientId, ct);
                }
            }
            else throw new ChannelProtocolException("PLATFORM_INVALID");
            outbox.PlatformMessageId = platformId?.Length > 191 ? platformId[..191] : platformId;
            outbox.AcceptedAtUtc = DateTime.UtcNow;
            await Finish("Accepted", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ChannelProtocolException e)
        {
            var errorCode = account.Platform == ChannelManagementService.Weixin && e.ErrorCode == "HTTP_412"
                ? "WEIXIN_SEND_HTTP_412" : e.ErrorCode;
            if (e.ErrorCode is "HTTP_429" && route.ExpiresAtUtc > DateTime.UtcNow.AddSeconds(30) && outbox.Attempts < 3)
            {
                outbox.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(30);
                await Finish("Retry", errorCode);
            }
            else
            {
                var unknown = e.ErrorCode is "NETWORK_ERROR" or "HTTP_500" or "HTTP_502" or "HTTP_503" or "HTTP_504";
                await Finish(unknown ? "Unknown" : "Failed", errorCode);
            }
            if (e.ErrorCode == "WEIXIN_SESSION_EXPIRED")
                await db.ChannelAccounts.Where(x => x.Id == account.Id)
                    .ExecuteUpdateAsync(x => x.SetProperty(a => a.Enabled, false).SetProperty(a => a.LastErrorCode, e.ErrorCode), ct);
        }
        catch { await Finish("Unknown", "SEND_UNCERTAIN"); }

        async Task Finish(string status, string code)
        {
            outbox.Status = status;
            outbox.LastErrorCode = code;
            await db.SaveChangesAsync(ct);
        }
    }
}
