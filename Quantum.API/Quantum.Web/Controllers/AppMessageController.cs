using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.Model;
using Quantum.Utils;
using Quantum.Web.Filters;
using Quantum.Utils;

namespace Quantum.Web.Controllers;

/// <summary>
/// App 会话接口：seq 增量同步、已读回执、未读数、历史检索、指令提交、厂商推送注册与设备管理。
/// 单管理员体系：业务端点全部 Manager 专属，身份取 JWT 声明（DeviceId）。
/// 版本下发端点（原 version/latest 与 version/download 匿名端点）已随 App 端自更新移除，升级改手动安装 APK。
/// </summary>
[Route("api/App")]
[CustomAuthorizationFilter]
[ManagerOnly]
public class AppMessageController : BaseController
{
    private readonly AppMessageService _messageService;
    private readonly AppPushService _pushService;
    private readonly AppAuthService _appAuthService;
    private readonly AppNotifySettingService _notifySettingService;
    private readonly QrLoginService _qrLoginService;

    public AppMessageController(AppMessageService messageService, AppPushService pushService,
        AppAuthService appAuthService,
        AppNotifySettingService notifySettingService, QrLoginService qrLoginService)
    {
        _messageService = messageService;
        _pushService = pushService;
        _appAuthService = appAuthService;
        _notifySettingService = notifySettingService;
        _qrLoginService = qrLoginService;
    }

    /// <summary>
    /// 增量同步会话消息：取本地最大 seq 之后的消息（重连/启动补拉），顺带推进送达状态
    /// </summary>
    /// <param name="afterSeq">已同步到的最大 seq（首次传 0）</param>
    /// <param name="limit">单页上限（默认 100，最大 500）</param>
    [HttpGet("messages")]
    public async Task<object> Messages([FromQuery] long afterSeq = 0, [FromQuery] int limit = AppMessageService.SyncPageSize)
    {
        var (messages, maxSeq) = await _messageService.SyncAsync(afterSeq, limit);
        return new
        {
            MaxSeq = maxSeq,
            Data = messages
        };
    }

    /// <summary>
    /// 已读回执：确认已读到 upToSeq，返回剩余未读数
    /// </summary>
    [HttpPost("messages/read")]
    public Task<long> MarkRead([FromQuery] long upToSeq)
    {
        return _messageService.MarkReadAsync(upToSeq);
    }

    /// <summary>
    /// 清空会话（App 端「删除会话」）：**2026-09-21 日志删除逻辑调整起与 session/delete 同语义**——
    /// 只删除会话行，消息记录（业务真实产生的数据）不再连带删除，删除入口收敛到日志中心。
    /// 端点仅为已发布 App 的兼容保留，响应形状不变（Cleared 恒为 0，MaxSeq 为当前全局最大 Seq）。
    /// </summary>
    [HttpPost("messages/clear")]
    public async Task<object> ClearSession([FromQuery] string session = null)
    {
        var (_, maxSeq) = await _messageService.DeleteSessionAsync(session);
        return new
        {
            Cleared = 0,
            MaxSeq = maxSeq
        };
    }

    /// <summary>
    /// 未读会话消息数
    /// </summary>
    [HttpGet("messages/unread-count")]
    public Task<long> UnreadCount()
    {
        return _messageService.UnreadCountAsync();
    }

    /// <summary>
    /// 历史漫游：按关键字检索会话消息（SQL LIKE），seq 倒序分页
    /// </summary>
    [HttpGet("history")]
    public Task<List<ChatMessageModel>> History([FromQuery] string keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        return _messageService.HistoryAsync(keyword, page, pageSize);
    }

    /// <summary>
    /// 会话列表快照：每会话最后一条消息 + 总数 + 未读数（Web 管理端会话列表首屏）。
    /// 请求体携带各会话的本地已读水位（键 = 会话键，空串 = 默认会话）——服务端合并钳制为权威水位
    /// （吸收上报并落库推进），未读按权威水位计算，响应条目带 ReadSeq（客户端吸收即完成对端追赶）。
    /// </summary>
    [HttpPost("sessions/overview")]
    public async Task<object> SessionsOverview([FromBody] ChatSessionOverviewRequest request)
    {
        var (sessions, maxSeq) = await _messageService.SessionsAsync(request?.ReadSeqs);
        return new
        {
            MaxSeq = maxSeq,
            Sessions = sessions
        };
    }

    /// <summary>
    /// 会话已读水位上报（2026-09-21 双端同步批次）：按会话推进服务端权威水位（只进 + 钳到 LastSeq，
    /// 入参形状与 sessions/overview 相同故直接复用）。实际推进的会话逐个广播 session_read 帧——
    /// 在线对端即时清角标；离线对端靠下次 overview 交换追赶。响应 Advanced = 实际推进的 (会话键, 水位) 列表。
    /// </summary>
    [HttpPost("sessions/read")]
    public async Task<object> SessionsRead([FromBody] ChatSessionOverviewRequest request)
    {
        var advanced = await _messageService.MarkSessionsReadAsync(request?.ReadSeqs);
        if (advanced.Count > 0)
        {
            await _pushService.BroadcastSessionReadAsync(advanced);
        }
        return new
        {
            Advanced = advanced.Select(n => new { Session = n.SessionKey, Seq = n.ReadSeq })
        };
    }

    /// <summary>
    /// 会话内分页：取某会话 Seq 倒序的一页（升序返回），首屏加载与向上滚动加载更早历史用（只读）。
    /// </summary>
    /// <param name="session">会话键（空 = 默认会话）</param>
    /// <param name="beforeSeq">取该 Seq 之前（更早）的消息；≤ 0 表示从最新一条开始</param>
    /// <param name="limit">单页条数（默认 50，最大 200）</param>
    [HttpGet("session/messages")]
    public async Task<object> SessionMessages([FromQuery] string session = null, [FromQuery] long beforeSeq = 0,
        [FromQuery] int limit = 50)
    {
        var (messages, hasMore) = await _messageService.SessionPageAsync(session, beforeSeq, limit);
        return new
        {
            HasMore = hasMore,
            Messages = messages
        };
    }

    /// <summary>
    /// 删除会话（2026-09-18 会话分组归并批次；2026-09-21 日志删除逻辑调整）：只删除会话实体
    /// （t_chat_session 行 = 会话数据），**该会话消息行（t_chat_message，业务真实产生的记录）保留**——
    /// 消息记录删除入口只有日志中心（session 空 = 默认会话，与 messages/clear 同风格 query 参数）。
    /// 响应带当前全局最大 Seq：消息不再删除后 Seq 号段不复用，客户端既有的游标回拨
    /// min(当前, MaxSeq) 退化为 no-op，兼容保留。会话删除后，归属任务再推送消息时会话自动重建
    /// （出站落库驱动，重建行预置已读水位，历史消息不计未读）。
    /// </summary>
    [HttpPost("session/delete")]
    public async Task<object> DeleteSession([FromQuery] string session = null)
    {
        var (deleted, maxSeq) = await _messageService.DeleteSessionAsync(session);
        return new
        {
            Deleted = deleted,
            MaxSeq = maxSeq
        };
    }

    /// <summary>
    /// 提交指令/消息（REST 通道，等价 /ws/app 的 command 帧；2026-09-19 起不限流——
    /// 选项可重复点选语义下连点被 2s 窗口误拦，按所有者要求移除，鉴权与指令校验不变）。
    /// 图片/文件消息：Content=FileId、ContentType=image/file（A5.0① 上传后封装）。
    /// </summary>
    [HttpPost("command")]
    public async Task<object> Command([FromBody] AppCommandRequest request)
    {
        var accepted = await _pushService.SubmitCommandAsync(request?.Content, request?.ContentType, request?.ContentText, request?.Session, request?.TargetTaskId);
        return new
        {
            Accepted = accepted,
            Message = accepted ? "指令已提交。" : "指令为空。"
        };
    }

    /// <summary>
    /// 我的多端在线设备列表（平台/机型/最近活跃）
    /// </summary>
    [HttpGet("devices")]
    public Task<List<AppDeviceModel>> Devices()
    {
        return _appAuthService.GetDevicesAsync();
    }

    /// <summary>
    /// 远程下线（解绑）设备
    /// </summary>
    [HttpPost("devices/unbind")]
    public Task<bool> UnbindDevice([FromBody] AppUnbindDeviceRequest request)
    {
        return _appAuthService.UnbindDeviceAsync(request?.DeviceId);
    }

    /// <summary>
    /// 我的通知偏好（无记录时返回默认全开）
    /// </summary>
    [HttpGet("notify-setting")]
    public Task<AppNotifySettingModel> NotifySetting()
    {
        return _notifySettingService.GetAsync();
    }

    /// <summary>
    /// 保存通知偏好（仅影响厂商推送；免打扰时段格式 HH:mm）
    /// </summary>
    [HttpPut("notify-setting")]
    public Task<AppNotifySettingModel> UpdateNotifySetting([FromBody] AppNotifySettingModel setting)
    {
        return _notifySettingService.UpdateAsync(setting ?? new AppNotifySettingModel());
    }

    /// <summary>
    /// 扫码登录管理后台：App 侧管理员（Manager 令牌）授权 Web 登录票据
    /// </summary>
    [HttpPost("scan-login")]
    [ManagerOnly]
    public async Task<object> ScanLogin([FromBody] AppScanLoginRequest request)
    {
        if (string.IsNullOrEmpty(request?.Ticket))
        {
            throw new BusinessException("缺少扫码票据！");
        }
        // 用当前管理员 App 令牌换取等价 Web 管理令牌挂到票据（一次性取走）
        var appToken = HttpContext.Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        var claims = User.Claims.Select(n => new System.Security.Claims.Claim(n.Type, n.Value)).ToList();
        var webToken = JwtTokenIssuer.Issue(claims, DateTime.Now.AddDays(7));
        var authorized = _qrLoginService.Authorize(request.Ticket, webToken);
        if (!authorized)
        {
            throw new BusinessException("扫码票据无效或已过期！");
        }
        await Task.CompletedTask;
        return new { Authorized = true };
    }
}

/// <summary>
/// 扫码登录入参
/// </summary>
public class AppScanLoginRequest
{
    /// <summary>
    /// Web 端 qr-create 生成的票据（二维码内容）
    /// </summary>
    public string Ticket { get; set; }
}

/// <summary>
/// 会话列表快照入参
/// </summary>
public class ChatSessionOverviewRequest
{
    /// <summary>
    /// 各会话的本地已读水位（键 = 会话键，空串 = 默认会话；缺省视为 0 = 全部未读）
    /// </summary>
    public Dictionary<string, long> ReadSeqs { get; set; }
}

/// <summary>
/// App 指令/消息提交入参
/// </summary>
public class AppCommandRequest
{
    /// <summary>
    /// 指令内容（与对话窗口输入一致，支持系统指令/快捷指令/脚本任务）；
    /// 图片/文件消息时为 FileId
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// 内容类型（text/image/file，缺省 text；image/file 时 Content=FileId）
    /// </summary>
    public string ContentType { get; set; }

    /// <summary>
    /// 配文：image/file 消息附带的文字说明（可空；文本消息忽略）
    /// </summary>
    public string ContentText { get; set; }

    /// <summary>
    /// 来源会话键（= 任务 Id 或任务配置的会话名，可空 = 默认会话）：仅用于「接收」行回显与快捷回复归属，
    /// 不影响指令处理本身（任何会话发起的指令走同一处理管道）
    /// </summary>
    public string Session { get; set; }

    /// <summary>
    /// 点选来源任务 Id（可空）：选项消息根部 taskId 由客户端点选代发时透传（手打/重发不带），
    /// 供入站三级路由①级精确路由——命中启用任务且指令匹配时只触发该任务；不落库、不影响回显
    /// </summary>
    public string TargetTaskId { get; set; }
}

/// <summary>
/// 设备解绑入参
/// </summary>
public class AppUnbindDeviceRequest
{
    public string DeviceId { get; set; }
}
