using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 自定义数据表格头
/// </summary>
[CustomAuthorizationFilter]
public class CustomDataTitleController : BaseController
{
    readonly CustomDataTitleService _customDataTitleService;

    public CustomDataTitleController(CustomDataTitleService customDataTitleService)
    {
        _customDataTitleService = customDataTitleService;
    }

    /// <summary>
    /// 获取自定义数据类型
    /// </summary>
    /// <param name="type"></param>
    [HttpGet("{type}")]
    public Task<CustomDataTitleModel> Get([FromRoute] string type)
    {
        return _customDataTitleService.Get(type);
    }

    /// <summary>
    /// 删除（管理员专用）
    /// </summary>
    /// <returns></returns>
    [HttpDelete("{type}/{deleteData}")]
    [ManagerOnly]
    public Task DeleteAsync([FromRoute] string type, bool deleteData)
    {
        return _customDataTitleService.DeleteAsync(type, deleteData);
    }

    /// <summary>
    /// 获取所有（需登录：匿名会向公网泄露自定义数据结构元数据）
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public Task<List<CustomDataTitleModel>> Get()
    {
        return _customDataTitleService.GetAll();
    }


    /// <summary>
    /// 分页
    /// </summary>
    /// <returns></returns>
    [HttpGet("page")]
    public Task<PageResult<CustomDataTitleModel>> Get([FromQuery] BaseQuery query)
    {
        return _customDataTitleService.GetPage(query);
    }

    /// <summary>
    /// 添加自定义数据字段信息（管理员专用）
    /// </summary>
    [HttpPost]
    [ManagerOnly]
    public Task<CustomDataTitleModel> AddOrUpdate([FromBody] CustomDataTitleModel data)
    {
        return _customDataTitleService.AddOrUpdate(data);
    }
}
