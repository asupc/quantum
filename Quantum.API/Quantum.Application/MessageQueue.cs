using log4net;
using Microsoft.Extensions.DependencyInjection;
using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Utils;
using System.Collections.Concurrent;

namespace Quantum.Application;

/// <summary>
/// 进程内消息队列：专用消费线程逐条取出，每条消息在独立线程上异步处理（互不阻塞、保持顺序间隔）。
/// </summary>
public static class MessageQueue
{

    private static readonly ILog Log = LogManager.GetLogger("NETCoreRepository", typeof(MessageQueue));

    /// <summary>
    /// 入站队列上限：消费线程固定 Sleep 节流，外部触发洪峰时只增不减，
    /// 超容量丢最旧并 Error 留痕（与日志/出站队列同款同量）。
    /// </summary>
    private const int MaxQueueLength = 50_000;

    public static BoundedConcurrentQueue<MessageProccessDTO> MessageQueues { get; set; }

    private static IServiceProvider _serviceProvider;

    private static Setting _setting;

    public static void InitMessageQueue(this IServiceProvider serviceProvider)
    {
        MessageQueues = new BoundedConcurrentQueue<MessageProccessDTO>(MaxQueueLength);
        MessageQueues.OnDropped = item => LogServiceHelper.Error("App 入站消息队列溢出（丢弃最旧）",
            @$"来源：【{item?.CommunicationType}】，用户：【{item?.user_name}】，消息：【{item?.message}】", "MessageQueue");
        _serviceProvider = serviceProvider;
        _setting = SystemConfigHelper.GetSetting();
        ExecTask();
    }

    static void ExecTask()
    {
        Thread thread = new(() =>
        {
            while (true)
            {
                if (MessageQueues.TryDequeue(out MessageProccessDTO message))
                {
                    Log.Info($@"开始处理消息，来源：【{message.CommunicationType}】，用户：【{message.user_name}】，消息：【{message.message}】，队列剩余：【{MessageQueues.Count}】");
                    // async Task：异常在 Start 内部兜底记录；Task.Run 并行处理不阻塞消费循环
                    async Task Start()
                    {
                        try
                        {
                            var messageProces = _serviceProvider.GetService<MessageProcess>();
                            await messageProces.MessageAsync(message);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex);
                        }
                    }

                    _ = Task.Run(Start);
                    Thread.Sleep(Math.Max(1, _setting.MessageQueueInterval));
                }
                else
                {
                    _setting = SystemConfigHelper.GetSetting();
                    Thread.Sleep(Math.Max(1, _setting.MessageQueueInterval * 2));
                }
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
    }
}
