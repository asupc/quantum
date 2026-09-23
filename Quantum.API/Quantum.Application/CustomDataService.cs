using System.Text;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 自定义数据服务：按类型的分页查询（Data1-Data15 逐列模糊匹配）、增删改、批量提交与按类型清空。
/// 控制器只做参数绑定与结果返回，业务与数据访问全部收敛在本类。
/// </summary>
public class CustomDataService
{
    readonly IQuantumDbContext _dbContext;

    public CustomDataService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取自定义数据
    /// </summary>
    /// <param name="type">类型</param>
    /// <param name="queryModel">查询条件</param>
    /// <param name="createTimeStart">创建时间-起</param>
    /// <param name="createTimeEnd">创建时间-止</param>
    /// <param name="key">关键字</param>
    /// <param name="pageSize">分页大小</param>
    /// <param name="pageIndex">页码</param>
    /// <returns></returns>
    public async Task<PageResult<CustomDataModel>> Get(string type, CustomDataModel queryModel, DateTime? createTimeStart, DateTime? createTimeEnd, string key, int pageSize = 50, int pageIndex = 1)
    {
        // §2-8：默认全量 999999 会把整表拉进内存，收敛为默认 50、上限 1000；需要全量走导出端点
        pageIndex = Math.Max(1, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var allData = QueryDatas(type, queryModel, createTimeStart, createTimeEnd);
        return new()
        {
            TotalCount = await allData.CountAsync(),
            Data = await allData.OrderByDescending(n => n.CreateTime).ThenBy(n => n.Id).Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(),
            Page = pageIndex,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 导出自定义数据为 CSV 字节（UTF-8 BOM，Excel 可直接打开）：与列表同筛选条件、全量不分页、按创建时间倒序。
    /// 表头取该类型的标题定义（标题非空的 Data 列参与导出，末列固定「时间」= 创建时间），与页面导出旧版列结构一致。
    /// </summary>
    public async Task<byte[]> ExportCsvAsync(string type, CustomDataModel queryModel, DateTime? createTimeStart, DateTime? createTimeEnd)
    {
        var title = await _dbContext.CustomDataTitles.AsNoTracking().SingleOrDefaultAsync(n => n.Type == type);
        var datas = await QueryDatas(type, queryModel, createTimeStart, createTimeEnd).OrderByDescending(n => n.CreateTime).ToListAsync();

        var columns = new List<(string Header, Func<CustomDataModel, string> Value)>();
        if (title != null)
        {
            for (var i = 1; i <= 15; i++)
            {
                var header = (string)typeof(CustomDataTitleModel).GetProperty($"Title{i}")?.GetValue(title);
                if (string.IsNullOrEmpty(header))
                {
                    continue;
                }
                var dataField = $"Data{i}";
                columns.Add((header, d => (string)typeof(CustomDataModel).GetProperty(dataField)?.GetValue(d) ?? string.Empty));
            }
        }
        columns.Add(("时间", d => d.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")));

        static string Escape(string val)
        {
            val ??= string.Empty;
            return val.Contains('"') || val.Contains(',') || val.Contains('\n') || val.Contains('\r')
                ? '"' + val.Replace("\"", "\"\"") + '"'
                : val;
        }

        // \ufeff BOM 防止 Excel 打开中文乱码
        var sb = new StringBuilder("\ufeff");
        sb.Append(string.Join(',', columns.Select(c => Escape(c.Header))));
        foreach (var d in datas)
        {
            sb.Append("\r\n");
            sb.Append(string.Join(',', columns.Select(c => Escape(c.Value(d)))));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// 列表/导出共用的数据筛选：Type + 创建时间范围 + Data1-15 逐列模糊匹配
    /// </summary>
    private IQueryable<CustomDataModel> QueryDatas(string type, CustomDataModel queryModel, DateTime? createTimeStart, DateTime? createTimeEnd)
    {
        queryModel.Type = type;
        return _dbContext.CustomDatas.AsNoTracking().Where(
            n => n.Type == queryModel.Type && (!createTimeStart.HasValue || n.CreateTime >= createTimeStart)
            && (!createTimeEnd.HasValue || n.CreateTime <= createTimeEnd)
            && (string.IsNullOrEmpty(queryModel.Data1) || (!string.IsNullOrEmpty(n.Data1) && n.Data1.Contains(queryModel.Data1)))
            && (string.IsNullOrEmpty(queryModel.Data2) || (!string.IsNullOrEmpty(n.Data2) && n.Data2.Contains(queryModel.Data2)))
            && (string.IsNullOrEmpty(queryModel.Data3) || (!string.IsNullOrEmpty(n.Data3) && n.Data3.Contains(queryModel.Data3)))
            && (string.IsNullOrEmpty(queryModel.Data4) || (!string.IsNullOrEmpty(n.Data4) && n.Data4.Contains(queryModel.Data4)))
            && (string.IsNullOrEmpty(queryModel.Data5) || (!string.IsNullOrEmpty(n.Data5) && n.Data5.Contains(queryModel.Data5)))
            && (string.IsNullOrEmpty(queryModel.Data6) || (!string.IsNullOrEmpty(n.Data6) && n.Data6.Contains(queryModel.Data6)))
            && (string.IsNullOrEmpty(queryModel.Data7) || (!string.IsNullOrEmpty(n.Data7) && n.Data7.Contains(queryModel.Data7)))
            && (string.IsNullOrEmpty(queryModel.Data8) || (!string.IsNullOrEmpty(n.Data8) && n.Data8.Contains(queryModel.Data8)))
            && (string.IsNullOrEmpty(queryModel.Data9) || (!string.IsNullOrEmpty(n.Data9) && n.Data9.Contains(queryModel.Data9)))
            && (string.IsNullOrEmpty(queryModel.Data10) || (!string.IsNullOrEmpty(n.Data10) && n.Data10.Contains(queryModel.Data10)))
            && (string.IsNullOrEmpty(queryModel.Data11) || (!string.IsNullOrEmpty(n.Data11) && n.Data11.Contains(queryModel.Data11)))
            && (string.IsNullOrEmpty(queryModel.Data12) || (!string.IsNullOrEmpty(n.Data12) && n.Data12.Contains(queryModel.Data12)))
            && (string.IsNullOrEmpty(queryModel.Data13) || (!string.IsNullOrEmpty(n.Data13) && n.Data13.Contains(queryModel.Data13)))
            && (string.IsNullOrEmpty(queryModel.Data14) || (!string.IsNullOrEmpty(n.Data14) && n.Data14.Contains(queryModel.Data14)))
            && (string.IsNullOrEmpty(queryModel.Data15) || (!string.IsNullOrEmpty(n.Data15) && n.Data15.Contains(queryModel.Data15))));
    }

    /// <summary>
    /// 更新数据
    /// </summary>
    /// <returns></returns>
    public async Task<CustomDataModel> UpdateAsync(CustomDataModel data)
    {
        data.UpdateTime = DateTime.Now;
        await _dbContext.UpdateAsync(data);
        return data;
    }

    /// <summary>
    /// 批量更新数据
    /// </summary>
    /// <returns></returns>
    public async Task<List<CustomDataModel>> UpdatesAsync(List<CustomDataModel> datas)
    {
        foreach (var data in datas)
        {
            data.UpdateTime = DateTime.Now;
        }
        await _dbContext.UpdateRangeAsync(datas);
        return datas;
    }

    /// <summary>
    /// 批量提交数据
    /// </summary>
    public async Task<List<CustomDataModel>> AddAsync(List<CustomDataModel> datas)
    {
        foreach (var item in datas)
        {
            if (string.IsNullOrEmpty(item.Type))
            {
                throw new BusinessException("数据未指定Type");
            }
            item.Id = item.NewId();
            item.CreateTime = DateTime.Now;
            item.UpdateTime = DateTime.Now;
        }
        await _dbContext.CustomDatas.AddRangeAsync(datas);
        await _dbContext.SaveChangesAsync();
        return datas;
    }

    /// <summary>
    /// 删除数据
    /// </summary>
    /// <returns></returns>
    public async Task<List<CustomDataModel>> DeleteAsync(List<string> ids)
    {
        var datas = await _dbContext.CustomDatas.Where(n => ids.Contains(n.Id)).ToListAsync();
        _dbContext.CustomDatas.RemoveRange(datas);
        await _dbContext.SaveChangesAsync();
        return datas;
    }

    /// <summary>
    /// 按数据类型清空数据
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public async Task<bool> ClearAsync(string type)
    {
        var count = await _dbContext.CustomDatas.Where(n => n.Type == type).ExecuteDeleteAsync();
        await Console.Out.WriteLineAsync($"清空数据类型【{type}】");
        return true;
    }
}
