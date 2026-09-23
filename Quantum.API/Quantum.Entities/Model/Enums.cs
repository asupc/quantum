namespace Quantum.Entities.Model;


public enum AuthType
{
    Permanent = 1,
    Month = 2,
    Year = 3,
    TheTrial = 4
}

/// <summary>
/// 通讯类型。旧通道（QQ/公众号/WxPusher/微信/Web-Chat）已移除：
/// 枚举值 1-5 仅作历史数据兼容（任务 CommunicationTypes 过滤、日志展示），运行时统一路由 App 通道。
/// </summary>
public enum CommunicationType
{
    /// <summary>QQ 通道（已移除）</summary>
    QQ = 1,
    /// <summary>公众号通道（已移除）</summary>
    公众号 = 2,
    /// <summary>WxPusher 通道（已移除）</summary>
    WxPusher = 3,
    /// <summary>微信 VLW/千寻通道（已移除）</summary>
    微信 = 4,
    /// <summary>Web-Chat 通道（已移除）</summary>
    Web = 5,

    /// <summary>
    /// 安卓 App 通道（WS 在线直推 + 离线厂商推送）：唯一在用通道
    /// </summary>
    App = 6
}

/// <summary>
/// 任务状态
/// </summary>
public enum TaskStatus
{
    运行中 = 1,
    空闲 = 2
}

/// <summary>
/// 消息类型
/// </summary>
public enum MessageType
{
    文本 = 1,
    图片 = 2,
    视频 = 3,
    音频 = 4
}

/// <summary>
/// 任务执行类型
/// </summary>
public enum LogType
{
    任务日志 = 1,
    通知消息 = 2,
    操作日志 = 3,
    登录授权 = 4,
    指令触发 = 5,
    外触内执 = 6,
    系统日志 = 7,

    /// <summary>AI Agent 分析运行（2026-09-20 追加；枚举按 int 存储，追加不影响存量行）</summary>
    AI助手 = 8,

    /// <summary>AI 提案的影子试运行（日志详情走既有 GET /api/logs/details/{id}）</summary>
    AI试运行 = 9
}
