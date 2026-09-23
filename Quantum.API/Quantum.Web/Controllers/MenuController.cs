using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 菜单管理（管理员专用）
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class MenuController : BaseController
{
    private readonly MenuService _menuService;

    public MenuController(MenuService menuService)
    {
        _menuService = menuService;
    }

    /// <summary>
    /// 获取所有菜单（树形结构）。库为空时从 jsons/menu.json 初始化一次；
    /// "数据管理"下的自定义数据子菜单由 CustomDataTitleController 增删时同步维护，本接口只读。
    /// </summary>
    [HttpGet]
    public Task<List<MenuItem>> GetAsync()
    {
        return _menuService.GetAsync();
    }

    /// <summary>
    /// 保存菜单（全部更新，先删后增）
    /// </summary>
    [HttpPut]
    public Task<bool> UpdateMenuAsync([FromBody] List<MenuItem> menus)
    {
        return _menuService.UpdateAsync(menus);
    }

    /// <summary>
    /// 重置菜单到默认状态（从 JSON 重建，含自定义数据子菜单）
    /// </summary>
    [HttpPost("reset")]
    public async Task<bool> ResetMenuAsync()
    {
        await _menuService.ReseedAsync();
        return true;
    }
}
