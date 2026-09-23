namespace Quantum.Entities.DTOs;

public class SaveQLFile
{
    public string name { get; set; }

    public string content { get; set; }

    public string path { get; set; }

    public List<string> QLIds { get; set; }
}
