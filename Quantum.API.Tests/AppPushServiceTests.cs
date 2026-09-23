using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Quantum.Application;
using Quantum.Utils;
using Xunit;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Data;

namespace Quantum.API.Tests;

/// <summary>
/// App 推送编排器：在线 WS 广播（送达即标记）、离线仅落库待补拉（厂商离线推送已移除）、
/// 站内通知落库双写（镜像进会话流）、指令提交（用户气泡 + MessageQueue 入队）。
/// </summary>
public class AppPushServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly AppMessageService _messageService;
    private readonly AppWebSocketManager _wsManager;
    private readonly AppPushService _service;

    public AppPushServiceTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _messageService = new AppMessageService(_db, NullLogger<AppMessageService>.Instance);
        _wsManager = new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance);
        _service = new AppPushService(_db, _messageService, _wsManager, NullLogger<AppPushService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task SendChatMessageAsync_OnlineDevice_PushesFrameAndMarksDelivered()
    {
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);

        var message = await _service.SendChatMessageAsync("任务跑完了", "text");

        Assert.Contains("\"type\":\"message\"", socket.SentText.Replace(" ", ""));
        Assert.Contains(message.MsgId, socket.SentText);
        Assert.Contains("任务跑完了", socket.SentText);
        // WS 广播成功 → 已送达
        var stored = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Id == message.Id);
        Assert.Equal(ChatMessageStatus.已送达, stored.Status);
    }

    [Fact]
    public async Task SendChatMessageAsync_Offline_StaysPendingForCatchUp()
    {
        // 离线（无在线设备）：仅落库，状态保持发送中，设备重连后按 Seq 补拉
        var message = await _service.SendChatMessageAsync("离线提醒");

        var stored = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Id == message.Id);
        Assert.Equal(ChatMessageStatus.发送中, stored.Status);
        Assert.False(string.IsNullOrEmpty(stored.MsgId));
    }

    [Fact]
    public async Task SendChatMessageAsync_ImageWithCaption_FrameAndRowCarryCaption()
    {
        // 图片+配文：WS 帧与落库行都携带 ContentText，客户端一条气泡渲染图片+文字
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);

        var message = await _service.SendChatMessageAsync("f8c1img", "image", "截图说明");

        Assert.Contains("\"contentText\":\"截图说明\"", socket.SentText);
        var stored = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.MsgId == message.MsgId);
        Assert.Equal("f8c1img", stored.Content);
        Assert.Equal("image", stored.ContentType);
        Assert.Equal("截图说明", stored.ContentText);
    }

    [Fact]
    public async Task SendChatMessageAsync_OfflineImageWithCaption_PersistsCaptionOnRow()
    {
        // 离线图片消息：落库行保持 FileId 原文与配文（WS 直推与 REST 补拉的渲染不受影响）
        await _service.SendChatMessageAsync("f8c1img2", "image", "看这张图");

        var stored = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "f8c1img2");
        Assert.Equal("image", stored.ContentType);
        Assert.Equal("看这张图", stored.ContentText);
        Assert.Equal(ChatMessageStatus.发送中, stored.Status);
    }

    [Fact]
    public async Task SubmitCommandAsync_WithCaption_PersistsCaptionOnBubble()
    {
        // App 发图带配文：接收行落 ContentText，消息队列仍收 Content（脚本内核语义不变）
        MessageQueue.MessageQueues = new BoundedConcurrentQueue<MessageProccessDTO>(50_000);

        var accepted = await _service.SubmitCommandAsync("f8c1img3", "image", "我的截图");

        Assert.True(accepted);
        var bubble = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "f8c1img3");
        Assert.Equal("我的截图", bubble.ContentText);
        Assert.True(MessageQueue.MessageQueues.TryDequeue(out var dto));
        Assert.Equal("f8c1img3", dto.message);
    }

    [Fact]
    public async Task SendNotificationAsync_PersistsAndDeliversNotifyFrame_WithJumpAndMsgId()
    {
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);

        var notification = await _service.SendNotificationAsync("任务失败", "任务A执行失败", "task", "quantum://task/123");

        // 通知中心落库（Jump/MsgId 幂等路由字段）
        var stored = await _db.AppNotifications.AsNoTracking().SingleAsync(n => n.Id == notification.Id);
        Assert.Equal("task", stored.Category);
        Assert.Equal("quantum://task/123", stored.Jump);
        Assert.False(string.IsNullOrEmpty(stored.MsgId));
        Assert.Null(stored.ReadAt);

        // WS 下行 notify 帧同构，且必须携带镜像行真实 Seq 与时间 ——
        // 客户端据此入库（缺失时只能伪造本地 seq，会覆写真实消息行 / 产生同 msgId 两行）
        Assert.Contains("\"type\":\"notify\"", socket.SentText.Replace(" ", ""));
        Assert.Contains("quantum://task/123", socket.SentText);
        Assert.Contains(stored.MsgId, socket.SentText);

        // 同时落会话消息流（所有通知以机器人气泡展示，共用 msgId）
        var chatMsg = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.MsgId == notification.MsgId);
        Assert.Equal("【任务失败】\n任务A执行失败", chatMsg.Content);
        Assert.Equal(ChatMessageStatus.已送达, chatMsg.Status);
        Assert.Equal(AppMessageService.NotifyContentType, chatMsg.ContentType);
        Assert.Contains($"\"seq\":{chatMsg.Seq},", socket.SentText.Replace(" ", ""));
        Assert.Contains($"\"createTime\":\"{chatMsg.CreateTime:yyyy-MM-ddHH:mm:ss}\"", socket.SentText.Replace(" ", ""));
    }

    [Fact]
    public async Task SendNotificationAsync_Offline_PersistsNotificationAndMirrorForCatchUp()
    {
        // 离线：通知行 + 会话镜像行都已落库（共用 msgId），设备重连后增量补拉
        var notification = await _service.SendNotificationAsync("系统公告", "今晚维护", "system");

        var stored = await _db.AppNotifications.AsNoTracking().SingleAsync(n => n.Id == notification.Id);
        Assert.Equal("系统公告", stored.Title);
        Assert.Equal("今晚维护", stored.Content);

        var mirror = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.MsgId == notification.MsgId);
        Assert.Equal("【系统公告】\n今晚维护", mirror.Content);
        Assert.Equal(ChatMessageStatus.发送中, mirror.Status);
    }

    [Fact]
    public async Task SendNotificationAsync_EmptyTitle_IsNoOp()
    {
        Assert.Null(await _service.SendNotificationAsync(null, "t", "c"));
        Assert.Null(await _service.SendNotificationAsync("", "t", "c"));
        Assert.Empty(_db.AppNotifications);
    }

    [Fact]
    public async Task SubmitCommandAsync_EmptyContent_ReturnsFalse()
    {
        Assert.False(await _service.SubmitCommandAsync("  "));
        Assert.False(await _service.SubmitCommandAsync(null));
        Assert.Empty(_db.ChatMessages);
    }

    [Fact]
    public async Task SubmitCommandAsync_ValidContent_PersistsBubbleAndEnqueuesWithAppChannel()
    {
        MessageQueue.MessageQueues = new BoundedConcurrentQueue<MessageProccessDTO>(50_000);

        var accepted = await _service.SubmitCommandAsync("我的量子");

        Assert.True(accepted);
        // 用户气泡落库（方向=接收，直接已读）
        var bubble = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "我的量子");
        Assert.Equal(ChatMessageDirection.接收, bubble.Direction);
        Assert.Equal(ChatMessageStatus.已读, bubble.Status);
        // 指令以 App 通道进入消息内核（user_id 非空：单管理员主体键）
        Assert.True(MessageQueue.MessageQueues.TryDequeue(out var dto));
        Assert.Equal(CommunicationType.App, dto.CommunicationType);
        Assert.False(string.IsNullOrEmpty(dto.user_id));
        Assert.Equal("我的量子", dto.message);
    }

    [Fact]
    public async Task SubmitCommandAsync_EchoesUserBubbleWithDirection()
    {
        // 本用例不依赖同类其它用例先跑：静态入站队列自建（程序集加测试文件会抖动用例顺序，曾暴露此依赖）
        MessageQueue.MessageQueues = new BoundedConcurrentQueue<MessageProccessDTO>(50_000);

        // WS 发送端自己的气泡回显：接收行落库后作为 message 帧广播（direction=接收），
        // 发送设备即时渲染，不再依赖可能跑在落库之前的小同步；msgId 与 REST 行同源幂等
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);

        var accepted = await _service.SubmitCommandAsync("你好");

        Assert.True(accepted);
        Assert.Contains("你好", socket.SentText);
        Assert.Contains("\"direction\":2", socket.SentText.Replace(" ", ""));
        var bubble = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "你好");
        Assert.Equal(ChatMessageDirection.接收, bubble.Direction);
    }

    [Fact]
    public async Task SubmitCommandAsync_WithOriginSession_RowEchoAndQueueCarrySession()
    {
        // 从任务会话发起指令：自己的「接收」行与 echo 帧归来源会话；入队 DTO 同带会话键
        // （快捷回复经 SendMessage 克隆继承来源会话；任务回复由门面覆盖为任务自身会话）
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);
        MessageQueue.MessageQueues = new BoundedConcurrentQueue<MessageProccessDTO>(50_000);

        var accepted = await _service.SubmitCommandAsync("曹操", sessionKey: "task-music");

        Assert.True(accepted);
        var bubble = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "曹操");
        Assert.Equal(ChatMessageDirection.接收, bubble.Direction);
        Assert.Equal("task-music", bubble.SessionKey);
        Assert.Contains("\"session\":\"task-music\"", socket.SentText);
        Assert.True(MessageQueue.MessageQueues.TryDequeue(out var dto));
        Assert.Equal("task-music", dto.SessionKey);
    }

    [Fact]
    public async Task SendChatMessageAsync_WithSessionKey_RowAndFrameCarrySession()
    {
        // 任务脚本会话：sessionKey 落库行与 WS message 帧都携带（客户端据此归档到对应脚本会话）
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);

        var message = await _service.SendChatMessageAsync("电影港更新了", "text", sessionKey: "task-001");

        Assert.Contains("\"session\":\"task-001\"", socket.SentText);
        var stored = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Id == message.Id);
        Assert.Equal("task-001", stored.SessionKey);
    }

    [Fact]
    public async Task SendChatMessageAsync_WithoutSessionKey_FrameOmitsSession()
    {
        // 默认会话：无 sessionKey 时帧不携带 session 字段（旧客户端契约不变，新客户端归默认会话）
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);

        await _service.SendChatMessageAsync("快捷回复", "text");

        Assert.DoesNotContain("\"session\"", socket.SentText);
    }

    [Fact]
    public async Task SendNotificationAsync_TaskNotify_MirrorAndFrameCarrySession()
    {
        // 任务生命周期通知：镜像行与 notify 帧携带 session（归对应任务会话），jump 语义不变
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);

        var notification = await _service.SendNotificationAsync("任务通知", "执行完成", "task",
            "quantum://task/task-002/log", sessionKey: "task-002");

        Assert.Contains("\"session\":\"task-002\"", socket.SentText);
        var mirror = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.MsgId == notification.MsgId);
        Assert.Equal("task-002", mirror.SessionKey);
        Assert.Equal(AppMessageService.NotifyContentType, mirror.ContentType);
    }

    // ==================================================================== 跨会话触发消息迁移（§4.1/§4.4）

    [Fact]
    public async Task SubmitCommandAsync_EnqueuesBubbleMsgId()
    {
        // §4.1：入队 DTO 带 message_id——MessageProcess 迁移判定据此定位 t_chat_message 行
        MessageQueue.MessageQueues = new BoundedConcurrentQueue<MessageProccessDTO>(50_000);

        var accepted = await _service.SubmitCommandAsync("随机密码-1");

        Assert.True(accepted);
        var bubble = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "随机密码-1");
        Assert.True(MessageQueue.MessageQueues.TryDequeue(out var dto));
        Assert.Equal(bubble.MsgId, dto.message_id);
    }

    [Fact]
    public async Task MoveUserMessageAsync_DefaultOrigin_BroadcastsMessageMovedFrame()
    {
        // 默认会话来源（from 省略）：行改键 + message_moved 帧携带 msgId/seq/to
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);
        MessageQueue.MessageQueues = new BoundedConcurrentQueue<MessageProccessDTO>(50_000);
        Assert.True(await _service.SubmitCommandAsync("随机密码-1"));
        var bubble = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "随机密码-1");

        await _service.MoveUserMessageAsync(bubble.MsgId, null, "task-pwd");

        Assert.Contains("\"type\":\"message_moved\"", socket.SentText.Replace(" ", ""));
        Assert.Contains(bubble.MsgId, socket.SentText);
        Assert.Contains($"\"seq\":{bubble.Seq},", socket.SentText.Replace(" ", ""));
        Assert.Contains("\"to\":\"task-pwd\"", socket.SentText);
        Assert.DoesNotContain("\"from\"", socket.SentText); // 默认会话 from 省略（null 字段不序列化）
        // 行已改键（REST 补拉读到的是新会话键，与迁移帧语义一致）
        Assert.Equal("task-pwd", (await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.MsgId == bubble.MsgId)).SessionKey);
    }

    [Fact]
    public async Task MoveUserMessageAsync_TaskOrigin_FrameCarriesFrom()
    {
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);
        MessageQueue.MessageQueues = new BoundedConcurrentQueue<MessageProccessDTO>(50_000);
        Assert.True(await _service.SubmitCommandAsync("随机密码-1", sessionKey: "task-music"));
        var bubble = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "随机密码-1");

        await _service.MoveUserMessageAsync(bubble.MsgId, "task-music", "task-pwd");

        Assert.Contains("\"from\":\"task-music\"", socket.SentText);
        Assert.Contains("\"to\":\"task-pwd\"", socket.SentText);
    }

    [Fact]
    public async Task MoveUserMessageAsync_NotMoved_NoFrame_AndFailureNeverThrows()
    {
        // msgId 不存在：幂等 0 行，不广播
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);
        await _service.MoveUserMessageAsync("no-such-msg", "a", "b");
        Assert.DoesNotContain("message_moved", socket.SentText);

        // 迁移链路异常（DB 已释放）不抛：降级 = 消息留原会话，任务照常执行
        var (connection, db) = AppTestDb.Create();
        db.Dispose();
        connection.Dispose();
        var broken = new AppPushService(db,
            new AppMessageService(db, NullLogger<AppMessageService>.Instance),
            _wsManager, NullLogger<AppPushService>.Instance);
        await broken.MoveUserMessageAsync("any-msg", "a", "b");
    }

    /// <summary>
    /// 内存型 WebSocket（与 AppWebSocketManagerTests 中的假件同构），记录发送内容。
    /// </summary>
    private sealed class FakeAppSocket : WebSocket
    {
        private readonly List<byte> _buffer = [];
        private WebSocketState _state = WebSocketState.Open;

        public string SentText => Encoding.UTF8.GetString([.. _buffer]);

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("FakeAppSocket 不产生入站帧");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            _buffer.AddRange(buffer.Array!.Skip(buffer.Offset).Take(buffer.Count));
            return Task.CompletedTask;
        }
    }
}
