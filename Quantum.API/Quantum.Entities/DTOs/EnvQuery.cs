using Quantum.Entities.Model;

namespace Quantum.Entities.DTOs;

public class EnvQuery : BaseQuery
{
    public bool? Enable { get; set; }
}
