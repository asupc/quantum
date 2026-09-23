using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

[Table("t_custom_data")]
public class CustomDataModel : BaseModel
{
    /// <summary>
    /// 数据类型标识
    /// </summary>

    public string Type { get; set; }
    public string Data1 { get; set; }
    public string Data2 { get; set; }
    public string Data3 { get; set; }
    public string Data4 { get; set; }
    public string Data5 { get; set; }
    public string Data6 { get; set; }
    public string Data7 { get; set; }
    public string Data8 { get; set; }
    public string Data9 { get; set; }
    public string Data10 { get; set; }
    public string Data11 { get; set; }
    public string Data12 { get; set; }
    public string Data13 { get; set; }
    public string Data14 { get; set; }
    public string Data15 { get; set; }

    /// <summary>
    /// 数据创建时间
    /// </summary>
    public DateTime CreateTime { get; set; }

    /// <summary>
    /// 数据更新时间
    /// </summary>
    public DateTime UpdateTime { get; set; }
}
