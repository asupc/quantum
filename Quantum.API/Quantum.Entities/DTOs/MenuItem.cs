namespace Quantum.Entities.DTOs;


public class MenuItem
{
    public string id { get; set; }

    public string path { get; set; }

    public string name { get; set; }

    public string component { get; set; }

    public Dictionary<string, object> meta { get; set; }

    public List<MenuItem> children { get; set; }

    public int sort { get; set; }

    public bool hide { get; set; }
}
