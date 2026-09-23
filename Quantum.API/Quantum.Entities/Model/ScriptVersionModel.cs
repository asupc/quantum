using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 脚本版本历史（2026-09-20 新增，AI 脚本修复 Agent 计划阶段一）：
/// 每次脚本落盘（在线编辑保存 / 上传 / Agent 应用 / 回滚）记一条内容快照，供版本页查看、对比与回滚。
/// 当前版本始终以磁盘文件为准，本表只存历史——故删除脚本文件不清理版本记录（仍可查看/恢复）。
/// </summary>
[Table("t_script_version")]
public class ScriptVersionModel : BaseModel
{
    /// <summary>
    /// 脚本文件相对路径（相对 scripts/quantum 根，统一 / 分隔，如 B站任务.cs 或 open-trigger-task/x.cs）
    /// </summary>
    public string FileName { get; set; }

    /// <summary>
    /// 内容哈希（SHA256 大写十六进制，尾部换行不计入，与编译缓存源码哈希同款口径）
    /// </summary>
    public string Hash { get; set; }

    /// <summary>
    /// 源码全文
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// 字节数（UTF-8）
    /// </summary>
    public int Size { get; set; }

    /// <summary>
    /// 行数
    /// </summary>
    public int LineCount { get; set; }

    /// <summary>
    /// 来源（见 <see cref="ScriptVersionSource"/>）
    /// </summary>
    public string Source { get; set; }

    /// <summary>
    /// 备注（回滚/Agent 应用等场景的来源说明）
    /// </summary>
    public string Remark { get; set; }

    /// <summary>
    /// 操作人
    /// </summary>
    public string Creator { get; set; }

    /// <summary>
    /// 关联任务 Id（可空；由任务上下文写入时记录）
    /// </summary>
    public string TaskId { get; set; }

    /// <summary>
    /// 锁定：锁定的版本不被每文件保留数清理（保留策略见 ScriptVersionService.KeepPerFile）
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreateTime { get; set; }
}

/// <summary>
/// 脚本版本来源（字符串常量入库：新来源可直接追加，无需迁移改列型）
/// </summary>
public static class ScriptVersionSource
{
    /// <summary>在线编辑保存（PUT /api/Task/scripts）</summary>
    public const string ManualEdit = "ManualEdit";

    /// <summary>上传覆盖（POST /api/Upload/scripts）</summary>
    public const string Upload = "Upload";

    /// <summary>AI Agent 应用提案</summary>
    public const string AgentApply = "AgentApply";

    /// <summary>回滚（版本页/AI 应用前回退）</summary>
    public const string Rollback = "Rollback";

    /// <summary>任务导入等批量写入</summary>
    public const string Import = "Import";

    /// <summary>AI 删除脚本留底（delete_script 工具；force 绕过哈希去重，保证删除动作在版本表留有溯源行）</summary>
    public const string AgentDelete = "AgentDelete";
}
