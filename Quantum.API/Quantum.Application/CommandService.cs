using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 快捷回复（指令）服务：分页查询、增删改、批量启停。
/// 控制器只做参数绑定与结果返回，业务与数据访问全部收敛在本类。
/// </summary>
public class CommandService
{
    readonly IQuantumDbContext _dbContext;

    public CommandService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取所有快捷回复（分页）
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public PageResult<CommandModel> GetPage(CommandQuery query)
    {
        var commands = CacheManager.Get<CommandModel>().Where(n => string.IsNullOrEmpty(query.Key) || n.Key.Contains(query.Key) || n.Message.Contains(query.Key));
        return new PageResult<CommandModel>
        {
            Data = commands.OrderByDescending(n => n.Enable).Skip(query.Skip).Take(query.PageSize).ToList(),
            TotalCount = commands.Count()
        };
    }

    /// <summary>
    /// 删除快捷回复
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    public async Task<bool> DeleteAsync(string ids)
    {
        var idList = string.IsNullOrEmpty(ids) ? [] : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        await _dbContext.DeleteByIdsAsync<CommandModel>(idList);
        return true;
    }

    /// <summary>
    /// 添加快捷回复
    /// </summary>
    /// <param name="command"></param>
    /// <returns></returns>
    public async Task<bool> AddAsync(CommandModel command)
    {
        var c = await _dbContext.GetByIdAsync<CommandModel>(command.Id);
        if (c != null)
        {
            throw new BusinessException("快捷回复已存在，请刷新列表后重新新增！");
        }
        await _dbContext.AddAsync(command);
        return true;
    }

    /// <summary>
    /// 修改快捷回复
    /// </summary>
    /// <param name="command"></param>
    /// <returns></returns>
    public async Task<bool> UpdateAsync(CommandModel command)
    {
        var c = await _dbContext.GetByIdAsync<CommandModel>(command.Id);
        if (c == null)
        {
            throw new BusinessException("快捷回复不存在，可能已被删除，请刷新列表！");
        }
        await _dbContext.UpdateAsync(command);
        return true;
    }

    /// <summary>
    /// 禁用快捷回复
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    public async Task<bool> DisableAsync(List<string> ids)
    {
        await SetEnableAsync(ids, false);
        return true;
    }

    private async Task SetEnableAsync(List<string> ids, bool enable)
    {
        var commands = (await _dbContext.GetAllAsync<CommandModel>()).Where(s => ids.Contains(s.Id)).ToList();
        foreach (var item in commands)
        {
            item.Enable = enable;
        }
        await _dbContext.UpdateRangeAsync(commands);
    }

    /// <summary>
    /// 启用快捷回复
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    public async Task<bool> EnableAsync(List<string> ids)
    {
        await SetEnableAsync(ids, true);
        return true;
    }
}
