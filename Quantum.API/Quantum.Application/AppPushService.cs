using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// App 发送编排器（两级兜底）：在线 WS 广播 → 落库补拉（重连/启动按 Seq 增量同步）。
/// 厂商离线推送（极光）已整体移除：消息实时性由 App 常驻 WS（后台锁定保活）承担，
/// 断线期间消息仅落库，App 回前台/重连后增量补拉。
/// 会话消息落 t_chat_message，站内通知落 t_app_notification 并镜像进会话流（共用 msgId 幂等），
/// 二者共用同一条推送管道。单管理员体系：所有消息面向全部在线设备广播。
/// </summary>
public class AppPushService
{
    /// <summary>
    /// WS 下行帧序列化：关闭非 ASCII 转义（中文原文传输，便于客户端日志排查与减小帧体积）；
    /// null 字段省略（缺失字段与 null 对客户端反序列化等价，帧更紧凑）
    /// </summary>
    internal static readonly JsonSerializerOptions FrameJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IQuantumDbContext _dbContext;
    private readonly AppMessageService _messageService;
    private readonly AppWebSocketManager _wsManager;
    private readonly ILogger<AppPushService> _logger;

    public AppPushService(IQuantumDbContext dbContext, AppMessageService messageService,
        AppWebSocketManager wsManager, ILogger<AppPushService> logger)
    {
        _dbContext = dbContext;
        _messageService = messageService;
        _wsManager = wsManager;
        _logger = logger;
    }

    /// <summary>
    /// 机器人 → 会话消息：落库（发送中）→ WS 在线广播（送达即标记已送达）→ 离线走厂商推送。
    /// 无论在线与否消息都已持久化，离线设备重连后按 Seq 补拉。
    /// image/file 消息可带配文（contentText），一条消息同时呈现媒体与文字；
    /// payload 为结构化富交互载荷（可点选项/视频封面，JSON），客户端解析失败按无载荷降级渲染。
    /// </summary>
    public async Task<ChatMessageModel> SendChatMessageAsync(string content, string contentType = "text",
        string contentText = null, string sessionKey = null, string payload = null)
    {
        var message = await _messageService.AppendAsync(ChatMessageDirection.发送, content, contentType,
            contentText: contentText, sessionKey: sessionKey, payload: payload);
        var frame = JsonSerializer.Serialize(new
        {
            type = "message",
            msgId = message.MsgId,
            seq = message.Seq,
            content = message.Content,
            contentType = message.ContentType,
            contentText = message.ContentText,
            // 结构化富交互载荷（可点选项/视频封面）：null 序列化省略，旧客户端忽略未知字段
            payload = message.Payload,
            // 会话键（= 任务 Id 或任务配置的会话名）：客户端据此归档到对应会话；null 序列化省略（默认会话）
            session = message.SessionKey,
            createTime = message.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")
        }, AppPushService.FrameJsonOptions);
        if (await TryPushOnlineAsync(frame))
        {
            await _messageService.MarkDeliveredAsync(message.MsgId);
            message.Status = ChatMessageStatus.已送达;
        }
        else
        {
            // 离线路径：消息已落库，设备重连/回前台后按 Seq 增量补拉（厂商离线推送已移除）
            _logger.LogInformation("App 设备离线，消息待补拉：msgId={MsgId}", message.MsgId);
        }
        return message;
    }

    /// <summary>
    /// 站内通知：落 t_app_notification 并同步写入会话表 t_chat_message（所有通知统一在会话流中展示）
    /// → WS 广播 notify 帧（带镜像行真实 Seq）→ 离线厂商推送。
    /// </summary>
    public async Task<AppNotificationModel> SendNotificationAsync(string title, string content,
        string category = "system", string jump = null, string sessionKey = null)
    {
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }
        var msgId = Guid.NewGuid().ToString();
        var notification = new AppNotificationModel
        {
            Id = Guid.NewGuid().ToString().Replace("-", ""),
            Title = title,
            Content = content,
            Category = string.IsNullOrEmpty(category) ? "system" : category,
            Jump = jump,
            MsgId = msgId,
            CreatedAt = DateTime.Now
        };

        // 统一在会话流中展示：以机器人下发消息写入 t_chat_message（共用 msgId 保证幂等），
        // 与通知行同事务提交——会话流是通知在 App 内的唯一展示面，不允许出现"通知有、会话没有"
        var chatContent = string.IsNullOrEmpty(title) ? content : $"【{title}】\n{content}";
        var mirror = await SaveNotificationWithMirrorAsync(notification, chatContent, sessionKey);

        var frame = JsonSerializer.Serialize(new
        {
            type = "notify",
            msgId = notification.MsgId,
            // 镜像行真实 Seq：客户端据此入库，与 REST 补拉行同源同序（缺失时客户端不再伪造本地 seq）
            seq = mirror.Seq,
            category = notification.Category,
            title = notification.Title,
            content = notification.Content,
            jump = notification.Jump,
            // 会话键（= 任务 Id 或任务配置的会话名）：任务类通知归对应任务会话，system/security 无此字段（默认会话）
            session = mirror.SessionKey,
            createTime = mirror.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")
        }, FrameJsonOptions);
        if (!await TryPushOnlineAsync(frame))
        {
            _logger.LogInformation("App 设备离线，通知待补拉：msgId={MsgId}", notification.MsgId);
        }
        else
        {
            await _messageService.MarkDeliveredAsync(msgId);
        }
        return notification;
    }

    /// <summary>
    /// 通知行与它的会话镜像**同一事务**提交，Seq 撞车（并发写）时整事务回滚重试。
    /// 任一写入失败则两侧都不落库并把异常交给调用方——避免"通知表有、会话流没有"的半成品状态。
    /// </summary>
    private async Task<ChatMessageModel> SaveNotificationWithMirrorAsync(AppNotificationModel notification,
        string chatContent, string sessionKey)
    {
        for (var attempt = 0; ; attempt++)
        {
            ChatMessageModel mirror = null;
            try
            {
                await using var tx = await _dbContext.Database.BeginTransactionAsync();
                _dbContext.AppNotifications.Add(notification);
                await _dbContext.SaveChangesAsync();
                mirror = await _messageService.AppendNoSaveAsync(ChatMessageDirection.发送, chatContent,
                    AppMessageService.NotifyContentType, ChatMessageStatus.发送中, notification.MsgId,
                    sessionKey: sessionKey);
                await _dbContext.SaveChangesAsync();
                await tx.CommitAsync();
                return mirror;
            }
            catch (DbUpdateException e) when (attempt < 3)
            {
                // 回滚重试前必须让两个实体脱离跟踪（否则重试会带着上一次的插入一起提交）
                _dbContext.Entry(notification).State = EntityState.Detached;
                if (mirror != null)
                {
                    _dbContext.Entry(mirror).State = EntityState.Detached;
                }
                // AppendNoSaveAsync 同批 upsert 的会话行（t_chat_session）一并脱离跟踪（同上原因）
                _messageService.DetachPendingSessionRows();
                _logger.LogWarning(e, "通知与镜像写入冲突，整事务重试（{Attempt}/3）", attempt + 1);
            }
        }
    }

    /// <summary>
    /// 管理员 → 机器人指令提交：先落会话气泡（方向=接收，视为已读），再与 Web 通道同语义入消息队列。
    /// contentType 支持图片/文件消息封装（Content=FileId，客户端渲染走 AppUpload 鉴权下载），
    /// 图片/文件可带配文 contentText。调用方负责限流；指令为空返回 false。
    /// targetTaskId 为点选来源任务 Id（可空，选项消息根部 taskId 由客户端透传）：
    /// 仅用于入站三级路由的①级精确路由，不落库、不影响回显。
    /// </summary>
    public async Task<bool> SubmitCommandAsync(string content, string contentType = "text", string contentText = null,
        string sessionKey = null, string targetTaskId = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }
        var originSession = string.IsNullOrWhiteSpace(sessionKey) ? null : sessionKey.Trim();
        var originTargetTask = string.IsNullOrWhiteSpace(targetTaskId) ? null : targetTaskId.Trim();
        var bubble = await _messageService.AppendAsync(ChatMessageDirection.接收, content,
            string.IsNullOrEmpty(contentType) ? "text" : contentType, ChatMessageStatus.已读, contentText: contentText,
            sessionKey: originSession);
        // 广播「接收」行回显：WS 发送是异步投递，发送端的小同步可能跑在落库之前（自己的气泡丢失），
        // echo 帧让发送设备即时渲染；多端同屏一致；客户端按 msgId 幂等去重
        var echoFrame = JsonSerializer.Serialize(new
        {
            type = "message",
            msgId = bubble.MsgId,
            seq = bubble.Seq,
            content = bubble.Content,
            contentType = bubble.ContentType,
            contentText = bubble.ContentText,
            direction = (int)bubble.Direction,
            session = bubble.SessionKey,
            createTime = bubble.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")
        }, FrameJsonOptions);
        _ = Task.Run(async () =>
        {
            try
            {
                await TryPushOnlineAsync(echoFrame);
            }
            catch
            {
                // 回显失败无碍：行已落库，重连/回前台补拉兜底
            }
        });
        MessageQueue.MessageQueues.Enqueue(new MessageProccessDTO
        {
            CommunicationType = CommunicationType.App,
            message = content,
            user_id = SystemConfigHelper.GetSetting()?.UserName ?? "admin",
            // 气泡 msgId 透传（§4.1 跨会话触发消息迁移）：MessageProcess 迁移判定据此定位 t_chat_message 行；
            // 兼作任务环境变量 MessageId 的值（此前恒空被过滤，已核查无脚本消费）
            message_id = bubble.MsgId,
            // 来源会话继承：快捷回复/系统指令回复经 SendMessage 克隆透传后归来源会话；
            // 任务脚本的回复由 ctx.Notify 门面显式覆盖为任务自身会话，不受此影响
            SessionKey = originSession,
            // 点选来源任务（①级精确路由）：出站克隆（SendMessageHelper.SendMessage）会显式置空，
            // 防任务回复匹配自身指令形成自触发回路
            TargetTaskId = originTargetTask
        });
        LogServiceHelper.Logs.Enqueue(new LogModel
        {
            Title = "App触发消息指令",
            Remark = content,
            CreateTime = DateTime.Now,
            LogType = LogType.操作日志,
            Operator = "App",
            Success = true,
            UserIP = "app"
        });
        return true;
    }

    /// <summary>
    /// 会话已读水位广播（2026-09-21 双端同步批次）：把 sessions/read 实际推进的 (会话键, 水位) 逐个
    /// 广播 <c>session_read</c> 帧——在线对端据此只进推进本地水位，角标/red dot 即时消除；
    /// 默认会话省略 session 字段（与 message 帧同风格），服务端只可能发出递增 seq（只进推进才广播；
    /// clear 重置不广播，靠合并钳制自愈）。广播失败降级为静默（对端下次 overview 交换兜底追赶）。
    /// </summary>
    public async Task BroadcastSessionReadAsync(IReadOnlyCollection<(string SessionKey, long ReadSeq)> advanced)
    {
        foreach (var (key, seq) in advanced)
        {
            var frame = JsonSerializer.Serialize(new
            {
                type = "session_read",
                session = string.IsNullOrEmpty(key) ? null : key,
                seq
            }, FrameJsonOptions);
            await TryPushOnlineAsync(frame);
        }
    }

    /// <summary>
    /// WS 在线广播；推送异常按离线处理（消息已落库，可补拉）。
    /// </summary>
    private async Task<bool> TryPushOnlineAsync(string frame)
    {
        try
        {
            return await _wsManager.SendToAllAsync(frame);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "App WS 直推异常，转落库补拉");
            return false;
        }
    }

    /// <summary>
    /// 跨会话触发消息迁移（§4.4，2026-09-18 批次）：行改键（<see cref="AppMessageService.MoveMessageSessionAsync"/>）
    /// → 实际迁移才广播 message_moved 帧（msgId/seq/from/to；from 为空省略 = 默认会话），
    /// 客户端据此搬本地行、并在停留来源会话时自动切换窗口。
    /// 迁移/广播失败不抛（降级 = 消息留原会话，任务照常执行，不影响主流程）。
    /// </summary>
    public async Task MoveUserMessageAsync(string msgId, string from, string to)
    {
        try
        {
            var (moved, seq) = await _messageService.MoveMessageSessionAsync(msgId, to);
            if (!moved)
            {
                return;
            }
            var frame = JsonSerializer.Serialize(new
            {
                type = "message_moved",
                msgId,
                seq,
                from = string.IsNullOrWhiteSpace(from) ? null : from.Trim(),
                to = to?.Trim()
            }, FrameJsonOptions);
            await TryPushOnlineAsync(frame);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "跨会话触发消息迁移失败（降级为留原会话）：msgId={MsgId}", msgId);
        }
    }
}

/// <summary>
/// App 推送静态门面：SendMessageHelper（静态消息泵，无请求作用域）与 LoginService 等非请求场景
/// 通过这里创建作用域并调用 AppPushService。Startup.Configure 中调用 Init 完成装配。
/// </summary>
public static class AppPushDispatcher
{
    private static IServiceScopeFactory _scopeFactory;

    public static void Init(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 机器人 → 会话消息（SendMessageHelper 静态消息泵入口）。image/file 消息可带配文；
    /// payload 为结构化富交互载荷（可点选项/视频封面，JSON）。
    /// </summary>
    public static async Task SendChatMessageAsync(string content, string contentType = "text", string contentText = null,
        string sessionKey = null, string payload = null)
    {
        if (!EnsureInitialized())
        {
            return;
        }
        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppPushService>().SendChatMessageAsync(content, contentType, contentText, sessionKey, payload);
    }

    /// <summary>
    /// 站内通知（任务/系统/安全提醒入口）。
    /// </summary>
    public static async Task SendNotificationAsync(string title, string content, string category = "system", string jump = null,
        string sessionKey = null)
    {
        if (!EnsureInitialized())
        {
            return;
        }
        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppPushService>().SendNotificationAsync(title, content, category, jump, sessionKey);
    }

    /// <summary>
    /// 跨会话触发消息迁移（MessageProcess 入口：经 root provider 解析、无请求作用域，不能构造注入，
    /// 仿 SendChatMessageAsync 走 CreateScope；行迁移 + message_moved 帧广播）。
    /// </summary>
    public static async Task MoveUserMessageAsync(string msgId, string from, string to)
    {
        if (!EnsureInitialized())
        {
            return;
        }
        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppPushService>().MoveUserMessageAsync(msgId, from, to);
    }

    /// <summary>
    /// 未初始化（单测直调/启动异常）时降级为丢弃并留痕：静态消息泵不允许因 App 通道异常中断。
    /// </summary>
    private static bool EnsureInitialized()
    {
        if (_scopeFactory != null)
        {
            return true;
        }
        Console.WriteLine("AppPushDispatcher 未初始化（Startup.Configure 需调用 Init），App 消息已丢弃。");
        return false;
    }
}
