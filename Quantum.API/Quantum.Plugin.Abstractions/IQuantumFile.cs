namespace Quantum.Plugins;

/// <summary>
/// 受控文件落盘门面（2026-09-18 新增）：任务脚本产物文件落盘的唯一合法通道。
/// System.IO 在脚本门禁中仍被禁用；本门面把可写范围限定在配置的下载根目录内
/// （appsettings.json Quantum:FileDownloadRoot，缺省 ./downloads，相对运行目录），
/// 子目录只允许相对路径且禁止穿越，文件名自动清洗非法字符。
/// </summary>
public interface IQuantumFile
{
    /// <summary>
    /// 下载 url 内容并保存到「下载根目录/subDir/fileName」。
    /// subDir 可空（存根目录），只允许根内相对子路径；fileName 自动清洗非法字符并限长；
    /// 已存在同名文件时自动追加「 (n)」序号，不覆盖既有文件。
    /// 网络失败/取消时清理未写完的残片。返回最终落盘全路径与字节数。
    /// </summary>
    Task<QuantumFileResult> DownloadAsync(string url, string fileName, string subDir = null, CancellationToken ct = default);

    /// <summary>
    /// 把脚本自身产出的文本**覆盖**写入「下载根目录/subDir/fileName」（ACME 证书等需原地更新的产物用本通道，
    /// DownloadAsync 的重名追加序号语义对这类产物不适用）。约束与 DownloadAsync 同源：subDir 只允许根内相对路径、
    /// fileName 自动清洗；先写临时文件再原子替换，读侧不会看到半截内容。内容上限 1 MiB。
    /// </summary>
    Task<QuantumFileResult> SaveTextAsync(string content, string fileName, string subDir = null, CancellationToken ct = default);
}

/// <summary>文件落盘结果：FullPath 最终落盘全路径、FileName 清洗后的文件名、Length 字节数、
/// RelativePath 相对下载根目录的相对路径（/ 分隔；M2 起音频气泡经 AppMedia 通道回推使用）。</summary>
public sealed record QuantumFileResult(string FullPath, string FileName, long Length, string RelativePath = null);
