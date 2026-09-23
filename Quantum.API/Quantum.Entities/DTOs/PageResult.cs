namespace Quantum.Entities.DTOs;

public class PageResult<T>
{
    public List<T> Data { get; set; }

    public int TotalCount { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }
}
