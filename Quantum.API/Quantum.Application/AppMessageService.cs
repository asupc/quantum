using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using System.Linq.Expressions;

namespace Quantum.Application;

/// <summary>
/// App 会话消息服务：t_chat_message 落库（Seq 全局单调递增）、增量同步、已读回执、未读数、历史检索、
/// 会话列表快照与单会话分页（多端会话视图）。
/// 会话生命周期（2026-09-18 会话分组归并批次）：t_chat_session 让会话具备独立存在性——
/// 消息落库同事务 upsert 会话行（删除会话只删会话行、消息行保留——2026-09-21 日志删除逻辑调整）、
/// 改名时消息行与会话行一并迁移。
/// </summary>
public class AppMessageService
{
    private readonly IQuantumDbContext _dbContext;
    private readonly ILogger<AppMessageService> _logger;

    /// <summary>
    /// 同步默认页大小（App 未传 limit 时）
    /// </summary>
    public const int SyncPageSize = 100;

    /// <summary>
    /// 通知镜像行的会话内容类型标记：客户端据此把该行识别为站内通知并渲染通知卡片
    /// （旧客户端遇到未知类型按文本渲染，向后兼容）。分类/标题/jump 仍以 t_app_notification 为准。
    /// </summary>
    public const string NotifyContentType = "notify";

    /// <summary>
    /// Seq 撞车（并发分配 MAX+1）后的重试次数：撞一次通常是并发写，连续撞三次说明有热点写入需人工介入。
    /// </summary>
    private const int SeqConflictRetries = 3;

    /// <summary>
    /// 已读水位上报字典的条目上限（2026-09-21 双端同步批次）：sessions/read 与 sessions/overview
    /// 共用同一防护——异常超大字典的逐会话条件 UPDATE 有压力面，超限直接拒绝。
    /// </summary>
    public const int MaxReadWatermarkEntries = 1000;

    public AppMessageService(IQuantumDbContext dbContext, ILogger<AppMessageService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// 追加一条会话消息：事务内分配全局 Seq = MAX+1（多设备并发发送撞车时重试）。
    /// </summary>
    /// <param name="direction">方向（发送 = 机器人 → 用户；接收 = 用户 → 机器人）</param>
    /// <param name="content">内容（文本/图片URL/文件URL）</param>
    /// <param name="contentType">text/image/file/notify</param>
    /// <param name="status">初始投递状态（机器人下发默认发送中；用户上报直接已读）</param>
    /// <param name="msgId">指定消息id（通知镜像进会话流时与 t_app_notification 共用，保证客户端幂等；可空自动生成）</param>
    /// <param name="contentText">配文（image/file 消息附带的文字说明，可空）</param>
    /// <param name="payload">结构化富交互载荷（可点选项/视频封面，JSON，可空）</param>
    public async Task<ChatMessageModel> AppendAsync(ChatMessageDirection direction, string content,
        string contentType = "text", ChatMessageStatus status = ChatMessageStatus.发送中, string msgId = null,
        string contentText = null, string sessionKey = null, string payload = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            ChatMessageModel message = null;
            try
            {
                await using var tx = await _dbContext.Database.BeginTransactionAsync();
                message = await AppendNoSaveAsync(direction, content, contentType, status, msgId, contentText, sessionKey, payload);
                await _dbContext.SaveChangesAsync();
                await tx.CommitAsync();
                return message;
            }
            catch (DbUpdateException e) when (attempt < SeqConflictRetries)
            {
                // 并发分配 Seq 撞车：必须先把失败的实体脱离跟踪再重试——它仍处于 Added 状态，
                // 直接重试会让两次插入一起提交（旧实现的重试因此从未真正生效）
                if (message != null)
                {
                    _dbContext.Entry(message).State = EntityState.Detached;
                }
                // AppendNoSaveAsync 同批 upsert 的会话行（t_chat_session）同样可能处于 Added 状态，
                // 一并脱离跟踪——否则重试会把上一次的会话行插入与本次一起提交（含撞主键风险）
                DetachPendingSessionRows();
                _logger.LogWarning(e, "Chat message seq conflict, retrying ({Attempt}/{Max})", attempt + 1, SeqConflictRetries);
            }
        }
    }

    /// <summary>
    /// 构造并挂入一条会话消息（只 Add 进 DbContext，不 SaveChanges、不管理事务），Seq 取当前 MAX+1。
    /// 事务边界交给调用方：需要「通知行 + 会话镜像」同事务提交时，由 AppPushService 在同一事务内调用。
    /// </summary>
    public async Task<ChatMessageModel> AppendNoSaveAsync(ChatMessageDirection direction, string content,
        string contentType = "text", ChatMessageStatus status = ChatMessageStatus.发送中, string msgId = null,
        string contentText = null, string sessionKey = null, string payload = null)
    {
        var max = await _dbContext.ChatMessages.AsNoTracking()
            .MaxAsync(n => (long?)n.Seq) ?? 0;
        var message = new ChatMessageModel
        {
            Id = Guid.NewGuid().ToString().Replace("-", ""),
            Seq = max + 1,
            Direction = direction,
            Content = content?.RemoveEmoji(),
            ContentType = string.IsNullOrEmpty(contentType) ? "text" : contentType,
            ContentText = string.IsNullOrWhiteSpace(contentText) ? null : contentText.Trim().RemoveEmoji(),
            MsgId = string.IsNullOrEmpty(msgId) ? Guid.NewGuid().ToString() : msgId,
            Status = status,
            SessionKey = string.IsNullOrWhiteSpace(sessionKey) ? null : sessionKey.Trim(),
            // 结构化载荷为脚本组装的 JSON（选项/封面），不做 RemoveEmoji——客户端解析失败自会降级
            Payload = string.IsNullOrWhiteSpace(payload) ? null : payload.Trim(),
            CreateTime = DateTime.Now
        };
        _dbContext.ChatMessages.Add(message);
        // 会话行 upsert（§2.7，与消息同一 SaveChanges/事务提交，保证「消息在则会话在」）：
        // 建行（SessionKey 归一空串 = 默认会话；消息表默认会话存 null，两侧表示不同）或推进 LastSeq = 本条 Seq。
        // 外发（AppPushService）、通知镜像、入站回显三条落库路径共用本函数，一处挂点全覆盖。
        var normalizedKey = message.SessionKey ?? "";
        var session = await _dbContext.ChatSessions.FindAsync(normalizedKey);
        if (session == null)
        {
            // 建行预置已读水位（2026-09-21 日志删除逻辑调整）：会话删除不再连带删消息行，
            // 重建会话行时若 LastReadSeq 落 0，保留下来的全部历史消息会在重建瞬间整会话计未读；
            // 预置为该会话现有消息最大 Seq（本条 Seq 恒更大，Min 只防并发写入下的倒挂），
            // 未读仅剩本条新消息。全新会话（无历史消息）existingMax=0，行为与旧实现一致。
            var existingMax = await _dbContext.ChatMessages.AsNoTracking()
                .Where(n => normalizedKey.Length == 0
                    ? (n.SessionKey == null || n.SessionKey == "")
                    : n.SessionKey == normalizedKey)
                .MaxAsync(n => (long?)n.Seq) ?? 0;
            _dbContext.ChatSessions.Add(new ChatSessionModel
            {
                SessionKey = normalizedKey,
                CreateTime = message.CreateTime,
                LastSeq = message.Seq,
                LastReadSeq = Math.Min(existingMax, message.Seq)
            });
        }
        else if (message.Seq > session.LastSeq)
        {
            session.LastSeq = message.Seq;
        }
        return message;
    }

    /// <summary>
    /// 把处于 Added 状态的会话行脱离跟踪：Seq 撞车整事务重试前的清理
    /// （AppPushService.SaveNotificationWithMirrorAsync 的重试同样调用）。
    /// </summary>
    internal void DetachPendingSessionRows()
    {
        // IQuantumDbContext 未暴露 ChangeTracker；实现恒为 DbContext（QuantumSqlite/MySqlDbContext），直转安全
        foreach (var entry in ((DbContext)_dbContext).ChangeTracker.Entries<ChatSessionModel>()
                     .Where(n => n.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// 增量同步：取 Seq &gt; afterSeq 的会话消息（升序，最多 limit 条）；
    /// 其中机器人下发的「发送中」消息顺带标记为已送达（App 主动拉取即视为送达）。
    /// 返回消息页与本次同步应推进到的游标。
    /// </summary>
    /// <remarks>
    /// 游标只随「实取到的消息」推进：本页非空时取本页最大 Seq，空页时原样返回 afterSeq。
    /// 空页**不能**回落到 GetMaxSeqAsync()——分页查询与 MAX 查询不在同一快照内，两者之间落库的新消息
    /// 会让客户端把游标推到它之后，而该消息尚未被任何一页返回，此后重连补拉也永远拉不回（永久不可见）。
    /// 分页取满 limit 条时同样安全：客户端会带本页最大 Seq 再拉下一页。
    /// </remarks>
    public async Task<(List<ChatMessageModel> messages, long maxSeq)> SyncAsync(long afterSeq, int limit = SyncPageSize)
    {
        limit = Math.Clamp(limit <= 0 ? SyncPageSize : limit, 1, 500);
        var messages = await _dbContext.ChatMessages.AsNoTracking()
            .Where(n => n.Seq > afterSeq)
            .OrderBy(n => n.Seq)
            .Take(limit)
            .ToListAsync();
        var pendingIds = messages
            .Where(n => n.Direction == ChatMessageDirection.发送 && n.Status == ChatMessageStatus.发送中)
            .Select(n => n.Id)
            .ToList();
        if (pendingIds.Count > 0)
        {
            await _dbContext.ChatMessages
                .Where(n => pendingIds.Contains(n.Id) && n.Status == ChatMessageStatus.发送中)
                .ExecuteUpdateAsync(n => n.SetProperty(t => t.Status, ChatMessageStatus.已送达));
        }
        var maxSeq = messages.Count > 0 ? messages.Max(n => n.Seq) : afterSeq;
        return (messages, maxSeq);
    }

    /// <summary>
    /// 删除会话（2026-09-18 会话分组归并批次；2026-09-21 日志删除逻辑调整）：只删除会话行
    /// （t_chat_session，会话数据），**不删除消息行**——t_chat_message 是业务真实产生的记录，
    /// 消息记录的删除入口只有日志中心（Logs/delete|clear），会话侧删除不再连带清理。
    /// 返回 (会话行是否存在并删除, 当前全局最大 Seq)。会话删除后，归属任务再推送消息时会话
    /// 自动重建（出站落库驱动，重建行预置已读水位，见 <see cref="AppendNoSaveAsync"/>）。
    /// </summary>
    /// <remarks>
    /// 消息行不再删除 → Seq 号段不复用，响应 MaxSeq 恒为当前全局最大值；客户端既有的
    /// 「游标回拨 min(当前, MaxSeq)」兼容语义退化为 no-op（回拨到当前最大 = 不动），无需变更。
    /// </remarks>
    public async Task<(bool Deleted, long MaxSeq)> DeleteSessionAsync(string sessionKey)
    {
        var key = NormalizeSessionKey(sessionKey);
        var deleted = await _dbContext.ChatSessions
            .Where(n => n.SessionKey == key)
            .ExecuteDeleteAsync() > 0;
        // ExecuteDelete 绕过变更跟踪：同一上下文若残留该会话的跟踪实体（同作用域内先查后删再落库的
        // 连续操作，如测试），后续 FindAsync 会拿到已删行的陈旧快照——脱离跟踪，保持「行已删」的一致视图
        foreach (var entry in ((DbContext)_dbContext).ChangeTracker.Entries<ChatSessionModel>()
                     .Where(n => n.Entity.SessionKey == key).ToList())
        {
            entry.State = EntityState.Detached;
        }
        var maxSeq = await _dbContext.ChatMessages.AsNoTracking()
            .MaxAsync(n => (long?)n.Seq) ?? 0;
        return (deleted, maxSeq);
    }

    /// <summary>
    /// 会话改名迁移（§2.5，2026-09-18 会话分组归并批次）：把 oldKey 会话的消息行与会话行整体迁到 newKey。
    /// 假定调用方（TaskService.UpdateAsync）已开事务，自身不 SaveChanges、不开事务；
    /// 全程 ExecuteUpdate/ExecuteDelete（即时参与调用方事务，不产生需保存的跟踪实体）。
    /// </summary>
    /// <remarks>
    /// 消息行：(SessionKey, Seq) 复合索引已在，代价低；索引非唯一、Seq 全局唯一，无撞约束风险。
    /// 会话行（P2 合并语义）：目标键**无行** → 源行改键；目标键**已有行**（任务从独立会话并入既有共享会话，
    /// 本方案主场景）→ 合并——留目标行、LastSeq 取大、CreateTime 取小，删源行（直接 UPDATE 改键会撞主键）。
    /// 旧键无会话行时幂等（仅迁移消息行）。
    /// </remarks>
    internal async Task RenameSession(string oldKey, string newKey)
    {
        var oldNormalized = NormalizeSessionKey(oldKey);
        var newNormalized = NormalizeSessionKey(newKey);
        if (oldNormalized == newNormalized)
        {
            return;
        }
        // 1) 消息行整段改键（默认会话在消息表存 null，新键为空串时归一回 null 保持列内表示一致）
        await _dbContext.ChatMessages
            .Where(n => oldNormalized.Length == 0
                ? (n.SessionKey == null || n.SessionKey == "")
                : n.SessionKey == oldNormalized)
            .ExecuteUpdateAsync(s => s.SetProperty(
                n => n.SessionKey,
                newNormalized.Length == 0 ? null : newNormalized));
        // 2) 会话行：目标无行改名 / 有行合并（P2）
        var source = await _dbContext.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(n => n.SessionKey == oldNormalized);
        if (source == null)
        {
            return;
        }
        var target = await _dbContext.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(n => n.SessionKey == newNormalized);
        if (target == null)
        {
            await _dbContext.ChatSessions
                .Where(n => n.SessionKey == oldNormalized)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.SessionKey, newNormalized));
        }
        else
        {
            await _dbContext.ChatSessions
                .Where(n => n.SessionKey == newNormalized)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.LastSeq, Math.Max(target.LastSeq, source.LastSeq))
                    // 已读水位同取大：并入方的已读进度一并带入，否则其已读消息在合并会话里重现未读
                    .SetProperty(n => n.LastReadSeq, Math.Max(target.LastReadSeq, source.LastReadSeq))
                    .SetProperty(n => n.CreateTime, target.CreateTime < source.CreateTime ? target.CreateTime : source.CreateTime));
            await _dbContext.ChatSessions
                .Where(n => n.SessionKey == oldNormalized)
                .ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// 单条用户消息跨会话迁移（§4.3，2026-09-18 触发消息迁移批次）：把指定「接收」方向的消息行
    /// 改键到目标会话（目标键恒为任务会话键，非空），并 upsert 目标会话行推进 LastSeq。
    /// 返回 (是否实际迁移, 行 Seq)，供调用方决定是否广播 message_moved 帧。
    /// </summary>
    /// <remarks>
    /// 天然幂等：行已在新会话 / msgId 不存在（未落气泡的入口）时返回 (false, 0)，不建行不迁移。
    /// 行 Seq 不变——增量同步游标、已读水位、未读数全部不受影响；源会话行保留、LastSeq 不回拨
    /// （水位只影响列表排序，容忍残留）。默认会话在消息表存 null：SQL 三值逻辑下
    /// SessionKey != newKey 对 null 行不为真，定位条件必须显式包含 null——否则漏掉
    /// 「默认会话触发 → 迁任务会话」这一最主要场景。
    /// </remarks>
    public async Task<(bool Moved, long Seq)> MoveMessageSessionAsync(string msgId, string newSessionKey)
    {
        var newKey = NormalizeSessionKey(newSessionKey);
        if (string.IsNullOrWhiteSpace(msgId) || newKey.Length == 0)
        {
            return (false, 0);
        }
        await using var tx = await _dbContext.Database.BeginTransactionAsync();
        var row = await _dbContext.ChatMessages.AsNoTracking()
            .Where(n => n.MsgId == msgId && n.Direction == ChatMessageDirection.接收
                && (n.SessionKey == null || n.SessionKey != newKey))
            .FirstOrDefaultAsync();
        if (row == null)
        {
            return (false, 0);
        }
        await _dbContext.ChatMessages
            .Where(n => n.Id == row.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.SessionKey, newKey));
        // 目标会话行 upsert / LastSeq 推进（AppendNoSaveAsync 同款语义）；建行同样预置
        // 已读水位（防会话删除后重建时保留的历史整会话计未读，见 AppendNoSaveAsync 注释）
        var session = await _dbContext.ChatSessions.FindAsync(newKey);
        if (session == null)
        {
            var existingMax = await _dbContext.ChatMessages.AsNoTracking()
                .Where(n => n.SessionKey == newKey)
                .MaxAsync(n => (long?)n.Seq) ?? 0;
            _dbContext.ChatSessions.Add(new ChatSessionModel
            {
                SessionKey = newKey,
                CreateTime = DateTime.Now,
                LastSeq = row.Seq,
                LastReadSeq = Math.Min(existingMax, row.Seq)
            });
        }
        else if (row.Seq > session.LastSeq)
        {
            session.LastSeq = row.Seq;
        }
        await _dbContext.SaveChangesAsync();
        await tx.CommitAsync();
        return (true, row.Seq);
    }

    /// <summary>
    /// WS 下行消息 ACK：按 MsgId 标记已送达（幂等，仅从发送中推进）。
    /// </summary>
    public async Task<bool> MarkDeliveredAsync(string msgId)
    {
        if (string.IsNullOrEmpty(msgId))
        {
            return false;
        }
        return await _dbContext.ChatMessages
            .Where(n => n.MsgId == msgId && n.Status == ChatMessageStatus.发送中)
            .ExecuteUpdateAsync(n => n.SetProperty(t => t.Status, ChatMessageStatus.已送达)) > 0;
    }

    /// <summary>
    /// 已读回执：确认已读到 upToSeq（推进全部机器人下发消息的已读状态）。
    /// </summary>
    /// <returns>推进后剩余未读数</returns>
    public async Task<long> MarkReadAsync(long upToSeq)
    {
        await _dbContext.ChatMessages
            .Where(n => n.Direction == ChatMessageDirection.发送
                && n.Seq <= upToSeq && n.Status != ChatMessageStatus.已读)
            .ExecuteUpdateAsync(n => n.SetProperty(t => t.Status, ChatMessageStatus.已读));
        return await UnreadCountAsync();
    }

    /// <summary>
    /// 会话已读水位上报（2026-09-21 双端同步批次）：按会话推进服务端权威水位，
    /// 供 App/Web 的 sessions/read 端点调用。合并钳制见 <see cref="MergeReadWatermarksAsync"/>；
    /// 顺带把**实际推进会话内** Seq ≤ 水位且非已读的发送行推进为已读（比旧全局
    /// <see cref="MarkReadAsync"/> 更精确——不跨会话误标，messages/unread-count 体系继续有意义）。
    /// </summary>
    /// <param name="readSeqs">各会话的客户端本地水位（键 = 会话键，空串/空白 = 默认会话；服务端归一）</param>
    /// <returns>实际发生推进的 (会话键, 水位) 列表——调用方据此广播 session_read 帧（只推进才发帧）</returns>
    public async Task<List<(string SessionKey, long ReadSeq)>> MarkSessionsReadAsync(Dictionary<string, long> readSeqs)
    {
        readSeqs ??= [];
        var normalized = new Dictionary<string, long>(readSeqs.Count);
        foreach (var entry in readSeqs)
        {
            var key = NormalizeSessionKey(entry.Key);
            // 归一后同键（如 " a" 与 "a"）取最大，不丢进度
            normalized[key] = Math.Max(normalized.TryGetValue(key, out var prev) ? prev : 0, entry.Value);
        }
        if (normalized.Count == 0)
        {
            return [];
        }
        var sessions = await _dbContext.ChatSessions.AsNoTracking()
            .Where(n => normalized.Keys.Contains(n.SessionKey))
            .ToListAsync();
        var (_, advanced) = await MergeReadWatermarksAsync(sessions, normalized);
        foreach (var (key, seq) in advanced)
        {
            // 逐会话推进已读状态：默认会话在消息表存 null（会话行存空串），谓词必须显式含 null——
            // 漏写则默认会话 Status 推进静默失效，unread-count 体系默认会话未读失真
            var query = _dbContext.ChatMessages
                .Where(n => n.Direction == ChatMessageDirection.发送
                    && n.Seq <= seq && n.Status != ChatMessageStatus.已读);
            query = key.Length == 0
                ? query.Where(n => n.SessionKey == null || n.SessionKey == "")
                : query.Where(n => n.SessionKey == key);
            await query.ExecuteUpdateAsync(n => n.SetProperty(t => t.Status, ChatMessageStatus.已读));
        }
        return advanced;
    }

    /// <summary>
    /// 合并钳制（§3.2 单一函数，sessions/read 与 sessions/overview 共用）：
    /// <c>effective = min(max(server.LastReadSeq, client上报值), session.LastSeq)</c>，
    /// effective 大于服务端现值时条件 UPDATE 落库（只进；无推进零写）。
    /// max 吸收客户端超前值（离线期间已读但上报失败/延迟的追赶）；钳到 LastSeq 防异常高水位，
    /// 并让会话删除后的 stale 高水位自愈（2026-09-21 起删除即删行，重建建行时预置水位）。
    /// UPDATE 单语句条件推进（WHERE LastReadSeq &lt; effective）天然并发安全，受影响行数 &gt; 0 才计入推进列表。
    /// </summary>
    /// <returns>(每会话 effective 水位（含未推进者，供 overview 响应 ReadSeq）, 实际推进的 (键, 值) 列表（供广播）)</returns>
    private async Task<(Dictionary<string, long> Effective, List<(string SessionKey, long ReadSeq)> Advanced)> MergeReadWatermarksAsync(
        List<ChatSessionModel> sessions, Dictionary<string, long> readSeqs)
    {
        readSeqs ??= [];
        if (readSeqs.Count > MaxReadWatermarkEntries)
        {
            throw new BusinessException($"已读水位条目过多（{readSeqs.Count} > {MaxReadWatermarkEntries}），请分批上报");
        }
        var effective = new Dictionary<string, long>(sessions.Count);
        var advanced = new List<(string SessionKey, long ReadSeq)>();
        foreach (var session in sessions)
        {
            var key = session.SessionKey ?? "";
            readSeqs.TryGetValue(key, out var client);
            var value = Math.Min(Math.Max(session.LastReadSeq, client), session.LastSeq);
            effective[key] = value;
            if (value > session.LastReadSeq)
            {
                var affected = await _dbContext.ChatSessions
                    .Where(n => n.SessionKey == key && n.LastReadSeq < value)
                    .ExecuteUpdateAsync(n => n.SetProperty(t => t.LastReadSeq, value));
                if (affected > 0)
                {
                    advanced.Add((key, value));
                }
            }
        }
        return (effective, advanced);
    }

    /// <summary>
    /// 未读会话消息数（机器人下发且未读）
    /// </summary>
    public async Task<long> UnreadCountAsync()
    {
        return await _dbContext.ChatMessages.AsNoTracking()
            .Where(n => n.Direction == ChatMessageDirection.发送 && n.Status != ChatMessageStatus.已读)
            .LongCountAsync();
    }

    /// <summary>
    /// 当前最大 Seq（客户端校准游标用）
    /// </summary>
    public async Task<long> GetMaxSeqAsync()
    {
        return await _dbContext.ChatMessages.AsNoTracking()
            .MaxAsync(n => (long?)n.Seq) ?? 0;
    }

    /// <summary>
    /// 历史漫游：按关键字检索（SQL LIKE 起步），Seq 倒序分页。
    /// </summary>
    public async Task<List<ChatMessageModel>> HistoryAsync(string keyword, int page, int pageSize)
    {
        pageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);
        page = Math.Max(page, 1);
        var query = _dbContext.ChatMessages.AsNoTracking();
        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where(n => n.Content.Contains(keyword)
                || (n.ContentText != null && n.ContentText.Contains(keyword)));
        }
        return await query.OrderByDescending(n => n.Seq)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();
    }

    /// <summary>
    /// 会话列表快照（Web 管理端首屏 / 多端会话列表）：每会话最后一条消息 + 总数 + 未读数。
    /// 主从结构（2026-09-18 会话分组归并批次）：t_chat_session 会话表为主 LEFT JOIN 消息聚合——
    /// 空会话行（Total=0 / Last=null / Unread=0）同样出现在列表；排序 LastSeq 降序、
    /// 空会话（LastSeq=0）垫底按 CreateTime 降序。删除会话即删行：该会话不再出现在列表，
    /// 保留的消息行在新消息重建会话行后重新聚入（Total 含全部保留历史）。
    /// 2026-09-21 双端同步批次起水位**服务端权威化**：先合并钳制（吸收调用方上报、推进落库），
    /// 未读按合并后的 effective 水位计算（对端已读进度即时生效），响应条目带 ReadSeq 供调用方追赶。
    /// </summary>
    /// <param name="readSeqs">调用方各会话的本地已读水位（键 = 会话键，空串 = 默认会话）</param>
    /// <remarks>
    /// App 端能用本地 Room 库自行聚合列表与未读；Web 端（尤其首次打开）没有全量历史，
    /// 若靠 api/App/messages 增量把全库拉完才能算出未读，几万条历史时是几十次请求。
    /// <para>
    /// 未读不能用 Status != 已读 代替：服务端 MarkReadAsync 按**全局 Seq** 推进已读状态，
    /// 多会话交错时会把别的会话一并标已读；水位判定与 App 本地实现（direction=发送 且 seq &gt; 水位）同源。
    /// </para>
    /// </remarks>
    public async Task<(List<ChatSessionOverview> sessions, long maxSeq)> SessionsAsync(Dictionary<string, long> readSeqs)
    {
        readSeqs ??= [];
        var sessions = await _dbContext.ChatSessions.AsNoTracking()
            .OrderByDescending(n => n.LastSeq)
            .ThenByDescending(n => n.CreateTime)
            .ToListAsync();

        // 合并钳制（§3.2）：吸收调用方水位并推进落库；未读与响应 ReadSeq 都以 effective 权威值为准
        var (effective, _) = await MergeReadWatermarksAsync(sessions, readSeqs);

        // 消息聚合（从表）：Total / LastSeq / 各会话最后一条；空会话在 headMap 无条目
        var heads = await _dbContext.ChatMessages.AsNoTracking()
            .GroupBy(n => n.SessionKey)
            .Select(g => new { SessionKey = g.Key, LastSeq = g.Max(x => x.Seq), Total = (long)g.Count() })
            .ToListAsync();
        var headMap = heads
            .GroupBy(n => NormalizeSessionKey(n.SessionKey))
            .ToDictionary(g => g.Key, g => g.First());

        // Seq 全局唯一（唯一索引 + 事务内 MAX+1 分配），因此一次 IN 查询即可取齐各会话最后一条
        var lastSeqs = heads.Select(n => n.LastSeq).ToList();
        var lasts = lastSeqs.Count == 0
            ? []
            : await _dbContext.ChatMessages.AsNoTracking()
                .Where(n => lastSeqs.Contains(n.Seq))
                .ToListAsync();
        var lastMap = lasts.ToDictionary(n => n.Seq);

        var result = new List<ChatSessionOverview>(sessions.Count);
        // 未读计数单条 SQL（LoadUnreadCountsAsync）：替代循环内逐会话 LongCountAsync 的 N+1；
        // 水位用合并后的 effective（服务端权威）——对端（另一设备/浏览器）的已读进度即时反映
        var unreadMap = await LoadUnreadCountsAsync(sessions, effective);
        foreach (var session in sessions)
        {
            var key = session.SessionKey ?? "";
            headMap.TryGetValue(key, out var head);
            unreadMap.TryGetValue(key, out var unread);
            lastMap.TryGetValue(head?.LastSeq ?? 0, out var last);
            effective.TryGetValue(key, out var readSeq);
            result.Add(new ChatSessionOverview
            {
                SessionKey = key,
                Total = head?.Total ?? 0,
                Unread = unread,
                ReadSeq = readSeq,
                Last = last
            });
        }
        var maxSeq = heads.Count > 0 ? heads.Max(n => n.LastSeq) : 0;
        return (result, maxSeq);
    }

    /// <summary>
    /// 逐会话水位未读数 → 单条 GROUP BY 查询：把「每会话(会话匹配 &amp;&amp; Seq&gt;水位)」用
    /// System.Linq.Expressions 组装成 OR 链谓词（EF 可整体翻译为 SQL，MySql/Sqlite 双库均支持）。
    /// 水位取合并钳制后的 effective 权威值（2026-09-21 起服务端落库）；无条目的会话水位为 0 = 全部发送消息未读，
    /// 与原逐会话实现 readSeqs.TryGetValue(key, out var readSeq) 缺省 0 的语义一致。
    /// </summary>
    private async Task<Dictionary<string, long>> LoadUnreadCountsAsync(List<ChatSessionModel> sessions, Dictionary<string, long> readSeqs)
    {
        if (sessions.Count == 0)
        {
            return [];
        }
        var p = Expression.Parameter(typeof(ChatMessageModel), "n");
        var seqProp = Expression.Property(p, nameof(ChatMessageModel.Seq));
        var keyProp = Expression.Property(p, nameof(ChatMessageModel.SessionKey));
        Expression body = null;
        foreach (var session in sessions)
        {
            var key = session.SessionKey ?? "";
            readSeqs.TryGetValue(key, out var readSeq);
            Expression match = key.Length == 0
                ? Expression.OrElse(
                    Expression.Equal(keyProp, Expression.Constant(null, typeof(string))),
                    Expression.Equal(keyProp, Expression.Constant(string.Empty)))
                : Expression.Equal(keyProp, Expression.Constant(key));
            var condition = Expression.AndAlso(match,
                Expression.GreaterThan(seqProp, Expression.Constant(readSeq, typeof(long))));
            body = body == null ? condition : Expression.OrElse(body, condition);
        }
        var predicate = Expression.Lambda<Func<ChatMessageModel, bool>>(body, p);
        var rows = await _dbContext.ChatMessages.AsNoTracking()
            .Where(n => n.Direction == ChatMessageDirection.发送)
            .Where(predicate)
            .GroupBy(n => n.SessionKey)
            .Select(g => new { SessionKey = g.Key, Unread = (long)g.Count() })
            .ToListAsync();
        return rows
            .GroupBy(n => NormalizeSessionKey(n.SessionKey))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Unread));
    }

    /// <summary>
    /// 会话内倒序分页：取某会话 Seq &lt; beforeSeq 的最近 limit 条（升序返回），首屏与上翻加载用，只读。
    /// </summary>
    /// <param name="sessionKey">会话键（空 = 默认会话）</param>
    /// <param name="beforeSeq">取该 Seq 之前（更早）的消息；≤ 0 表示从最新一条开始取</param>
    /// <param name="limit">单页条数（默认 50，最大 200）</param>
    /// <remarks>
    /// 与 SyncAsync 的分工：SyncAsync 是「全局 Seq 升序游标增量」（断线补拉，跨会话），
    /// 本方法是「单会话按 Seq 倒序分页」（首屏只拉当前会话最近一页，与历史总量无关）。
    /// </remarks>
    public async Task<(List<ChatMessageModel> messages, bool hasMore)> SessionPageAsync(string sessionKey, long beforeSeq, int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        var key = NormalizeSessionKey(sessionKey);
        var query = _dbContext.ChatMessages.AsNoTracking()
            .Where(n => key.Length == 0
                ? (n.SessionKey == null || n.SessionKey == "")
                : n.SessionKey == key);
        if (beforeSeq > 0)
        {
            query = query.Where(n => n.Seq < beforeSeq);
        }
        // 多取一条判断是否还有更早的，返回前剔除（省掉一次 COUNT）
        var rows = await query.OrderByDescending(n => n.Seq).Take(limit + 1).ToListAsync();
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        rows.Reverse();
        return (rows, hasMore);
    }

    /// <summary>
    /// 会话键归一：null/空白 → 空串（默认会话），其余去首尾空白。
    /// </summary>
    private static string NormalizeSessionKey(string sessionKey)
    {
        return string.IsNullOrWhiteSpace(sessionKey) ? "" : sessionKey.Trim();
    }
}
