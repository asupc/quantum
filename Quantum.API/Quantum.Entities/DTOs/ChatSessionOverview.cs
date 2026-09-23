namespace Quantum.Entities.DTOs;

using Quantum.Entities.Model;

/// <summary>
/// 会话列表快照项（多端会话列表：Web 管理端首屏 / App 可复用）。
/// 未读语义与 App 本地一致：机器人下发且该会话内 Seq 大于调用方水位。
/// </summary>
public class ChatSessionOverview
{
    /// <summary>
    /// 会话键（= 任务 Id 或任务配置的会话名；空串 = 默认会话）
    /// </summary>
    public string SessionKey { get; set; } = "";

    /// <summary>
    /// 该会话消息总数
    /// </summary>
    public long Total { get; set; }

    /// <summary>
    /// 未读数（Direction=发送 且 Seq 大于调用方声明的该会话水位）
    /// </summary>
    public long Unread { get; set; }

    /// <summary>
    /// 服务端权威已读水位（2026-09-21 双端同步批次）：= 合并钳制后的 effective 值
    /// （min(max(服务端, 调用方上报), LastSeq)）。客户端吸收它只进推进本地水位，
    /// 即完成对端离线期间已读进度的追赶。
    /// </summary>
    public long ReadSeq { get; set; }

    /// <summary>
    /// 该会话最后一条消息（会话内最新 Seq）
    /// </summary>
    public ChatMessageModel Last { get; set; }
}
