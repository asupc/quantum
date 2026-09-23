using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

public class BaseModel
{
    [Column(TypeName = "nvarchar(64)")]
    public string Id { get; set; } = Guid.NewGuid().ToString().ToUpper().Replace("-", "");

    public string NewId()
    {
        return Guid.NewGuid().ToString().Replace("-", "");
    }
}
