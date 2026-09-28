using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

/// <summary>入站、游标、原路回复及 App 气泡的单库原子提交；失败绝不推进游标。</summary>
public sealed class ChannelInboxService
{
    private readonly IQuantumDbContext _db;
    private readonly AppMessageService _messages;
    private readonly AppWebSocketManager _websocket;
    private readonly ChannelSecretProtector _secret;

    public ChannelInboxService(IQuantumDbContext db, AppMessageService messages, AppWebSocketManager websocket,
        ChannelSecretProtector secret, ChannelQuickReplyService quickReplies = null)
    {
        _db = db;
        _messages = messages;
        _websocket = websocket;
        _secret = secret;
    }

    /// <summary>批次只有整笔持久化成功才保存 QQ s/微信游标，返回本次产生的会话消息数。</summary>
    public async Task<int> AcceptBatchAsync(string accountId, string platform, IReadOnlyList<ChannelIncoming> batch,
        string cursor = null, long? qqSequence = null, string qqSessionId = null, CancellationToken ct = default)
    {
        if (batch is null || batch.Count > 100) throw new InvalidOperationException("平台入站批次过大");
        if (cursor is { Length: > 8192 }) throw new InvalidOperationException("平台游标过长，拒绝推进");
        if (cursor is { Length: 0 }) cursor = null;
        var account = await _db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId && x.Platform == platform, ct);
        if (account is null) return 0;
        var now = DateTime.UtcNow;
        var accepted = 0;
        var frames = new List<string>();
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
            // 第一条落库后再为第二条分配 Seq，两个消息及 Outbox 与 Inbox/游标仍在同一事务内。
            var sessionKey = "channel:" + platform.ToLowerInvariant();
            var title = ChannelManagementService.DisplayName(platform);
            var inbound = await _messages.AppendNoSaveAsync(ChatMessageDirection.接收, inbox.Content,
                status: ChatMessageStatus.已读, msgId: inbox.Id, sessionKey: sessionKey, sessionTitle: title);
            await _db.SaveChangesAsync(ct);
            // 指令由 ChannelCommandWorker 消费 Received 行，不在网络批次中执行脚本。
            frames.Add(Frame(inbound));
            accepted++;
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
        foreach (var frame in frames)
        {
            try { await _websocket.SendToAllAsync(frame, ct); }
            catch { /* 落库已完成，App/Web 重连走消息增量补拉。 */ }
        }
        return accepted;
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

    private static string Frame(ChatMessageModel message) => JsonSerializer.Serialize(new
    {
        type = "message", msgId = message.MsgId, seq = message.Seq,
        content = message.Content, contentType = message.ContentType,
        direction = (int)message.Direction, session = message.SessionKey, sessionTitle = message.SessionTitle,
        createTime = message.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")
    }, AppPushService.FrameJsonOptions);
}
