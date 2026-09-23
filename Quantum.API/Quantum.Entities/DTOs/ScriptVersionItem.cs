namespace Quantum.Entities.DTOs;

/// <summary>
/// 脚本版本列表项（不含正文——正文动辄数十 KB，走详情端点按需取）
/// </summary>
public class ScriptVersionItem
{
    public string Id { get; set; }

    public string FileName { get; set; }

    /// <summary>内容哈希（SHA256 大写十六进制）</summary>
    public string Hash { get; set; }

    /// <summary>字节数（UTF-8）</summary>
    public int Size { get; set; }

    /// <summary>行数</summary>
    public int LineCount { get; set; }

    /// <summary>来源（见 ScriptVersionSource）</summary>
    public string Source { get; set; }

    public string Remark { get; set; }

    public string Creator { get; set; }

    public string TaskId { get; set; }

    public bool IsPinned { get; set; }

    public DateTime CreateTime { get; set; }
}

/// <summary>
/// 有版本记录的脚本文件（版本页左侧筛选列表）
/// </summary>
public class ScriptVersionFileItem
{
    public string FileName { get; set; }

    /// <summary>版本条数</summary>
    public int Count { get; set; }

    /// <summary>最近一次版本时间</summary>
    public DateTime LastTime { get; set; }
}
