using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

[Table("t_custom_data_title")]
public class CustomDataTitleModel : BaseModel
{
    public string Type { get; set; }
    public string TypeName { get; set; }
    public string Title1 { get; set; }
    public string Title2 { get; set; }
    public string Title3 { get; set; }
    public string Title4 { get; set; }
    public string Title5 { get; set; }
    public string Title6 { get; set; }
    public string Title7 { get; set; }
    public string Title8 { get; set; }
    public string Title9 { get; set; }
    public string Title10 { get; set; }
    public string Title11 { get; set; }
    public string Title12 { get; set; }
    public string Title13 { get; set; }
    public string Title14 { get; set; }
    public string Title15 { get; set; }

    public bool Hide { get; set; }
}
