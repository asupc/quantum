using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.Model;

namespace Quantum.Application.Channels;

/// <summary>
/// 仅由平台 Worker 构造，私聊事件经协议校验后进入单用户指令流。
/// RejectHint 由平台侧给出**不含正文**的结构指纹，用于让「判成非文本」这类拒收可定位。
/// </summary>
public sealed record ChannelIncoming(string EventType, string MessageId, string MessageIndex,
    string PeerId, string Text, string ContextToken = null, bool IsText = true, string Fingerprint = null,
    string RejectHint = null);

/// <summary>
/// 入站、游标与原路回复的单库原子提交；失败绝不推进游标。
/// 2026-10-12 机器人消息不同步 Web/App：入站不再落会话回显、不广播 WS 帧——指令交互只存在于
/// 平台私聊与 t_channel_inbox，Web/App 会话不再出现 channel:* 会话（历史行由启动清理删除）。
/// </summary>
public sealed class ChannelInboxService
{
    private readonly IQuantumDbContext _db;
    private readonly ChannelSecretProtector _secret;

    public ChannelInboxService(IQuantumDbContext db, ChannelSecretProtector secret)
    {
        _db = db;
        _secret = secret;
    }

    /// <summary>批次只有整笔持久化成功才保存 QQ s/微信游标。</summary>
    public async Task AcceptBatchAsync(string accountId, string platform, IReadOnlyList<ChannelIncoming> batch,
        string cursor = null, long? qqSequence = null, string qqSessionId = null, CancellationToken ct = default)
    {
        if (batch is null || batch.Count > 100) throw new InvalidOperationException("平台入站批次过大");
        if (cursor is { Length: > 8192 }) throw new InvalidOperationException("平台游标过长，拒绝推进");
        if (cursor is { Length: 0 }) cursor = null;
        var account = await _db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId && x.Platform == platform, ct);
        if (account is null) return;
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        foreach (var incoming in batch)
        {
            if (incoming is null) continue;
            var reason = Validate(incoming, account);
            var msgId = string.IsNullOrWhiteSpace(incoming.MessageId)
                ? "missing:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(incoming.Fingerprint ?? "")))[..32]
                : incoming.MessageId;
            var index = string.IsNullOrWhiteSpace(incoming.MessageIndex) ? "0" : incoming.MessageIndex;
            if (msgId.Length > 191 || index.Length > 160) reason = "IdentifierTooLong";
            if (msgId.Length > 191) msgId = "oversized:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(msgId)))[..32];
            if (index.Length > 160) index = "oversized:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index)))[..32];
            var eventType = incoming.EventType is { Length: > 0 } ? incoming.EventType[..Math.Min(incoming.EventType.Length, 32)] : "unknown";
            if (await _db.ChannelInboxes.AnyAsync(x => x.AccountId == accountId && x.EventType == eventType &&
                    x.MessageId == msgId && x.MessageIndex == index, ct)) continue;
            var inbox = new ChannelInboxModel
            {
                AccountId = accountId, EventType = eventType,
                MessageId = msgId, MessageIndex = index,
                PeerId = incoming.PeerId is { Length: <= 128 } ? incoming.PeerId : null,
                Content = reason is null ? incoming.Text.Trim() : null,
                Status = reason is null ? "Received" : "Rejected",
                RejectReason = reason,
                ReceivedAtUtc = now
            };
            _db.ChannelInboxes.Add(inbox);
            if (reason != null) continue;

            // 每条平台私聊自带原路回复目标；账户版本使重配后的旧消息/路由立即失效。
            var route = new ChannelReplyRouteModel
            {
                AccountId = accountId, PeerId = incoming.PeerId, BindingVersion = account.BindingVersion,
                ReplyMessageId = msgId, ExpiresAtUtc = now.Add(ChannelManagementService.ReplyWindow(platform))
            };
            if (platform == ChannelManagementService.Weixin)
                route.ContextTokenCiphertext = _secret.Protect(incoming.ContextToken, accountId, "context-token:" + route.Id);
            _db.ChannelReplyRoutes.Add(route);
            inbox.ReplyRouteId = route.Id;
            // 2026-10-12 起入站不再落会话回显：指令由 ChannelCommandWorker 消费 Received 行
            // 并按原路路由回复平台，Web/App 会话不产生任何行。
            await _db.SaveChangesAsync(ct);
        }
        if (cursor is not null || qqSequence.HasValue)
        {
            var checkpoint = await _db.ChannelCursors.FirstOrDefaultAsync(x => x.AccountId == accountId, ct);
            if (checkpoint is null)
            {
                checkpoint = new ChannelCursorModel { AccountId = accountId };
                _db.ChannelCursors.Add(checkpoint);
            }
            if (cursor is not null)
                checkpoint.WeixinCursorCiphertext = _secret.Protect(cursor, accountId, "weixin-cursor");
            if (qqSequence.HasValue)
            {
                checkpoint.QqSequence = qqSequence;
                checkpoint.QqSessionId = qqSessionId;
            }
            checkpoint.UpdatedAtUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string Validate(ChannelIncoming msg, ChannelAccountModel account)
    {
        if (string.IsNullOrWhiteSpace(msg.MessageId)) return "MissingId";
        if (msg.EventType is null or { Length: > 32 }) return "UnsupportedEvent";
        if (!account.Enabled || account.CredentialCiphertext is not { Length: > 0 }) return "Disabled";
        if (string.IsNullOrWhiteSpace(msg.PeerId) || msg.PeerId.Length > 128) return "InvalidPeer";
        if (account.Platform == ChannelManagementService.Weixin && msg.EventType != "WEIXIN_MESSAGE" ||
            account.Platform == ChannelManagementService.Qq && msg.EventType != "C2C_MESSAGE_CREATE" ||
            account.Platform == ChannelManagementService.Feishu && msg.EventType != "im.message.receive_v1") return "UnsupportedEvent";
        if (!msg.IsText || string.IsNullOrWhiteSpace(msg.Text) || msg.Text.Length > 2000)
            // Worker 可给出「只含结构、不含正文」的指纹，否则一个 UnsupportedContent 无法区分是形态不符还是权限问题
            return msg.RejectHint ?? "UnsupportedContent";
        if (account.Platform == ChannelManagementService.Weixin && (string.IsNullOrWhiteSpace(msg.ContextToken) || msg.ContextToken.Length > 8192))
            return "MissingContextToken";
        return null;
    }
}
