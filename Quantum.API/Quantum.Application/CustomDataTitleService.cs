using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 自定义数据表格头服务：按类型查询表格头配置、增改（含"数据管理"子菜单的联动新增/改名）与删除（连带清空该类型数据并移除子菜单）。
/// 控制器只做参数绑定与结果返回，业务与数据访问全部收敛在本类。
/// </summary>
public class CustomDataTitleService
{
    readonly IQuantumDbContext _dbContext;

    public CustomDataTitleService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取自定义数据类型
    /// </summary>
    /// <param name="type"></param>
    public async Task<CustomDataTitleModel> Get(string type)
    {
        var data = await _dbContext.CustomDataTitles.AsNoTracking().FirstOrDefaultAsync(n => n.Type == type);
        return data;
    }

    /// <summary>
    /// 删除
    /// </summary>
    /// <returns></returns>
    public async Task DeleteAsync(string type, bool deleteData)
    {
        var count = await _dbContext.CustomDatas.Where(n => n.Type == type).ExecuteDeleteAsync();
        await _dbContext.DeleteRangeAsync(await _dbContext.CustomDataTitles.Where(n => n.Type == type).ToListAsync());
        // 同步移除"数据管理"下对应的子菜单
        var menu = await _dbContext.Menus.SingleOrDefaultAsync(n => n.Path == $"/custom-data/{type}");
        if (menu != null)
        {
            _dbContext.Menus.Remove(menu);
            await _dbContext.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 获取所有
    /// </summary>
    /// <returns></returns>
    public async Task<List<CustomDataTitleModel>> GetAll()
    {
        var data = await _dbContext.CustomDataTitles.ToListAsync();
        return data;
    }

    /// <summary>
    /// 分页
    /// </summary>
    /// <returns></returns>
    public async Task<PageResult<CustomDataTitleModel>> GetPage(BaseQuery query)
    {
        var data = _dbContext.CustomDataTitles.Where(n => string.IsNullOrEmpty(query.Key) || n.TypeName.Contains(query.Key));
        return new PageResult<CustomDataTitleModel>
        {
            Data = await data.OrderBy(n => n.Type).Skip(query.Skip).Take(query.PageSize).ToListAsync(),
            TotalCount = await data.CountAsync()
        };
    }

    /// <summary>
    /// 添加自定义数据字段信息
    /// </summary>
    public async Task<CustomDataTitleModel> AddOrUpdate(CustomDataTitleModel data)
    {
        if (string.IsNullOrEmpty(data.Type))
        {
            throw new BusinessException("未指定数据类型Type");
        }
        var s = await _dbContext.CustomDataTitles.AsNoTracking().FirstOrDefaultAsync(n => n.Type == data.Type);
        if (s != null)
        {
            data.Id = s.Id;
            await _dbContext.UpdateAsync(data);
            // 类型名可能变化，同步更新子菜单
            var menu = await _dbContext.Menus.SingleOrDefaultAsync(n => n.Path == $"/custom-data/{data.Type}");
            if (menu != null && menu.Name != data.TypeName)
            {
                menu.Name = data.TypeName;
                menu.Title = data.TypeName;
                await _dbContext.SaveChangesAsync();
            }
        }
        else
        {
            data.Id = data.NewId();
            await _dbContext.CustomDataTitles.AddAsync(data);
            await _dbContext.SaveChangesAsync();
            // 新增类型：在"数据管理"下追加子菜单
            var parent = await _dbContext.Menus.SingleOrDefaultAsync(n => n.Name == "customData" && n.ParentName == null);
            if (parent != null)
            {
                var sort = await _dbContext.Menus.CountAsync(n => n.ParentName == parent.Name);
                _dbContext.Menus.Add(new MenuModel
                {
                    Name = data.TypeName,
                    Path = $"/custom-data/{data.Type}",
                    Component = "custom-data/index",
                    ParentName = parent.Name,
                    Sort = sort + 1,
                    Title = data.TypeName,
                    Icon = "fa fa-table",
                });
                await _dbContext.SaveChangesAsync();
            }
        }
        return data;
    }
}
