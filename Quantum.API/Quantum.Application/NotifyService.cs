using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

public class NotifyService
{
    /// <summary>结构化载荷长度上限（32KB）：防超大帧（WS 单帧 256KB 限制的前置闸）。</summary>
    private const int MaxPayloadLength = 32 * 1024;

    /// <summary>
    /// 发送消息（入口校验：空消息直接拒绝）
    /// </summary>
    public bool Send(SendNotifyDTO sendNotify)
    {
        if (string.IsNullOrEmpty(sendNotify.message))
        {
            throw new BusinessException("尝试发送空消息，已跳过。");
        }
        if (sendNotify.Payload != null && sendNotify.Payload.Length > MaxPayloadLength)
        {
            throw new BusinessException($"消息结构化载荷超过 {MaxPayloadLength / 1024}KB 上限。");
        }
        return SendMessage(sendNotify);
    }

    public bool SendMessage(SendNotifyDTO sendNotify)
    {
        if (string.IsNullOrEmpty(sendNotify.message))
        {
            return false;
        }

        // 旧通道（QQ/公众号/WxPusher/微信/Web）已移除：所有通知统一走 App 会话管道
        // （落库 t_chat_message + WS 广播/离线厂商推送）；单管理员体系下入参 user_id 忽略，
        // 统一投递到管理员会话
        var messageProccess = new MessageProccessDTO
        {
            group_id = sendNotify.group_id,
            SessionKey = sendNotify.SessionKey,
            Payload = sendNotify.Payload,
            user_id = SystemConfigHelper.GetSetting()?.UserName ?? "admin",
            message_text = sendNotify.message_text,
            MessageType = sendNotify.MessageType ?? MessageType.文本,
            CommunicationType = CommunicationType.App
        };
        messageProccess.SendMessage(sendNotify.message, sendNotify.TextToPicture);
        return true;
    }
}
