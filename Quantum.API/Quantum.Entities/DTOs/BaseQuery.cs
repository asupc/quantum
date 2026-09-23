namespace Quantum.Entities.DTOs;

public class BaseQuery
{
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;

    public string Key { get; set; }

    public int Skip
    {
        get
        {
            PageIndex = PageIndex < 1 ? 1 : PageIndex;
            PageSize = PageSize < 5 ? 5 : PageSize;
            return (PageIndex - 1) * PageSize;
        }
    }
}