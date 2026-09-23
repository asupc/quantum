using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Plugins;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 富交互消息（2026-09-18 批次2/3）：结构化 Payload 的落库/WS 帧透传、
/// QuantumNotifyFacade 选项组装与校验、NotifyService 载荷长度上限、
/// MessageProcess 会话内优先路由候选筛选。
/// §5-3：用例改写进程级静态 SendMessageHelper.Sender，并入 ConstsState 全局静态串行集合，避免与其它 Sender 用例并发互覆。
/// </summary>
[Collection("ConstsState")]
public class RichMessagePayloadTests : IDisposable
{
    private const string Sentinel = "facade-payload-test";

    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly AppMessageService _messageService;
    private readonly AppWebSocketManager _wsManager;
    private readonly AppPushService _pushService;
    private readonly NotifyService _notifyService;

    public RichMessagePayloadTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _messageService = new AppMessageService(_db, NullLogger<AppMessageService>.Instance);
        _wsManager = new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance);
        _pushService = new AppPushService(_db, _messageService, _wsManager, NullLogger<AppPushService>.Instance);
        _notifyService = new NotifyService();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// 换发送接缝捕获 DTO（只留本类哨兵内容，隔离其他测试可能入队的消息）；
    /// 静态泵可能抢先出队但走同一 Sender，故按哨兵过滤 + 轮询等待（与 ScriptEngineTests 同一惯例）。
    /// </summary>
    private static async Task<List<MessageProccessDTO>> CaptureSentAsync(Func<Task> send, int expected)
    {
        var captured = new List<MessageProccessDTO>();
        var original = SendMessageHelper.Sender;
        SendMessageHelper.Sender = m =>
        {
            if (m.message != null && m.message.Contains(Sentinel, StringComparison.Ordinal))
            {
                captured.Add(m);
            }
            return Task.CompletedTask;
        };
        try
        {
            await send();
            for (var i = 0; captured.Count < expected && i < 200; i++)
            {
                await Task.Delay(10);
            }
        }
        finally
        {
            SendMessageHelper.Sender = original;
        }
        return captured;
    }

    [Fact]
    public async Task SendChatMessageAsync_WithPayload_RowAndFrameCarryPayload()
    {
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);
        var payload = "{\"options\":[{\"key\":\"3\",\"label\":\"3. 晴天\",\"reply\":\"3\",\"color\":\"green\"}]}";

        var message = await _pushService.SendChatMessageAsync("选一首试听：", "text", payload: payload);

        // 帧内 payload 是转义后的 JSON 字符串值，断言以字段存在 + 落库行原文为准
        Assert.Contains("\"payload\":", socket.SentText.Replace(" ", ""));
        Assert.Contains("options", socket.SentText);
        var stored = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Id == message.Id);
        Assert.Equal(payload, stored.Payload);
    }

    [Fact]
    public async Task SendChatMessageAsync_WithoutPayload_FrameOmitsPayload()
    {
        var socket = new FakeAppSocket();
        _wsManager.Add(socket);

        await _pushService.SendChatMessageAsync("普通消息", "text");

        Assert.DoesNotContain("\"payload\"", socket.SentText);
        var stored = await _db.ChatMessages.AsNoTracking().SingleAsync(n => n.Content == "普通消息");
        Assert.Null(stored.Payload);
    }

    [Fact]
    public async Task Facade_SendOptionsAsync_PayloadShapeAndSessionKey()
    {
        var captured = await CaptureSentAsync(() => new QuantumNotifyFacade(_notifyService, "task-music", "task-music")
            .SendOptionsAsync($"{Sentinel} 搜索结果：", new[]
            {
                new QuantumOption("3", "3. 晴天 - 周杰伦", Desc: "《叶惠美》 04:29"),
                new QuantumOption("save", "保存到服务器", Reply: "保存3", Color: "blue")
            }), 1);

        var dto = Assert.Single(captured);
        Assert.Equal("task-music", dto.SessionKey);
        Assert.Equal($"{Sentinel} 搜索结果：", dto.message);
        var payload = JObject.Parse(dto.Payload);
        var options = (JArray)payload["options"];
        Assert.Equal(2, options.Count);
        // reply 缺省归一为 key；可选字段为 null（客户端按缺省渲染）
        Assert.Equal("3", options[0]["key"]);
        Assert.Equal("3. 晴天 - 周杰伦", options[0]["label"]);
        Assert.Equal("3", options[0]["reply"]);
        // JToken 的 JSON null 不是 CLR null，取字符串值断言
        Assert.Null(options[0]["color"].Value<string>());
        Assert.Equal("《叶惠美》 04:29", options[0]["desc"]);
        Assert.Equal("保存3", options[1]["reply"]);
        Assert.Equal("blue", options[1]["color"]);
    }

    [Fact]
    public async Task Facade_SendImageAsync_WithOptions_CarriesPayload()
    {
        var captured = await CaptureSentAsync(() => new QuantumNotifyFacade(_notifyService, "task-movie", "task-movie")
            .SendImageAsync($"https://img/{Sentinel}-cover.jpg", "[ID]片名", new[]
            {
                new QuantumOption("45148", "订阅本片", Color: "green")
            }), 1);

        var dto = Assert.Single(captured);
        Assert.Equal(MessageType.图片, dto.MessageType);
        Assert.Equal("task-movie", dto.SessionKey);
        var payload = JObject.Parse(dto.Payload);
        Assert.Equal("45148", payload["options"][0]["key"]);
        Assert.Equal("订阅本片", payload["options"][0]["label"]);
    }

    [Fact]
    public async Task Facade_SendImageAsync_WithoutOptions_NoPayload()
    {
        var captured = await CaptureSentAsync(() => new QuantumNotifyFacade(_notifyService, "task-movie", "task-movie")
            .SendImageAsync($"https://img/{Sentinel}-plain.jpg", "说明"), 1);

        Assert.Null(Assert.Single(captured).Payload);
    }

    [Fact]
    public async Task Facade_SendVideoAsync_WithPoster_CarriesPosterPayload()
    {
        var captured = await CaptureSentAsync(() => new QuantumNotifyFacade(_notifyService, "task-video", "task-video")
            .SendVideoAsync($"https://v/{Sentinel}.mp4", "标题", $"https://img/{Sentinel}-poster.jpg"), 1);

        var dto = Assert.Single(captured);
        Assert.Equal(MessageType.视频, dto.MessageType);
        Assert.Equal($"https://img/{Sentinel}-poster.jpg", JObject.Parse(dto.Payload)["poster"]);
    }

    [Fact]
    public async Task Facade_SendVideoAsync_WithoutPoster_NoPayload()
    {
        var captured = await CaptureSentAsync(() => new QuantumNotifyFacade(_notifyService, "task-video", "task-video")
            .SendVideoAsync($"https://v/{Sentinel}-plain.mp4"), 1);

        Assert.Null(Assert.Single(captured).Payload);
    }

    [Fact]
    public async Task Facade_SendOptionsAsync_MoreThanLimit_Throws()
    {
        var facade = new QuantumNotifyFacade(_notifyService, "t", "t");
        var options = Enumerable.Range(1, 21).Select(i => new QuantumOption(i.ToString(), $"选项{i}")).ToArray();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => facade.SendOptionsAsync("内容", options));
        Assert.Contains("20", ex.Message);
    }

    [Fact]
    public async Task Facade_SendOptionsAsync_BlankKeyOrLabel_Throws()
    {
        var facade = new QuantumNotifyFacade(_notifyService, "t", "t");

        await Assert.ThrowsAsync<BusinessException>(() => facade.SendOptionsAsync("内容", [new QuantumOption(" ", "标签")]));
        await Assert.ThrowsAsync<BusinessException>(() => facade.SendOptionsAsync("内容", [new QuantumOption("k", null)]));
    }

    [Fact]
    public async Task Facade_SendOptionsAsync_EmptyOptions_Throws()
    {
        var facade = new QuantumNotifyFacade(_notifyService, "t", "t");

        await Assert.ThrowsAsync<BusinessException>(() => facade.SendOptionsAsync("内容", Array.Empty<QuantumOption>()));
    }

    [Fact]
    public void NotifyService_Send_OversizedPayload_Throws()
    {
        var oversize = new string('x', 33 * 1024);

        var ex = Assert.Throws<BusinessException>(() => _notifyService.Send(new SendNotifyDTO
        {
            message = "内容",
            Payload = "{\"options\":[{\"label\":\"" + oversize + "\"}]}"
        }));
        Assert.Contains("32KB", ex.Message);
    }

    [Fact]
    public void SelectTaskCandidates_SessionTaskMatches_OnlyThatTask()
    {
        // 电影港/音乐搜索正则重叠（纯数字都命中）：会话键命中电影港任务 → 只返回电影港
        var movie = NewTask("t-movie", "^[0-9,，\\s]+$", regex: true);
        var music = NewTask("t-music", "^[0-9,，\\s]+$", regex: true);
        var process = new MessageProcess();

        var candidates = MessageProcess.SelectTaskCandidates([movie, music],
            new MessageProccessDTO { message = "3", SessionKey = "t-movie", CommunicationType = CommunicationType.App },
            process.commandReg);

        var candidate = Assert.Single(candidates);
        Assert.Equal("t-movie", candidate.Id);
    }

    [Fact]
    public void SelectTaskCandidates_SessionTaskNotMatch_FallsBackToAll()
    {
        // 会话内发了会话任务不匹配的指令（如关键字搜索）：回落全局匹配，行为与旧版一致
        var movie = NewTask("t-movie", "^[0-9,，\\s]+$", regex: true);
        var music = NewTask("t-music", "^音乐搜索.*", regex: true);
        var process = new MessageProcess();

        var candidates = MessageProcess.SelectTaskCandidates([movie, music],
            new MessageProccessDTO { message = "音乐搜索 周杰伦", SessionKey = "t-movie", CommunicationType = CommunicationType.App },
            process.commandReg);

        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void SelectTaskCandidates_NoOrUnknownSession_AllTasks()
    {
        var movie = NewTask("t-movie", "^[0-9,，\\s]+$", regex: true);
        var music = NewTask("t-music", "^[0-9,，\\s]+$", regex: true);
        var process = new MessageProcess();

        Assert.Equal(2, MessageProcess.SelectTaskCandidates([movie, music],
            new MessageProccessDTO { message = "3", CommunicationType = CommunicationType.App }, process.commandReg).Count);
        // 会话键不是现存任务 Id（已删除的任务会话）：同样回落全局
        Assert.Equal(2, MessageProcess.SelectTaskCandidates([movie, music],
            new MessageProccessDTO { message = "3", SessionKey = "t-gone", CommunicationType = CommunicationType.App }, process.commandReg).Count);
    }

    [Fact]
    public void SelectTaskCandidates_CommunicationTypeFilterStillApplies()
    {
        // 通讯类型不兼容的任务始终被过滤（会话优先路由不破坏既有过滤语义）
        var movie = NewTask("t-movie", "^[0-9,，\\s]+$", regex: true);
        var webOnly = NewTask("t-web", "^[0-9,，\\s]+$", regex: true, communicationTypes: ((int)CommunicationType.App + 1).ToString());
        var process = new MessageProcess();

        var candidates = MessageProcess.SelectTaskCandidates([movie, webOnly],
            new MessageProccessDTO { message = "3", CommunicationType = CommunicationType.App }, process.commandReg);

        var candidate = Assert.Single(candidates);
        Assert.Equal("t-movie", candidate.Id);
    }

    private static TaskModel NewTask(string id, string command, bool regex, string communicationTypes = null) =>
        new()
        {
            Id = id,
            Name = id,
            Command = command,
            EnableRegex = regex,
            CommunicationTypes = communicationTypes,
            Enable = true
        };

    /// <summary>内存型 WebSocket（与 AppPushServiceTests 中的假件同构），记录发送内容。</summary>
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
