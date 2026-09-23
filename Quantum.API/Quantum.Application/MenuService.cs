using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;

using Quantum.Utils;
namespace Quantum.Application;

/// <summary>
/// 菜单服务：菜单树的读取/全量保存/重置，以及 jsons/menu.json 种子重建
/// （"数据管理"下的自定义数据子菜单由 CustomDataTitleService 增删时联动维护）。
/// </summary>
public class MenuService
{
    private readonly IQuantumDbContext _dbContext;

    public MenuService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取所有菜单（树形结构）。库为空时从 jsons/menu.json 初始化一次。
    /// </summary>
    public virtual async Task<List<MenuItem>> GetAsync()
    {
        var menus = await _dbContext.Menus.AsNoTracking().ToListAsync();
        if (menus.Count == 0)
        {
            await ReseedAsync();
            menus = await _dbContext.Menus.AsNoTracking().ToListAsync();
        }
        return BuildMenuTree(menus);
    }

    /// <summary>
    /// 从 jsons/menu.json 重建菜单表，并为每个自定义数据类型生成"数据管理"子菜单。
    /// 初始化与重置共用；CustomDataTitle 增删后也调用以保持子菜单同步。
    /// </summary>
    public async Task ReseedAsync()
    {
        _dbContext.Menus.RemoveRange(await _dbContext.Menus.ToListAsync());
        await _dbContext.SaveChangesAsync();

        using StreamReader reader = new("jsons/menu.json");
        var json = await reader.ReadToEndAsync();
        var menuItems = JsonConvert.DeserializeObject<List<MenuItem>>(json) ?? [];

        var customDataMenu = menuItems.FirstOrDefault(n => string.Equals(n.name, "customData", StringComparison.OrdinalIgnoreCase));
        if (customDataMenu != null)
        {
            customDataMenu.children ??= [];
            var sort = customDataMenu.children.Count;
            foreach (var title in await _dbContext.CustomDataTitles.ToListAsync())
            {
                sort++;
                customDataMenu.children.Add(new MenuItem
                {
                    id = Guid.NewGuid().ToString(),
                    sort = sort,
                    path = $"/custom-data/{title.Type}",
                    name = title.TypeName,
                    component = "custom-data/index",
                    meta = new Dictionary<string, object>
                    {
                        ["title"] = title.TypeName,
                        ["icon"] = "fa fa-table",
                    }
                });
            }
        }

        await _dbContext.Menus.AddRangeAsync(ConvertToMenuModels(menuItems, null));
        await _dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// 保存菜单（全部更新，先删后增）
    /// </summary>
    public async Task<bool> UpdateAsync(List<MenuItem> menus)
    {
        if (menus == null || menus.Count == 0)
            throw new BusinessException("菜单数据不能为空！");
        var existingMenus = await _dbContext.Menus.ToListAsync();
        _dbContext.Menus.RemoveRange(existingMenus);
        var models = ConvertToMenuModels(menus, null);
        await _dbContext.Menus.AddRangeAsync(models);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 将 JSON 结构转换为 MenuModel（递归）
    /// </summary>
    private static List<MenuModel> ConvertToMenuModels(List<MenuItem> items, string? parentName)
    {
        var result = new List<MenuModel>();
        foreach (var item in items)
        {
            var model = new MenuModel
            {
                Name = item.name,
                Path = item.path,
                Component = item.component ?? string.Empty,
                ParentName = parentName,
                Sort = item.sort,
                Title = item.meta?.GetValueOrDefault("title")?.ToString() ?? item.name,
                Icon = item.meta?.GetValueOrDefault("icon")?.ToString(),
                HideInMenu = item.meta?.GetValueOrDefault("hideInMenu")?.ToString()?.ToLower() == "true",
                HideInBread = item.meta?.GetValueOrDefault("hideInBread")?.ToString()?.ToLower() == "true",
                IsDefault = item.meta?.GetValueOrDefault("isDefault")?.ToString()?.ToLower() == "true"
            };
            result.Add(model);

            if (item.children != null && item.children.Count != 0)
            {
                var children = ConvertToMenuModels(item.children, item.name);
                result.AddRange(children);
            }
        }
        return result;
    }

    /// <summary>
    /// 将扁平菜单列表构建为树形结构
    /// </summary>
    private static List<MenuItem> BuildMenuTree(List<MenuModel> menus)
    {
        var result = new List<MenuItem>();
        var rootMenus = menus.Where(m => string.IsNullOrEmpty(m.ParentName))
                              .OrderBy(m => m.Sort)
                              .ToList();

        foreach (var root in rootMenus)
        {
            var item = ConvertToMenuItem(root);
            item.children = GetChildren(menus, root.Name);
            result.Add(item);
        }

        return result;
    }

    private static List<MenuItem> GetChildren(List<MenuModel> allMenus, string parentName)
    {
        return allMenus.Where(m => m.ParentName == parentName)
                       .OrderBy(m => m.Sort)
                       .Select(m =>
                       {
                           var item = ConvertToMenuItem(m);
                           item.children = GetChildren(allMenus, m.Name);
                           return item;
                       })
                       .ToList();
    }

    private static MenuItem ConvertToMenuItem(MenuModel model)
    {
        var meta = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(model.Title))
            meta["title"] = model.Title;
        if (!string.IsNullOrEmpty(model.Icon))
            meta["icon"] = model.Icon;
        if (model.HideInMenu)
            meta["hideInMenu"] = "true";
        if (model.HideInBread)
            meta["hideInBread"] = "true";
        if (model.IsDefault)
            meta["isDefault"] = "true";

        return new MenuItem
        {
            id = model.Id,
            name = model.Name,
            path = model.Path,
            component = model.Component,
            sort = model.Sort,
            meta = meta,
            children = [] // 确保 children 始终有初始值
        };
    }
}
