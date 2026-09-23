using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

[Table("t_env")]
public class EnvModel : BaseModel
{
    /// <summary>
    /// 变量名称
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// 变量值
    /// </summary>
    public string Value { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enable { get; set; }

    /// <summary>
    /// 备注
    /// </summary>
    public string Remark { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreateTime { get; set; }

    /// <summary>
    /// 更新时间    
    /// </summary>
    public DateTime? UpdateTime { get; set; }

}

