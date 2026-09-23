using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 菜单实体 - 支持树形结构
/// </summary>
[Table("t_menu")]
public class MenuModel : BaseModel
{
    /// <summary>
    /// 路由路径
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 路由名称（唯一标识）
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 组件路径（相对于 view 目录，如 "setting/index"）
    /// </summary>
    public string Component { get; set; } = string.Empty;

    /// <summary>
    /// 父菜单名称（顶级菜单为空）
    /// </summary>
    public string ParentName { get; set; }

    /// <summary>
    /// 排序号（数字越小越靠前）
    /// </summary>
    public int Sort { get; set; }

    /// <summary>
    /// 是否在菜单中隐藏
    /// </summary>
    public bool HideInMenu { get; set; }

    /// <summary>
    /// 菜单标题
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 图标类名
    /// </summary>
    public string Icon { get; set; }

    /// <summary>
    /// 是否在面包屑中隐藏
    /// </summary>
    public bool HideInBread { get; set; }

    /// <summary>
    /// 是否为默认首页
    /// </summary>
    public bool IsDefault { get; set; }
}
