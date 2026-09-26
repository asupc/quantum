namespace Quantum.Plugins;

/// <summary>
/// Docker 容器运维门面（2026-09-24 新增，ACME 证书续期计划批次 1）：
/// 任务脚本可用的容器操作收敛到「重启 + 探活」两件事——证书落盘后重载 nginx 是本门面的来由。
/// 停止/删除/exec/镜像/网络/卷一律不放开：脚本经 docker.sock 能做的事越多，
/// 脚本源码外泄或被误用时的爆炸半径越大。重启目标应取自环境变量（勿硬编码），结果应回显到通知。
/// </summary>
public interface IQuantumDocker
{
    /// <summary>
    /// 按容器名（或容器 Id）重启容器；waitBeforeKillSeconds 为发出强制终止前的优雅停止等待秒数。
    /// 容器不存在或守护进程不可达时抛 <see cref="Exception"/>（中文文案含容器名，便于任务日志定位）。
    /// </summary>
    Task RestartAsync(string container, uint waitBeforeKillSeconds = 10, CancellationToken ct = default);

    /// <summary>查询容器是否处于运行中。容器不存在返回 false（不抛），供重启后复核用。</summary>
    Task<bool> IsRunningAsync(string container, CancellationToken ct = default);
}
