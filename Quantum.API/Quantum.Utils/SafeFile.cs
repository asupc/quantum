namespace Quantum.Utils;

/// <summary>
/// 「固定根目录 + 用户输入」拼接路径的统一防线。
/// 所有以用户输入参与拼路径的文件读/写/删，落盘前必须经 <see cref="Resolve"/> 越狱校验，
/// 杜绝 "../" 目录穿越（历史漏洞：脚本读写删、上传接口均曾可直接逃出根目录）。
/// </summary>
public static class SafeFile
{
    /// <summary>
    /// 把用户输入解析到 root 目录内部；输入为空、含非法路径字符或解析结果越出 root 时返回 null。
    /// </summary>
    public static string Resolve(string root, string userInput)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(userInput))
        {
            return null;
        }
        var fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar) && !fullRoot.EndsWith(Path.AltDirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(fullRoot, userInput.TrimStart('/', '\\')));
        }
        catch (Exception)
        {
            // 非法路径字符（Windows 保留名、非法符号等）
            return null;
        }
        return candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    /// <summary>
    /// 校验文件扩展名在白名单内（大小写不敏感）。
    /// </summary>
    public static bool IsExtensionAllowed(string fileName, IEnumerable<string> allowedExtensions)
    {
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension)
            && allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
