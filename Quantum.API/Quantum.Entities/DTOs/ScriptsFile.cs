namespace Quantum.Entities.DTOs;

public class ScriptsFile
{
    public string title { get; set; }

    public bool contextmenu { get; set; }

    public List<ScriptsFile> children { get; set; }

    public string path { get; set; }
}
