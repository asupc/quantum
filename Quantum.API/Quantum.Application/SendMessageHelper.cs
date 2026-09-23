using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using System.Collections.Concurrent;

namespace Quantum.Application;

public static class SendMessageHelper
{
    /// <summary>
    /// 出站队列上限：泵限速（默认 100ms/条）下脚本群发快于消费时只增不减，
    /// 超容量丢最旧并 Error 留痕（与日志队列同款同量），优于链路停摆全丢。
    /// </summary>
    private const int MaxQueueLength = 50_000;

    private static readonly BoundedConcurrentQueue<MessageProccessDTO> MessageProccesses;


    static SendMessageHelper()
    {
        MessageProccesses = new BoundedConcurrentQueue<MessageProccessDTO>(MaxQueueLength);
        MessageProccesses.OnDropped = item => LogServiceHelper.Error("App 出站消息队列溢出（丢弃最旧）",
            @$"消息内容：{item?.message}
消息类型：{item?.MessageType}
会话键：{item?.SessionKey}", "SendMessageHelper");
        Start();
    }

    /// <summary>
    /// 发送接缝（测试可替换）：泵每条消息经此发出。
    /// </summary>
    internal static Func<MessageProccessDTO, Task> Sender = Send;

    private static void Start()
    {
        // 消息泵全程异步等待：不再占用线程池线程空转
        _ = Task.Run(async () =>
        {
            while (true)
            {
                // 整轮兜底：单条发送失败（DB 抖动/Seq 撞车重试耗尽等）只记日志继续，
                // 异常逃出循环会让这个唯一的泵 Task 静默 faulted，此后 App 通道全部下行消息永久丢失直到重启
                try
                {
                    await ProcessNextAsync();
                    var setting = SystemConfigHelper.GetSetting();
                    await Task.Delay((setting.MessageInterval > 0 ? setting.MessageInterval : 1) * 100);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"消息泵本轮处理异常（已跳过，继续运行）：{e}");
                    await Task.Delay(1000);
                }
            }
        });
    }

    /// <summary>
    /// 取一条消息并安全发送：独立成方法便于单测异常隔离语义。返回是否取到了消息。
    /// </summary>
    internal static async Task<bool> ProcessNextAsync()
    {
        if (!MessageProccesses.TryDequeue(out var message))
        {
            return false;
        }
        await SendSafeAsync(message);
        return true;
    }

    /// <summary>
    /// 单条发送 + 异常吞掉记日志：发送失败（DB 抖动/Seq 撞车重试耗尽等）绝不向泵循环传播——
    /// 异常逃出会让唯一的泵 Task 静默 faulted，此后 App 通道全部下行消息永久丢失直到重启。
    /// 但也不能只写控制台：失败等于这条消息被丢弃，必须写日志中心留痕（否则现场表现为"机器人没回复"且无据可查）。
    /// </summary>
    internal static async Task SendSafeAsync(MessageProccessDTO message)
    {
        try
        {
            await Sender(message);
        }
        catch (Exception e)
        {
            Console.WriteLine($"消息发送失败（已丢弃）：{e}");
            LogServiceHelper.Error("App 消息发送失败（已丢弃）",
                @$"推送方式：{message?.CommunicationType}
消息内容：{message?.message?.RemoveEmoji()}
用户：{message?.user_id}",
                "SendMessageHelper", e.ToString());
        }
    }

    private static async Task Send(MessageProccessDTO messageProccess)
    {
        LogModel log = new()
        {
            CreateTime = DateTime.Now,
            Id = Guid.NewGuid().ToString(),
            LogType = LogType.通知消息,
            Operator = "System",
            Success = true,
            Title = "消息通知日志",
            Remark = @$"推送方式：{messageProccess.CommunicationType}
消息内容：{messageProccess.message.RemoveEmoji()}
消息类型：{messageProccess.MessageType}
群ID：{messageProccess.group_id}
接收人：{messageProccess.user_id}"
        };

        // 旧通道（QQ/公众号/WxPusher/微信/Web）已移除：所有会话消息统一走 App 管道
        // （落库 t_chat_message + WS 在线直推/离线厂商推送）
        var appContentType = messageProccess.MessageType switch
        {
            MessageType.图片 => "image",
            // 视频消息用独立 video 类型（App 端内全屏播放；旧消息的 file 由扩展名兜底路由）
            MessageType.视频 => "video",
            MessageType.音频 => "audio",
            _ => "text"
        };
        await AppPushDispatcher.SendChatMessageAsync(messageProccess.message, appContentType,
            messageProccess.message_text, messageProccess.SessionKey, messageProccess.Payload);
        log.Remark += "\r\n通知结果：App推送。";
        LogServiceHelper.Logs.Enqueue(log);
    }


    public static void SendMessage(this MessageProccessDTO messageProccess, string message, bool textToPic = false)
    {
        var ttt = messageProccess.Clone();
        ttt.message = message;
        ttt.textToPic = false;
        // P3 防自回路：出站克隆必须显式置空点选来源任务——JSON 整体克隆会把入站的 TargetTaskId 一并
        // 传播到出站消息，任务回复文本若匹配自身指令，会经①级精确路由形成「点选→任务→回复→再触发」循环
        //（与上方置空 textToPic 同一处理方式，克隆后仅保留会话键用于回复归属回显）
        ttt.TargetTaskId = null;
        MessageProccesses.Enqueue(ttt);
    }
}
