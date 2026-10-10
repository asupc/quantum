namespace Quantum.Entities.DTOs;

public class BaseQuery
{
    /// <summary>分页下限：沿用既有口径（小于 5 视为 5）。</summary>
    public const int MinPageSize = 5;

    /// <summary>
    /// 分页上限：请求侧 PageSize 直接进 EF Take，无上限时一个查询即可要求整表物化
    /// （探针实测 ?PageIndex=100000&amp;PageSize=1000000 被原样回显并执行）。
    /// 取 1e6 是为覆盖脚本指令导出：导出链路在服务端显式设 PageSize=999999 取全量（见 TaskService.ExportAsync）。
    /// </summary>
    public const int MaxPageSize = 1_000_000;

    private int _pageIndex = 1;
    private int _pageSize = 20;

    public int PageIndex
    {
        get => _pageIndex;
        set => _pageIndex = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < MinPageSize ? MinPageSize : (value > MaxPageSize ? MaxPageSize : value);
    }

    public string Key { get; set; }

    public int Skip
    {
        get
        {
            // long 计算再收敛：PageIndex=100000 与 PageSize=1000000 的 int 乘法会溢出成负偏移，
            // 负 OFFSET 在 SQLite 上被当 0、在 MySQL 上直接报错（两种行为都能被请求触发）
            var skip = ((long)_pageIndex - 1) * _pageSize;
            return skip > int.MaxValue ? int.MaxValue : (int)skip;
        }
    }
}