using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Quantum.Application;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Web.Middleware;

/// <summary>
/// /ws/app?token=&lt;jwt&gt; 的 App 长连接网关（重写自 /ws/chat，替代 Web 聊天成为唯一用户触点）：
/// - 握手必须携带有效 JWT（AppAuthService 签发，与管理端共用密钥），验签失败 401 拒绝升级；
///   也支持连接后首帧 {"type":"auth","token":"..."} 兜底鉴权（4.4 协议上行 auth）。
///   单管理员体系：令牌还必须带 Manager claim——Open AppKey/任务临时令牌禁止连 WS（下行广播含会话内容）
/// - 下行 message/notify 帧带 msgId，客户端 ack 幂等确认送达；断线重连后按 seq 走 sync/REST 补拉
/// - 接收帧：ping 保活、command 指令（按设备限流 2s）、ack 已送达、sync 增量同步
/// - 60s 无任何帧踢连接（客户端 25s 应用层心跳，约 2 倍余量，容忍 Doze/网络抖动）
/// - 单帧总长度上限 256KB：拒绝无 EndOfMessage 的无限分片拼帧（未认证连接即可发起的内存耗尽 DoS）
/// </summary>
public class AppWebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AppWebSocketManager _manager;
    private readonly ILogger<AppWebSocketMiddleware> _logger;

    /// <summary>
    /// 单帧最大总字节数（拼帧累计，超限即断连）：正常业务帧远小于此值。
    /// </summary>
    private const int MaxFrameBytes = 256 * 1024;

    public AppWebSocketMiddleware(RequestDelegate next, AppWebSocketManager manager, ILogger<AppWebSocketMiddleware> logger)
    {
        _next = next;
        _manager = manager;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path != "/ws/app" || !context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        // 握手鉴权：token 存在但验签失败 → 401 拒绝；留空则等待首帧 auth 兜底
        var token = context.Request.Query["token"].ToString();
        var principal = JwtTokenValidator.Validate(token);
        if (!string.IsNullOrWhiteSpace(token) && principal == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        // 单管理员：仅 Manager 令牌可连（Open AppKey/任务临时令牌无 Manager claim，防广播内容泄露）
        var deviceId = principal?.FindFirst("DeviceId")?.Value ?? "";
        var authenticated = principal?.FindFirst("Manager")?.Value == "true";
        if (principal != null)
        {
            // WS 不经认证中间件，这里手动把已验证令牌挂到 HttpContext：
            // 后续按身份取值（设备 Id/账号）的逻辑与 REST 端点共用同一事实源
            context.User = principal;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (authenticated)
        {
            _manager.Add(socket);
        }
        else
        {
            await SendJson(socket, new { type = "error", content = "未鉴权：请发送 {\"type\":\"auth\",\"token\":\"<jwt>\"} 完成登录。" });
        }

        // §1-12：心跳超时巡检已上收到进程内单例 AppWebSocketHeartbeatService（周期扫全表、踢死连），
        // 此处不再为每条连接各起一个 Task.Delay 循环（N 连接 = N 长驻定时器，且连接销毁前不停）。
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var receiveCts = new CancellationTokenSource(ReceiveTimeout);
                WebSocketReceiveResult result = null;
                // 分片字节先积累、EndOfMessage 后一次性解码：逐分片 GetString 在多字节字符被切断的
                // 边界会产生 U+FFFD 乱码（正确性缺陷），且单帧多份字符串拷贝
                using var frame = new MemoryStream();
                var oversized = false;
                try
                {
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                        frame.Write(buffer, 0, result.Count);
                        if (frame.Length > MaxFrameBytes)
                        {
                            // 无 EndOfMessage 的无限分片拼帧是内存耗尽 DoS：超限即断连，不再继续读
                            oversized = true;
                            break;
                        }
                    } while (!result.EndOfMessage);
                }
                catch (OperationCanceledException)
                {
                    // 客户端长时间沉默由心跳巡检处理，这里继续等待
                    continue;
                }
                if (result != null && result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                if (oversized)
                {
                    _logger.LogWarning("App WS frame over {Max}KB, closing: deviceId={DeviceId}", MaxFrameBytes / 1024, deviceId);
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"frame exceeds {MaxFrameBytes / 1024}KB", CancellationToken.None);
                    }
                    catch
                    {
                        // 连接已死，忽略关闭异常
                    }
                    break;
                }

                _manager.MarkAlive(socket);

                var text = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }
                // 每帧只解析一次，ping 判定/兜底鉴权/帧处理共享（旧实现各 Parse 一次）
                using var doc = TryParseFrame(text);
                if (IsPingFrame(text, doc))
                {
                    await SendJson(socket, new { type = "pong" });
                    continue;
                }
                if (!authenticated)
                {
                    (authenticated, principal) = await TryAuthenticateAsync(socket, doc);
                    if (!authenticated)
                    {
                        // 首帧 auth 验签失败/非管理员令牌：下发错误并关闭，客户端应重新登录换取 token
                        break;
                    }
                    context.User = principal;
                    deviceId = principal.FindFirst("DeviceId")?.Value ?? "";
                    _manager.Add(socket);
                    continue;
                }
                await HandleFrameAsync(context, socket, doc);
            }
        }
        catch (WebSocketException e)
        {
            _logger.LogInformation(e, "App WS closed unexpectedly: deviceId={DeviceId}", deviceId);
        }
        finally
        {
            if (authenticated)
            {
                _manager.Remove(socket);
            }
        }
    }

    private static TimeSpan ReceiveTimeout => TimeSpan.FromMinutes(10);

    /// <summary>
    /// 心跳帧按 type 字段精确判断（原实现是子串匹配，会把内容含 "ping" 的 command 帧误当心跳吞掉）。
    /// doc 为调用方一次解析的结果（可空 = 非 JSON 帧）。
    /// </summary>
    private static bool IsPingFrame(string text, JsonDocument doc)
    {
        if (text == "ping")
        {
            return true;
        }
        return doc != null
            && doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "ping";
    }

    /// <summary>帧文本一次解析（ping 判定/兜底鉴权/帧处理共享）；非 JSON 帧返回 null。</summary>
    private static JsonDocument TryParseFrame(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 首帧兜底鉴权：type=auth 携带 token；验签失败或非 Manager 令牌下发 error 帧并关闭连接（返回 false）。
    /// doc 为调用方一次解析的结果（可空 = 非 JSON 帧，按鉴权失败处理）。
    /// </summary>
    private async Task<(bool ok, ClaimsPrincipal principal)> TryAuthenticateAsync(WebSocket socket, JsonDocument doc)
    {
        string token = null;
        if (doc?.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("type", out var type)
            && type.GetString() == "auth"
            && doc.RootElement.TryGetProperty("token", out var tokenElement))
        {
            token = tokenElement.GetString();
        }
        var principal = JwtTokenValidator.Validate(token);
        if (principal == null)
        {
            await SendJson(socket, new { type = "error", content = "token 无效或已过期，请重新登录。" });
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth failed", CancellationToken.None);
            return (false, null);
        }
        // 单管理员：非 Manager 令牌（Open AppKey/任务临时令牌）拒绝接入
        if (principal.FindFirst("Manager")?.Value != "true")
        {
            await SendJson(socket, new { type = "error", content = "需要管理员权限。" });
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "manager only", CancellationToken.None);
            return (false, null);
        }
        return (true, principal);
    }

    /// <summary>
    /// 已鉴权连接的帧分发。DB 操作走短生命周期作用域，避免长连接长期占用 DbContext。
    /// doc 为调用方一次解析的结果（可空 = 非 JSON 帧，直接忽略）。
    /// </summary>
    private async Task HandleFrameAsync(HttpContext context, WebSocket socket, JsonDocument doc)
    {
        string type = null, content = null, msgId = null, contentType = null, session = null, contentText = null, targetTask = null;
        long afterSeq = 0;
        if (doc?.RootElement.ValueKind == JsonValueKind.Object)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeElement))
            {
                type = typeElement.GetString();
            }
            if (root.TryGetProperty("content", out var contentElement))
            {
                content = contentElement.GetString();
            }
            if (root.TryGetProperty("contentType", out var contentTypeElement))
            {
                contentType = contentTypeElement.GetString();
            }
            if (root.TryGetProperty("contentText", out var contentTextElement))
            {
                // command 帧配文（与 REST api/App/command 的 ContentText 同语义；此前 WS 漏解析）
                contentText = contentTextElement.GetString();
            }
            if (root.TryGetProperty("msgId", out var msgIdElement))
            {
                msgId = msgIdElement.GetString();
            }
            if (root.TryGetProperty("session", out var sessionElement))
            {
                session = sessionElement.GetString();
            }
            if (root.TryGetProperty("targetTask", out var targetTaskElement))
            {
                // command 帧点选来源任务（可空）：选项消息根部 taskId 由客户端点选代发时透传，
                // 供入站三级路由①级精确路由使用（手打/重发不带）
                targetTask = targetTaskElement.GetString();
            }
            if (root.TryGetProperty("afterSeq", out var afterSeqElement) && afterSeqElement.TryGetInt64(out var seq))
            {
                afterSeq = seq;
            }
        }
        if (string.IsNullOrEmpty(type))
        {
            return;
        }

        using var scope = context.RequestServices.CreateScope();
        var pushService = scope.ServiceProvider.GetRequiredService<AppPushService>();
        var messageService = scope.ServiceProvider.GetRequiredService<AppMessageService>();

        switch (type)
        {
            case "ack":
                await messageService.MarkDeliveredAsync(msgId);
                break;
            case "sync":
                var (messages, maxSeq) = await messageService.SyncAsync(afterSeq);
                foreach (var message in messages)
                {
                    await SendJson(socket, new
                    {
                        type = "message",
                        msgId = message.MsgId,
                        seq = message.Seq,
                        content = message.Content,
                        contentType = message.ContentType,
                        payload = message.Payload,
                        direction = (int)message.Direction,
                        session = message.SessionKey,
                        createTime = message.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                }
                await SendJson(socket, new { type = "sync_done", maxSeq });
                break;
            case "command":
                // 2026-09-19 起不限流：选项可重复点选语义下连点被 2s 窗口误拦，按所有者要求移除（与 REST api/App/command 同步）
                if (!await pushService.SubmitCommandAsync(content, contentType, contentText, session, targetTask))
                {
                    await SendJson(socket, new { type = "error", content = "指令提交失败。" });
                }
                break;
            default:
                // 未知帧忽略（协议向前兼容）
                break;
        }
    }

    private async Task SendJson(WebSocket socket, object payload)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        var json = JsonSerializer.Serialize(payload, AppPushService.FrameJsonOptions);
        // 已注册连接（通过 URL token 或首帧 auth 完成鉴权）必须经管理器按连接 SendLock 发送：
        // pong/sync/error 等下行帧与随机时刻的推送并发写同一 WebSocket 会抛 InvalidOperationException。
        // §1-3：仅「从未注册」的连接才回退直发（此时无并发推送、直发安全）；
        // SendFailed 表示套接字已被管理器 Abort+Remove，绝不裸发；Sent 表示已由管理器发送成功。
        var result = await _manager.SendToOneAsync(socket, json);
        if (result != SendToOneResult.NotFound)
        {
            return;
        }
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            using var timeoutCts = new CancellationTokenSource(_manager.SendTimeout);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeoutCts.Token);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // 未注册连接的下行帧尽力而为，失败不再冒泡（对端多半已断开）
        }
    }
}
