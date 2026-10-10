using System.Net.WebSockets;
using log4net;
using Microsoft.Extensions.Hosting;

namespace Quantum.Application;

/// <summary>
/// App WebSocket 心跳全局巡检（§1-12）：替代原先「每连接一个 Task.Delay 循环」——N 条连接即 N 个
/// 长驻定时器，各自独立唤醒扫描整张注册表（O(N²) 且连接销毁前不会停）。合并为进程内单个 PeriodicTimer，
/// 按 ScanInterval 全局扫一遍，踢掉超过 HeartbeatTimeout 无任何帧的连接。
/// 关闭码沿用 1001（EndpointUnavailable）而非 NormalClosure(1000)：客户端把 1000 视为服务端正常关闭
/// 而放弃重连，心跳踢除恰恰是「应该立即重连」的异常断开。
/// 关闭**必须有界**：黑洞对端（TCP 零窗口/不完成关闭握手）会让 CloseAsync 无限挂起，
/// 一次挂起即让整个 PeriodicTimer 循环体不再返回——此后所有巡检停摆、死连接只增不减。
/// </summary>
public class AppWebSocketHeartbeatService : BackgroundService
{
    private static readonly ILog Log = LogManager.GetLogger("NETCoreRepository", typeof(AppWebSocketHeartbeatService));

    /// <summary>
    /// 无任何帧即判死（与客户端 25s 应用层心跳留约 2 倍余量，容忍 Doze/网络抖动）。
    /// internal 便于单测缩短。
    /// </summary>
    internal static TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 单连接关闭握手的时间上限：超时即 Abort 强制拆链（与 AppWebSocketManager.SendTimeout 同一类防线）。
    /// 不设界时黑洞对端能让 CloseAsync 永久挂起，一次挂起即停摆全部后续巡检。
    /// internal 便于单测缩短。
    /// </summary>
    internal static TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>巡检节拍：明显小于 HeartbeatTimeout，保证死连接最迟在超时 + 一个节拍内被踢。internal 便于单测缩短。</summary>
    internal static TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(20);

    private readonly AppWebSocketManager _manager;

    public AppWebSocketHeartbeatService(AppWebSocketManager manager)
    {
        _manager = manager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnceAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主正常停机
        }
    }

    /// <summary>
    /// 扫描一遍注册表并踢除超时连接（抽成方法供单测直接驱动，不依赖定时器节拍）。
    /// 逐条关闭、异常隔离：任何一条连接关闭失败/挂起都不得影响其余连接的处置。
    /// </summary>
    internal async Task SweepOnceAsync()
    {
        foreach (var dead in _manager.CollectTimeoutSockets(HeartbeatTimeout))
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(CloseTimeout);
                // 双界收敛：既传令牌（真实 WebSocket 实现据此中止关闭握手），又与超时任务赛跑——
                // 单传令牌不足以设界，关闭实现可以无视令牌永久挂起（黑洞对端），那样一次挂起仍会停摆整轮巡检。
                var close = dead.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "heartbeat timeout", timeoutCts.Token);
                if (await Task.WhenAny(close, Task.Delay(CloseTimeout, timeoutCts.Token)) != close)
                {
                    // 超时：先把挂起的关闭任务转后台观察（否则异常未观察会被吞/上报），再 Abort 强制拆链
                    _ = close.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException($"关闭握手超过 {CloseTimeout.TotalSeconds}s 未完成");
                }
                await close;
                timeoutCts.Cancel(); // 提前收掉超时任务，不留下多余的计时器
            }
            catch
            {
                // 关闭失败或超时：Abort 强制拆链，否则套接字与对端资源都留着（连接已移出注册表，不会再被巡检扫到）
                try
                {
                    dead.Abort();
                }
                catch
                {
                    // Abort 本身失败（套接字已释放等）不再冒泡，继续处置下一条
                }
            }
        }
    }
}
