using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Quantum.Application;
using Quantum.API.Tests.Contract;
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
        Consts.ManagerTokenNotBefore = 0;
    }

    private static AppWebSocketMiddleware CreateMiddleware()
        => new(new RequestDelegate(_ => Task.CompletedTask), new AppWebSocketManager(NullLogger<AppWebSocketManager>.Instance), NullLogger<AppWebSocketMiddleware>.Instance);

    private static object InvokeStatic(string name, params object?[] args)
        => typeof(AppWebSocketMiddleware).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    private static string CreateToken(bool manager)
    {
        var claims = new List<Claim>
        {
            new("Name", "tester"),
            new("UserId", "user-1"),
            new("DeviceId", "device-1"),
        };
        if (manager)
        {
            claims.Add(new Claim("Manager", "true"));
            // 管理令牌吊销闸要求携带 LoginTime（Unix 秒），否则 ValidateManagerNotBefore 直接拒绝
            claims.Add(new Claim("LoginTime", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));
        }
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
    public void TryAuthenticate_ManagerToken_SucceedsAndKeepsSocketOpen()
    {
        var socket = new RecordingWebSocket();
        var frame = "{\"type\":\"auth\",\"token\":\"" + CreateToken(manager: true) + "\"}";

        var (ok, principal) = Authenticate(socket, frame);

        Assert.True(ok);
        Assert.NotNull(principal);
        Assert.Equal("true", principal.FindFirst("Manager")?.Value);
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
    public void TryAuthenticate_NonManagerToken_RejectsAndCloses()
    {
        // Open AppKey/任务临时令牌：验签通过但无 Manager claim，禁止连 WS（下行广播含会话内容）
        var socket = new RecordingWebSocket();
        var frame = "{\"type\":\"auth\",\"token\":\"" + CreateToken(manager: false) + "\"}";

        var (ok, principal) = Authenticate(socket, frame);

        Assert.False(ok);
        Assert.Null(principal);
        Assert.Equal(WebSocketState.Closed, socket.State);
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
