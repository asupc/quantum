using Quantum.Entities.Model;

namespace Quantum.Entities.DTOs;

public class SendNotifyDTO
{
    /// <summary>
    /// 通讯类型
    /// </summary>
    public CommunicationType? CommunicationType { get; set; }

    /// <summary>
    /// 通讯id
    /// </summary>
    public string CommunicationId { get; set; }

    /// <summary>
    /// 消息内容
    /// </summary>
    public string message { get; set; }

    /// <summary>
    /// 配文：图片/视频消息附带的文字说明（App 会话同气泡展示），纯文本消息忽略
    /// </summary>
    public string message_text { get; set; }

    /// <summary>
    /// 是否文字转图片(暂未实现)
    /// </summary>
    public bool TextToPicture { get; set; }

    /// <summary>
    /// 会话键（= 任务 Id 或任务配置的会话名）：任务执行门面注入（会话名非空时为会话名 Trim，否则任务 Id），
    /// 标记消息归属的客户端会话；null = 默认会话
    /// </summary>
    public string SessionKey { get; set; }

    /// <summary>
    /// 结构化富交互载荷（JSON，可空）：由 QuantumNotifyFacade 组装（可点选项/视频封面），
    /// 经消息泵透传到 t_chat_message.Payload 与 WS 帧 payload 字段
    /// </summary>
    public string Payload { get; set; }

    /// <summary>
    /// 用户id
    /// </summary>
    public string user_id { get; set; }

    /// <summary>
    /// 分组id
    /// </summary>
    public string group_id { get; set; }

    /// <summary>
    /// 消息标题
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// 消息内容
    /// </summary>
    public MessageType? MessageType { get; set; }
}
