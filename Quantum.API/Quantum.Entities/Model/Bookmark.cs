using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 书签实体
/// </summary>
[Table("t_bookmark")]
public class Bookmark : BaseModel
{
    /// <summary>
    /// 标题
    /// </summary>
    [Required]
    public string Title { get; set; }

    /// <summary>
    /// URL链接
    /// </summary>
    public string Url { get; set; }

    /// <summary>
    /// 图标URL
    /// </summary>
    public string IconUrl { get; set; }

    /// <summary>
    /// 描述信息
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// 父级书签ID（用于文件夹层级结构）
    /// </summary>
    public string ParentId { get; set; }

    /// <summary>
    /// 排序字段
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 是否为文件夹
    /// </summary>
    public bool IsFolder { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}