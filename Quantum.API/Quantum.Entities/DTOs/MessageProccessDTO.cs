using Quantum.Entities.Model;

namespace Quantum.Entities.DTOs;

public class MessageProccessDTO
{
    public CommunicationType CommunicationType { get; set; }

    /// <summary>
    /// 用户id
    /// </summary>
    public string user_id { get; set; }

    public string user_name { get; set; }

    /// <summary>
    /// 群组id
    /// </summary>
    public string group_id { get; set; }

    /// <summary>
    /// 消息内容
    /// </summary>
    public string message { get; set; }

    /// <summary>
    /// 消息id
    /// </summary>
    public string message_id { get; set; }

    /// <summary>
    /// 配文：图片/文件消息附带的文字说明（可与正文同气泡展示），纯文本消息忽略
    /// </summary>
    public string message_text { get; set; }

    public bool textToPic { get; set; }

    public MessageType MessageType { get; set; }

    /// <summary>
    /// 会话键（= 任务 Id 或任务配置的会话名）：任务脚本经 ctx.Notify 发消息时由门面注入，随队列透传到落库；
    /// null = 默认会话。入站指令提交时可携带来源会话（App 会话详情页/选项点选），仅用于
    /// 会话内优先路由与回复归属回显，不影响落库内容。
    /// </summary>
    public string SessionKey { get; set; }

    /// <summary>
    /// 点选来源任务 Id（可空）：仅入站指令「点选精确路由」用——选项消息根部 taskId 由客户端
    /// 点选代发时透传，命中启用任务且指令匹配时只触发该任务；出站克隆（SendMessage）必须置空，
    /// 防止任务回复文本匹配自身指令形成自触发回路。
    /// </summary>
    public string TargetTaskId { get; set; }

    /// <summary>
    /// 结构化富交互载荷（JSON，可空）：随队列透传到 t_chat_message.Payload（可点选项/视频封面）
    /// </summary>
    public string Payload { get; set; }

    /// <summary>
    /// 手写出站克隆：与 JSON 深拷贝语义等价——本 DTO 字段全部为 string/值类型，无可变引用成员。
    /// 替代 JsonConvert 序列化+反序列化的整体克隆（每条出站消息一次反射双份分配）。
    /// </summary>
    public MessageProccessDTO Clone()
    {
        return new MessageProccessDTO
        {
            CommunicationType = CommunicationType,
            user_id = user_id,
            user_name = user_name,
            group_id = group_id,
            message = message,
            message_id = message_id,
            message_text = message_text,
            textToPic = textToPic,
            MessageType = MessageType,
            SessionKey = SessionKey,
            TargetTaskId = TargetTaskId,
            Payload = Payload
        };
    }
}
