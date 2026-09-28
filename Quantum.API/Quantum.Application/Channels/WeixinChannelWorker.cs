using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quantum.Data;

namespace Quantum.Application.Channels;

/// <summary>微信扫码机器人 HTTPS 长轮询；仅唯一启用且已确认私聊账号入站，停止/换绑可取消轮询。</summary>
public sealed class WeixinChannelWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ChannelNetwork _network;

    public WeixinChannelWorker(IServiceScopeFactory scopes, ChannelNetwork network)
    {
        _scopes = scopes;
        _network = network;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            string accountId = null;
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
                var account = await db.ChannelAccounts.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Platform == ChannelManagementService.Weixin &&
                        x.Enabled, stoppingToken);
                if (account?.CredentialCiphertext is not { Length: > 0 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }
                accountId = account.Id;
                var protector = scope.ServiceProvider.GetRequiredService<ChannelSecretProtector>();
                using var credentials = JsonDocument.Parse(protector.Unprotect(account.CredentialCiphertext, account.Id, "weixin-credentials"));
                var botToken = QqChannelClient.Text(credentials.RootElement, "BotToken");
                var apiRoot = QqChannelClient.Text(credentials.RootElement, "ApiBase");
                string version, channelVersion;
                try
                {
                    // 留空按内置默认值兜底（channel_version 即官方插件版本号），不再因缺版本锁死通道。
                    (version, channelVersion) = WeixinQrLoginService.ResolveVersions(
                        QqChannelClient.Text(credentials.RootElement, "ClientVersion"),
                        QqChannelClient.Text(credentials.RootElement, "ChannelVersion"));
                }
                catch (Quantum.Utils.BusinessException) { throw new ChannelProtocolException("WEIXIN_VERSION_INVALID"); }
                if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(apiRoot))
                    throw new ChannelProtocolException("WEIXIN_CREDENTIAL_INVALID");
                ChannelNetwork.WeixinUri("", apiRoot); // 启动前校验地址，失败不带令牌请求。
                await PollAsync(account.Id, account.BotId, account.BindingVersion, account.CredentialCiphertext,
                    botToken, apiRoot, version, channelVersion, protector, stoppingToken);
                backoff = 2;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                // 管理端关闭/换绑触发本轮长轮询取消；不写游标，下一次从持久检查点恢复。
            }
            catch (Exception error)
            {
                var code = error is ChannelProtocolException protocol ? protocol.ErrorCode : "WEIXIN_POLL_ERROR";
                try
                {
                    await SetErrorAsync(accountId, code, stoppingToken);
                    if (code is "WEIXIN_SESSION_EXPIRED") await DisableAsync(accountId, stoppingToken);
                }
                catch { /* DB 异常不得影响 App/QQ 通道；下轮重试。 */ }
                try { await Task.Delay(TimeSpan.FromSeconds(backoff + Random.Shared.Next(0, 3)), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                backoff = Math.Min(backoff * 2, 120);
            }
        }
    }

    private async Task PollAsync(string accountId, string botId, int versionId, string encryptedCredential,
        string token, string apiRoot, string clientVersion, string channelVersion,
        ChannelSecretProtector protector, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var guard = WatchAccountAsync(accountId, versionId, encryptedCredential, linked, linked.Token);
        try
        {
            var cursor = "";
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
                var checkpoint = await db.ChannelCursors.AsNoTracking().FirstOrDefaultAsync(x => x.AccountId == accountId, linked.Token);
                if (checkpoint?.WeixinCursorCiphertext is { Length: > 0 })
                    cursor = protector.Unprotect(checkpoint.WeixinCursorCiphertext, accountId, "weixin-cursor");
            }
            var errorCleared = false;
            while (!linked.IsCancellationRequested)
            {
                JsonElement response;
                try
                {
                    response = await _network.PostAsync(ChannelNetwork.WeixinUri("ilink/bot/getupdates", apiRoot),
                        new { get_updates_buf = cursor, base_info = new { channel_version = channelVersion, bot_agent = "Quantum/1" } },
                        token: token, version: clientVersion, ct: linked.Token);
                }
                catch (ChannelProtocolException e) when (e.ErrorCode == "HTTP_412")
                {
                    throw new ChannelProtocolException("WEIXIN_POLL_HTTP_412");
                }
                EnsurePollAccepted(response);
                if (!errorCleared)
                {
                    await ClearErrorAsync(accountId, linked.Token);
                    errorCleared = true;
                }
                var list = QqChannelClient.Field(response, "msgs");
                if (list is not { ValueKind: JsonValueKind.Array }) throw new ChannelProtocolException("WEIXIN_MESSAGES_INVALID");
                var next = QqChannelClient.Text(response, "get_updates_buf");
                if (list.Value.GetArrayLength() > 0 && string.IsNullOrEmpty(next))
                    throw new ChannelProtocolException("WEIXIN_CURSOR_MISSING");
                var batch = new List<ChannelIncoming>();
                foreach (var msg in list.Value.EnumerateArray())
                {
                    var incoming = ParseIncoming(msg, botId);
                    batch.Add(incoming);
                }
                using (var scope = _scopes.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<ChannelInboxService>()
                        .AcceptBatchAsync(accountId, ChannelManagementService.Weixin, batch,
                            cursor: string.IsNullOrEmpty(next) ? null : next, ct: linked.Token);
                if (!string.IsNullOrEmpty(next)) cursor = next;
            }
        }
        finally
        {
            linked.Cancel();
            try { await guard; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// 判定一次长轮询响应是否被平台接受。
    /// 实测：`ilink/bot/getupdates` 的成功响应**不带 ret 字段**（只有扫码/二维码接口才带 `ret:0`），
    /// 把「字段缺失」当成失败会让通道在 HTTP 200 上永远转圈；形状由后续 msgs 校验兜底。
    /// </summary>
    internal static void EnsurePollAccepted(JsonElement response)
    {
        var ret = QqChannelClient.Int(response, "ret");
        var err = QqChannelClient.Int(response, "errcode");
        if (ret == -14 || err == -14) throw new ChannelProtocolException("WEIXIN_SESSION_EXPIRED");
        if (ret is not null and not 0 || err is not null and not 0)
            throw new ChannelProtocolException("WEIXIN_RET_" +
                ((ret is not null and not 0 ? ret : err)?.ToString() ?? "UNKNOWN"));
    }

    /// <summary>
    /// 兼容协议/客户端版本中 message_id 的 number/string 表示；不解析媒体或群事件为业务指令。
    ///
    /// 判据与本仓库**出站**报文对齐（<c>ChannelDeliveryWorker</c> 发的 1:1 文本用 <c>message_type=2</c>、
    /// 文本项 <c>type=1</c>）：旧实现要求 <c>message_type==1</c> 且 <c>item_list</c> 恰好 1 项，
    /// 真实微信私聊文本因此恒被判成 UnsupportedContent（连接正常却永远不回）。
    /// 拒收时只回传「结构指纹」（类型与条数），绝不携带正文。
    /// </summary>
    internal static ChannelIncoming ParseIncoming(JsonElement msg, string botId)
    {
        var peer = QqChannelClient.Text(msg, "from_user_id");
        var receiver = QqChannelClient.Text(msg, "to_user_id");
        var type = QqChannelClient.Int(msg, "message_type");
        var items = QqChannelClient.Field(msg, "item_list");
        string text = null;
        var kinds = new List<string>();
        if (items is { ValueKind: JsonValueKind.Array } list)
            foreach (var item in list.EnumerateArray())
            {
                var kind = QqChannelClient.Int(item, "type");
                kinds.Add(kind?.ToString() ?? "?");
                // 文本项不必是唯一一项：表情/引用/回复会带兄弟项，取第一个有内容的 type=1
                if (kind == 1 && text is null && QqChannelClient.Field(item, "text_item") is { } part)
                    text = QqChannelClient.Text(part, "text");
            }
        var idField = QqChannelClient.Field(msg, "message_id");
        var msgId = idField is { ValueKind: JsonValueKind.Number }
            ? idField.Value.GetRawText()
            : idField is { ValueKind: JsonValueKind.String } ? idField.Value.GetString() : null;
        var groupId = QqChannelClient.Text(msg, "group_id");
        // 2026-09-28 真机实测：微信 1:1 私聊报文**也带 group_id**（指纹 NotText:group 即为此），
        // 它不是群聊标识，故私聊判据改为「发给本机器人 + message_type 为 1/2 + 有文本项」。
        // 平台若将来真的下发群事件，仍会因 to_user_id 非本机器人或 context_token 缺失被拒；
        // 同时 group 关系留在指纹里，便于见到真实群报文后再补精确判据。
        var validDm = (type == 1 || type == 2) && receiver == botId;
        var isText = validDm && !string.IsNullOrWhiteSpace(text);
        // 指纹必须覆盖 IsText=false 的**全部**成因：此前只在「没解析出文本」时挂指纹，
        // 私聊判定不通过（to_user_id 非本机器人 / message_type 意外值）仍只剩一句
        // UnsupportedContent，现场表现为「连接正常、有拒收行、却无从定位」。只报结构与判据，绝不带正文。
        string hint = null;
        if (!isText)
        {
            var why = new List<string>();
            if (receiver != botId) why.Add("toNotBot");
            if (groupId is not null) why.Add(groupId == receiver ? "group==to" : groupId == peer ? "group==from" : "group");
            if (type != 1 && type != 2) why.Add("mtype=" + (type?.ToString() ?? "?"));
            if (string.IsNullOrWhiteSpace(text))
                why.Add($"items={kinds.Count},types=[{string.Join("|", kinds.Take(8))}]");
            hint = "NotText:" + string.Join(",", why);
            if (hint.Length > 78) hint = hint[..78];   // t_channel_inbox.RejectReason 为 nvarchar(80)
        }
        return new ChannelIncoming("WEIXIN_MESSAGE", msgId, "0", peer, text?.Trim(),
            QqChannelClient.Text(msg, "context_token"), isText,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(msg.GetRawText()))),
            hint);
    }

    private async Task WatchAccountAsync(string accountId, int version, string credential,
        CancellationTokenSource linked, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
            var account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null || !account.Enabled ||
                account.BindingVersion != version || account.CredentialCiphertext != credential)
            {
                linked.Cancel();
                return;
            }
        }
    }

    private async Task ClearErrorAsync(string accountId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
        await db.ChannelAccounts.Where(x => x.Id == accountId && x.LastErrorCode != null)
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastErrorCode, (string)null), ct);
    }

    private async Task SetErrorAsync(string accountId, string code, CancellationToken ct)
    {
        if (accountId is null || ct.IsCancellationRequested) return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
        await db.ChannelAccounts.Where(x => x.Id == accountId)
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastErrorCode, code[..Math.Min(code.Length, 128)]), ct);
    }

    private async Task DisableAsync(string accountId, CancellationToken ct)
    {
        if (accountId is null || ct.IsCancellationRequested) return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
        await db.ChannelAccounts.Where(x => x.Id == accountId).ExecuteUpdateAsync(x => x.SetProperty(a => a.Enabled, false), ct);
    }
}
