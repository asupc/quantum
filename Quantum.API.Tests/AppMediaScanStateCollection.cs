using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// AppMediaService 扫描缓存是进程级单槽静态（根目录+结果+30s 时间窗）：AppMediaServiceTests 每用例重建临时目录，
/// 依赖「ctor 失效 → 首扫建缓存 → 窗口内读旧值」断言；而 QuantumFileFacadeTests 驱动真实
/// QuantumFileFacade.DownloadAsync，落盘成功即调 InvalidateScanCache 清槽——两类并行时清槽恰落在
/// 断言窗口内会偶发重扫命中新落盘文件（2026-09-19 验收首跑实测：期望 2 实际 3）。
/// 与 ConstsState 同款 DisableParallelization 集合，将两者串行化以消除竞态。
/// </summary>
[CollectionDefinition("AppMediaScanState", DisableParallelization = true)]
public class AppMediaScanStateCollection
{
}
