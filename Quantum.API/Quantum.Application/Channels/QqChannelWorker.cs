using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quantum.Data;
using Quantum.Entities.Model;

namespace Quantum.Application.Channels;

/// <summary>QQ 官方 Gateway 托管服务；只接收 C2C 文本与短时绑定挑战，默认无账户/禁用时不连接。</summary>
public sealed class QqChannelWorker : BackgroundService
{
    private const int C2cIntent = 1 << 25;
    private readonly IServiceScopeFactory _scopes;
    private readonly ChannelNetwork _network;
    private readonly QqChannelClient _client;

    public QqChannelWorker(IServiceScopeFactory scopes, ChannelNetwork network, QqChannelClient client)
    {
        _scopes = scopes;
        _network = network;
        _client = client;
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
                    .FirstOrDefaultAsync(x => x.Platform == ChannelManagementService.Qq, stoppingToken);
                if (account?.CredentialCiphertext is not { Length: > 0 } || !ShouldConnect(account))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }
                accountId = account.Id;
                var crypto = scope.ServiceProvider.GetRequiredService<ChannelSecretProtector>();
                var credentials = crypto.Unprotect(account.CredentialCiphertext, account.Id, "qq-credentials");
                using var parsed = JsonDocument.Parse(credentials);
                var appId = QqChannelClient.Text(parsed.RootElement, "AppId");
                var secret = QqChannelClient.Text(parsed.RootElement, "AppSecret");
                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(secret)) throw new ChannelProtocolException("QQ_CREDENTIAL_INVALID");
                var apiBase = QqChannelClient.Text(parsed.RootElement, "ApiBase");
                var access = await _client.TokenAsync(appId, secret, apiBase, stoppingToken);
                var (gateway, reset) = await _client.GatewayAsync(access, apiBase, stoppingToken);
                await ClearErrorAsync(accountId, stoppingToken);
                if (reset > 0)
                {
                    await Task.Delay(reset, stoppingToken);
                    continue;
                }
                await PumpAsync(account.Id, account.BindingVersion, account.CredentialCiphertext, access, gateway, stoppingToken);
                backoff = 2;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // 不记录 URL、响应正文、token、微信上下文或收到的文本。端点状态仅保留脱敏错误码。
                try { await SetErrorAsync(accountId, error is ChannelProtocolException p ? p.ErrorCode : "QQ_CONNECTION_ERROR", stoppingToken); }
                catch { /* DB 异常不得使通道 Worker 连带停止主应用。 */ }
                try { await Task.Delay(TimeSpan.FromSeconds(backoff + Random.Shared.Next(0, 3)), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                backoff = Math.Min(backoff * 2, 120);
            }
        }
    }

    private static bool ShouldConnect(ChannelAccountModel account) => account.Enabled;

    private async Task PumpAsync(string accountId, int bindingVersion, string encryptedCredential,
        string token, Uri gateway, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        await ws.ConnectAsync(gateway, _network.WebSocketInvoker, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(1, 1);
        Task heartbeat = null;
        var ack = 0;
        string session;
        long seq;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
            var stored = await db.ChannelCursors.AsNoTracking().FirstOrDefaultAsync(x => x.AccountId == accountId, ct);
            session = stored?.QqSessionId;
            seq = stored?.QqSequence ?? -1;
        }
        var guard = WatchAccountAsync(accountId, bindingVersion, encryptedCredential, ws, linked.Token);
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var bytes = await ReceiveAsync(ws, ct);
                if (bytes is null) break;
                using var doc = JsonDocument.Parse(bytes);
                var frame = doc.RootElement;
                var op = QqChannelClient.Int(frame, "op");
                if (op == 10)
                {
                    var hello = QqChannelClient.Field(frame, "d");
                    var interval = hello is { } h ? QqChannelClient.Int(h, "heartbeat_interval") : null;
                    if (interval is null or <= 0) throw new ChannelProtocolException("QQ_HELLO_INVALID");
                    if (session is not null && seq >= 0)
                        await SendAsync(ws, gate, new { op = 6, d = new { token = "QQBot " + token, session_id = session, seq } }, ct);
                    else
                        await SendAsync(ws, gate, new { op = 2, d = new
                        {
                            token = "QQBot " + token, intents = C2cIntent, shard = new[] { 0, 1 },
                            properties = new Dictionary<string, string>
                            {
                                ["$os"] = Environment.OSVersion.Platform.ToString().ToLowerInvariant(),
                                ["$browser"] = "quantum-dotnet", ["$device"] = "quantum-dotnet"
                            }
                        } }, ct);
                    heartbeat = HeartbeatAsync(ws, gate, () => Interlocked.Read(ref seq),
                        () => Volatile.Read(ref ack), value => Interlocked.Exchange(ref ack, value),
                        Math.Clamp(interval.Value, 5000, 300000), linked.Token);
                    continue;
                }
                if (op == 11) { Interlocked.Exchange(ref ack, 0); continue; }
                if (op == 9)
                {
                    await ResetSessionAsync(accountId, ct);
                    ws.Abort();
                    return;
                }
                if (op == 7) { ws.Abort(); return; }
                if (op != 0) continue;
                var data = QqChannelClient.Field(frame, "d");
                var eventName = QqChannelClient.Text(frame, "t");
                var incomingSeq = QqChannelClient.Long(frame, "s");
                if (eventName == "READY" && data is { } ready)
                    session = QqChannelClient.Text(ready, "session_id") ?? throw new ChannelProtocolException("QQ_READY_INVALID");
                var batch = new List<ChannelIncoming>();
                if (eventName == "C2C_MESSAGE_CREATE" && data is { } msg)
                {
                    var peer = QqChannelClient.Field(msg, "author") is { } author
                        ? QqChannelClient.Text(author, "user_openid") : null;
                    var content = QqChannelClient.Text(msg, "content");
                    var idx = QqChannelClient.Field(msg, "message_scene") is { } scene &&
                              QqChannelClient.Field(scene, "ext") is { } ext
                        ? QqChannelClient.GetMessageIndex(ext) : null;
                    batch.Add(new ChannelIncoming("C2C_MESSAGE_CREATE", QqChannelClient.Text(msg, "id"), idx,
                        peer, content, IsText: QqChannelClient.Int(msg, "message_type") == 0,
                        Fingerprint: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
                }
                // 不持久化 event 正文；仅在 Inbox (含拒绝记录) 与新游标同一事务成功后推进内存 s。
                using (var scope = _scopes.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<ChannelInboxService>()
                        .AcceptBatchAsync(accountId, ChannelManagementService.Qq, batch,
                            qqSequence: incomingSeq, qqSessionId: session, ct: ct);
                if (incomingSeq.HasValue) Interlocked.Exchange(ref seq, incomingSeq.Value);
            }
        }
        finally
        {
            linked.Cancel();
            ws.Abort();
            try { await guard; } catch (OperationCanceledException) { }
            if (heartbeat is not null)
            {
                try { await heartbeat; }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }
        }
    }

    private async Task WatchAccountAsync(string accountId, int version, string credential,
        ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
            var state = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (state is null || !ShouldConnect(state) || state.BindingVersion != version || state.CredentialCiphertext != credential)
            {
                ws.Abort();
                return;
            }
        }
    }

    private async Task ResetSessionAsync(string accountId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
        await db.ChannelCursors.Where(x => x.AccountId == accountId)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.QqSessionId, (string)null)
                                      .SetProperty(c => c.QqSequence, (long?)null), ct);
    }

    private async Task SetErrorAsync(string accountId, string code, CancellationToken ct)
    {
        if (accountId is null || ct.IsCancellationRequested) return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
        await db.ChannelAccounts.Where(x => x.Id == accountId &&
            (x.Enabled || x.ChallengeHash != null && x.ChallengeExpiresAtUtc > DateTime.UtcNow))
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastErrorCode, code[..Math.Min(code.Length, 128)]), ct);
    }

    private async Task ClearErrorAsync(string accountId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
        await db.ChannelAccounts.Where(x => x.Id == accountId && x.LastErrorCode != null)
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastErrorCode, (string)null), ct);
    }

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || data.Length + result.Count > 65536)
                throw new ChannelProtocolException("QQ_FRAME_INVALID");
            data.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return data.ToArray();
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, SemaphoreSlim gate, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await gate.WaitAsync(ct);
        try { await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct); }
        finally { gate.Release(); }
    }

    private static async Task HeartbeatAsync(ClientWebSocket ws, SemaphoreSlim gate,
        Func<long> sequence, Func<int> pending, Action<int> setPending, int interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
        while (await timer.WaitForNextTickAsync(ct) && ws.State == WebSocketState.Open)
        {
            if (pending() != 0) { ws.Abort(); return; }
            setPending(1);
            await SendAsync(ws, gate, new { op = 1, d = sequence() < 0 ? (long?)null : sequence() }, ct);
        }
    }
}
