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
/// </summary>
public class AppWebSocketHeartbeatService : BackgroundService
{
    private static readonly ILog Log = LogManager.GetLogger("NETCoreRepository", typeof(AppWebSocketHeartbeatService));

    /// <summary>
    /// 无任何帧即判死（与客户端 25s 应用层心跳留约 2 倍余量，容忍 Doze/网络抖动）。
    /// internal 便于单测缩短。
    /// </summary>
    internal static TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>巡检节拍：明显小于 HeartbeatTimeout，保证死连接最迟在超时 + 一个节拍内被踢。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(20);

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
                foreach (var dead in _manager.CollectTimeoutSockets(HeartbeatTimeout))
                {
                    try
                    {
                        await dead.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "heartbeat timeout", CancellationToken.None);
                    }
                    catch
                    {
                        // 连接已死，忽略关闭异常
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主正常停机
        }
    }
}
