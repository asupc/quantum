using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Quantum.Application;
using Quantum.API.Tests.Contract;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Utils;
using Quantum.Web.Middleware;

namespace Quantum.API.Tests;

/// <summary>
/// §5-7：AppWebSocketMiddleware 帧处理决策白盒测试（收帧 ping 判定 / 首帧兜底鉴权 / 帧上限守卫）。
/// 中间件完整收发循环依赖 Kestrel 的 IHttpWebSocketFeature（无 TestHost 包），故聚焦可隔离判定的
/// 私有纯函数与鉴权分支——这些正是 §1-3 回退直发外的连接生命周期关键路径。
/// </summary>
[Collection("ConstsState")]
public class AppWebSocketMiddlewareTests
{
    static AppWebSocketMiddlewareTests()
    {
        Log4NetTestSetup.EnsureRepository();
        Consts.SymmetricSecurityKey = "ws-mw-test-symmetric-security-key-0123456789abcdef";
        Consts.SecurityIssuer = "Issuer.WsMwTest";
        Consts.SecurityAudience = "Audience.WsMwTest";
        Consts.UserTokenNotBefore = 0;
    }

    private static AppWebSocketMiddleware CreateMiddleware()
        => new(new RequestDelegate(_ => Task.CompletedTask), new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance), NullLogger<AppWebSocketMiddleware>.Instance);

    private static object InvokeStatic(string name, params object?[] args)
        => typeof(AppWebSocketMiddleware).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    private static void InvokeInstance(string name, WebSocket socket, JsonDocument? doc)
        => InvokeFrame(HandleContext(null), socket, doc, name);

    /// <summary>帧分发所需的短生命周期 DI 作用域（AppPushService/AppMessageService 各一件）。</summary>
    private static DefaultHttpContext HandleContext(QuantumSqliteDbContext? db)
    {
        db ??= AppTestDb.Create().db;
        var messageService = new AppMessageService(db, NullLogger<AppMessageService>.Instance);
        var wsManager = new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton<IQuantumDbContext>(db);
        services.AddSingleton(messageService);
        services.AddSingleton(wsManager);
        services.AddSingleton(new AppPushService(db, messageService, wsManager, NullLogger<AppPushService>.Instance));
        // command 帧会经 AppPushService.SubmitCommandAsync 入队（MessageProcess 消费循环用）
        MessageQueue.MessageQueues ??= new BoundedConcurrentQueue<MessageProccessDTO>(1000);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private static void InvokeFrame(HttpContext context, WebSocket socket, JsonDocument? doc, string name = "HandleFrameAsync")
    {
        var task = (Task)typeof(AppWebSocketMiddleware)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(CreateMiddleware(), new object?[] { context, socket, doc })!;
        task.GetAwaiter().GetResult();
    }

    private static string CreateToken(bool login)
    {
        var claims = new List<Claim>
        {
            new("Name", login ? "tester" : HttpContextExtension.OpenAppTokenName),
            new("UserId", "user-1"),
            new("DeviceId", "device-1"),
        };
        if (login)
        {
            claims.Add(new Claim("TokenPurpose", "User"));
            claims.Add(new Claim("LoginTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));
        }
        else claims.Add(new Claim("TokenPurpose", "Open"));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey));
        var token = new JwtSecurityToken(
            Consts.SecurityIssuer, Consts.SecurityAudience, claims: claims,
            notBefore: DateTime.Now.AddMinutes(-1), expires: DateTime.Now.AddMinutes(120),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static JsonDocument? Parse(string text)
        => (JsonDocument?)InvokeStatic("TryParseFrame", text);

    // ---------- 收帧：ping 帧精确判定（原子串匹配会把含 "ping" 的 command 误吞） ----------

    [Theory]
    [InlineData("ping", true)]
    [InlineData("{\"type\":\"ping\"}", true)]
    [InlineData("{\"type\":\"command\",\"content\":\"去 ping 一下\"}", false)]
    [InlineData("{\"type\":\"ack\",\"msgId\":\"ping-1\"}", false)]
    [InlineData("not-json", false)]
    public void IsPingFrame_OnlyHeartbeatFramesMatch(string text, bool expected)
    {
        using var doc = Parse(text);
        var result = (bool)InvokeStatic("IsPingFrame", text, (object?)doc!);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryParseFrame_NonJson_ReturnsNull()
    {
        Assert.Null(Parse("{ this is not json"));
        Assert.NotNull(Parse("{\"type\":\"ping\"}"));
    }

    // ---------- 首帧兜底鉴权 ----------

    private static (bool ok, ClaimsPrincipal? principal) Authenticate(WebSocket socket, string authFrameJson)
    {
        using var doc = Parse(authFrameJson);
        var task = (Task)typeof(AppWebSocketMiddleware)
            .GetMethod("TryAuthenticateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(CreateMiddleware(), new object?[] { socket, doc })!;
        task.GetAwaiter().GetResult();
        var vt = (ValueTuple<bool, ClaimsPrincipal>)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (vt.Item1, vt.Item2);
    }

    [Fact]
    public void TryAuthenticate_LoginToken_SucceedsAndKeepsSocketOpen()
    {
        using var setting = new TestLoginSettingScope();
        var socket = new RecordingWebSocket();
        var frame = "{\"type\":\"auth\",\"token\":\"" + CreateToken(login: true) + "\"}";

        var (ok, principal) = Authenticate(socket, frame);

        Assert.True(ok);
        Assert.NotNull(principal);
        Assert.Equal("User", principal.FindFirst("TokenPurpose")?.Value);
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Equal(0, socket.SendCount);
    }

    [Fact]
    public void TryAuthenticate_InvalidToken_RejectsAndCloses()
    {
        var socket = new RecordingWebSocket();

        var (ok, principal) = Authenticate(socket, "{\"type\":\"auth\",\"token\":\"garbage.token.here\"}");

        Assert.False(ok);
        Assert.Null(principal);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Contains("error", socket.SentText);
    }

    [Fact]
    public void TryAuthenticate_OpenToken_RejectsAndCloses()
    {
        // Open AppKey 令牌不能进入广播连接。
        var socket = new RecordingWebSocket();
        var frame = "{\"type\":\"auth\",\"token\":\"" + CreateToken(login: false) + "\"}";

        var (ok, principal) = Authenticate(socket, frame);

        Assert.False(ok);
        Assert.Null(principal);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    // ---------- 非字符串 JSON 字段：GetString() 抛 InvalidOperationException（升级后异常逃逸） ----------

    /// <summary>
    /// 畸形帧（字段类型错）不得抛异常：GetString() 仅在 ValueKind=String 时返回值，
    /// 其它类型（Number/True/Array/Object）一律按「字段缺失」处理。
    /// 修复前 {"type":123} 会抛 InvalidOperationException 并逃出只捕获 WebSocketException 的接收循环
    /// （连接已升级，连 500 都产不出来）。
    /// </summary>
    [Theory]
    [InlineData("{\"type\":123}")]
    [InlineData("{\"type\":\"auth\",\"token\":123}")]
    [InlineData("{\"type\":\"ack\",\"msgId\":12}")]
    [InlineData("{\"type\":\"command\",\"content\":[],\"session\":{}}")]
    [InlineData("{\"type\":true}")]
    [InlineData("{\"type\":\"command\",\"content\":123,\"contentType\":false,\"contentText\":[1],\"msgId\":{},\"session\":[],\"targetTask\":7}")]
    public void HandleFrame_MalformedFieldTypes_AreIgnoredWithoutThrowing(string frame)
    {
        using var doc = Parse(frame);
        var socket = new RecordingWebSocket();
        var db = AppTestDb.Create().db;
        using var _ = db;

        InvokeFrame(HandleContext(db), socket, doc);

        // 非字符串内容不得落库成指令气泡（字段按缺失处理；command 无 content 仅回一条 error 帧）
        Assert.Empty(db.ChatMessages);
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.DoesNotContain("\"type\":\"message\"", socket.SentText);
    }

    [Theory]
    [InlineData("{\"type\":123}")]
    [InlineData("{\"type\":\"auth\",\"token\":true}")]
    [InlineData("{\"type\":\"auth\",\"token\":[]}")]
    [InlineData("{\"type\":\"auth\",\"token\":{\"a\":1}}")]
    [InlineData("{\"type\":[1]}")]
    [InlineData("\"just-a-string\"")]
    [InlineData("[1,2]")]
    [InlineData("42")]
    public void TryAuthenticate_MalformedFieldTypes_FailClosedWithoutThrowing(string frame)
    {
        var socket = new RecordingWebSocket();

        var (ok, principal) = Authenticate(socket, frame);

        // 非字符串 token 不得被当成凭据：按鉴权失败处理（下发 error + 关闭），绝不抛异常
        Assert.False(ok);
        Assert.Null(principal);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Contains("error", socket.SentText);
    }

    [Theory]
    [InlineData("{\"type\":123}")]
    [InlineData("{\"type\":true}")]
    [InlineData("{\"type\":[1]}")]
    [InlineData("{\"type\":{\"a\":1}}")]
    [InlineData("{\"type\":null}")]
    public void IsPingFrame_NonStringType_IsNotHeartbeat(string text)
    {
        using var doc = Parse(text);
        // type 非字符串：既不算心跳（不得吞帧），也不得抛 InvalidOperationException
        Assert.False((bool)InvokeStatic("IsPingFrame", text, (object?)doc!));
    }

    [Fact]
    public void HandleFrame_LegitimateFrames_BehaveUnchanged()
    {
        // 合法帧行为不变（回归护栏）：command 帧照常提交指令气泡入队，type 解析不受加固影响
        var db = AppTestDb.Create().db;
        using var _ = db;
        var socket = new RecordingWebSocket();

        using var doc = Parse("{\"type\":\"command\",\"content\":\"合法指令\",\"contentType\":\"text\",\"contentText\":\"配文\"}");
        InvokeFrame(HandleContext(db), socket, doc);

        var row = Assert.Single(db.ChatMessages);
        Assert.Equal("合法指令", row.Content);
        Assert.Equal("配文", row.ContentText);
    }

    // ---------- 单帧上限守卫（未认证连接即可发起的内存耗尽 DoS 防线） ----------

    [Fact]
    public void MaxFrameBytes_GuardLimitIs256KB()
    {
        var field = (int)typeof(AppWebSocketMiddleware)
            .GetField("MaxFrameBytes", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(256 * 1024, field);
    }

    /// <summary>记录型内存 WebSocket：捕获下行帧与关闭状态，用于鉴权分支断言。</summary>
    private sealed class RecordingWebSocket : WebSocket
    {
        private readonly List<byte> _buffer = [];
        private WebSocketState _state = WebSocketState.Open;

        public string SentText => Encoding.UTF8.GetString([.. _buffer]);
        public int SendCount { get; private set; }

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
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new InvalidOperationException("RecordingWebSocket 不产生入站帧");
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            _buffer.AddRange(buffer.Array!.Skip(buffer.Offset).Take(buffer.Count));
            SendCount++;
            return Task.CompletedTask;
        }
    }
}
