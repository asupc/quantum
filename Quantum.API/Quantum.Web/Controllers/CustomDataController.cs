using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;


/// <summary>
/// 自定义数据
/// </summary>
[CustomAuthorizationFilter]
public class CustomDataController : BaseController
{
    readonly CustomDataService _customDataService;

    public CustomDataController(CustomDataService customDataService)
    {
        _customDataService = customDataService;
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
    [HttpGet("{type}")]
    public Task<PageResult<CustomDataModel>> Get([FromRoute] string type, [FromQuery] CustomDataModel queryModel, [FromQuery] DateTime? createTimeStart, [FromQuery] DateTime? createTimeEnd, [FromQuery] string key, [FromQuery] int pageSize = 50, [FromQuery] int pageIndex = 1)
    {
        return _customDataService.Get(type, queryModel, createTimeStart, createTimeEnd, key, pageSize, pageIndex);
    }

    /// <summary>
    /// 导出自定义数据为 CSV（与列表同筛选条件、全量不分页；表头取该类型的标题定义）
    /// </summary>
    [HttpGet("export/{type}")]
    [ActionLogFilter("导出自定义数据")]
    public async Task<IActionResult> ExportAsync([FromRoute] string type, [FromQuery] CustomDataModel queryModel, [FromQuery] DateTime? createTimeStart, [FromQuery] DateTime? createTimeEnd)
    {
        return File(await _customDataService.ExportCsvAsync(type, queryModel, createTimeStart, createTimeEnd), "text/csv", type + ".csv");
    }

    /// <summary>
    /// 更新数据
    /// </summary>
    /// <returns></returns>
    [HttpPut]
    [ManagerOnly]
    public Task<CustomDataModel> UpdateAsync([FromBody] CustomDataModel data)
    {
        return _customDataService.UpdateAsync(data);
    }

    /// <summary>
    /// 更新数据
    /// </summary>
    /// <returns></returns>
    [HttpPut("updates")]
    [ManagerOnly]
    public Task<List<CustomDataModel>> UpdatesAsync([FromBody] List<CustomDataModel> datas)
    {
        return _customDataService.UpdatesAsync(datas);
    }

    /// <summary>
    /// 批量提交数据
    /// </summary>
    [HttpPost]
    [ManagerOnly]
    public Task<List<CustomDataModel>> AddAsync([FromBody] List<CustomDataModel> datas)
    {
        return _customDataService.AddAsync(datas);
    }

    /// <summary>
    /// 删除数据
    /// </summary>
    /// <returns></returns>
    [HttpDelete]
    [ManagerOnly]
    public Task<List<CustomDataModel>> DeleteAsync([FromBody] List<string> ids)
    {
        return _customDataService.DeleteAsync(ids);
    }

    /// <summary>
    /// 按数据类型清空数据
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    [HttpDelete("clear/{type}")]
    [ManagerOnly]
    public Task<bool> ClearAsync([FromRoute] string type)
    {
        return _customDataService.ClearAsync(type);
    }
}
