namespace Quantum.Utils;

/// <summary>
/// 下载根目录内的受控相对路径解析：ctx.File 落盘门面与 AppMedia 流媒体通道共用，
/// 杜绝两套防穿越逻辑各自漂移。两类用法：
/// - <see cref="ResolveDirectory"/>：门面写盘前的子目录校验（保持脚本可读的 BusinessException 文案）；
/// - <see cref="TryResolveFile"/>：AppMedia 读盘的完整相对文件路径校验（目录段 + 文件名段），
///   任何校验失败统一返回 false，不区分原因——调用方对外一律按「不存在/不可访问」处理，避免泄露判定细节。
/// </summary>
public static class SafeMediaPath
{
    /// <summary>Windows 保留设备名（CON.mp3 一类文件名在 Windows 上会命中设备命名空间，必须拒绝）。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>配置的下载根目录 → 规范化绝对路径（空/空白 = downloads，与门面缺省一致）。</summary>
    public static string ResolveRoot(string configuredRoot)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot) ? "downloads" : configuredRoot.Trim());
    }

    /// <summary>
    /// 校验并解析子目录：只允许根内相对路径（/ 或反斜杠分隔），拒绝绝对路径、盘符与 .. 穿越。
    /// 校验失败抛 <see cref="BusinessException"/>（脚本侧可读文案）。
    /// </summary>
    public static string ResolveDirectory(string rootFullPath, string subDir)
    {
        if (string.IsNullOrWhiteSpace(subDir))
        {
            return rootFullPath;
        }
        var raw = subDir.Trim();
        if (Path.IsPathRooted(raw) || raw.Contains(':'))
        {
            throw new BusinessException($"文件子目录只允许相对路径：{subDir}");
        }
        var normalized = raw.Replace('\\', '/');
        if (normalized.Contains(".."))
        {
            throw new BusinessException($"文件子目录不允许包含 ..：{subDir}");
        }
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p != ".")
            .ToArray();
        var combined = Path.GetFullPath(parts.Length == 0 ? rootFullPath : Path.Combine([rootFullPath, .. parts]));
        var rootWithSep = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException($"文件子目录越出下载根目录：{subDir}");
        }
        return combined;
    }

    /// <summary>
    /// 校验并解析完整相对文件路径（含文件名）。校验项：拒绝绝对路径/盘符、. 与 .. 段、
    /// 段内非法字符、段首尾空白与结尾点（Windows 截断语义）、保留设备名；最终路径必须仍在根内。
    /// 通过则输出绝对路径。
    /// </summary>
    public static bool TryResolveFile(string rootFullPath, string relativePath, out string absolutePath)
    {
        absolutePath = null;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }
        var raw = relativePath.Trim();
        if (Path.IsPathRooted(raw) || raw.Contains(':'))
        {
            return false;
        }
        var segments = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }
        // 目标是文件：末段必须带扩展名（纯目录路径不是可下发的文件；白名单外扩展名由调用方判定）
        if (!segments[^1].Contains('.'))
        {
            return false;
        }
        foreach (var segment in segments)
        {
            if (segment == "." || segment == "..")
            {
                return false;
            }
            if (segment != segment.Trim() || segment.EndsWith('.'))
            {
                return false;
            }
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }
            // Windows 保留设备名按「首个点之前的基名」判定：CON.mp3 / nul.flac 同样命中设备命名空间
            if (ReservedNames.Contains(segment.Split('.')[0]))
            {
                return false;
            }
        }
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine([rootFullPath, .. segments]));
        }
        catch (Exception)
        {
            return false;
        }
        var rootWithSep = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        absolutePath = combined;
        return true;
    }
}
