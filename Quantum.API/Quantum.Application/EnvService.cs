using System.Text;
using log4net;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

public class EnvService
{

    private readonly ILog log = LogManager.GetLogger("NETCoreRepository", typeof(EnvService));
    private readonly IQuantumDbContext _dbContext;

    public EnvService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 添加或更新环境变量
    /// </summary>
    /// <param name="envs"></param>
    /// <returns></returns>
    public async Task<List<string>> AddOrUpdateEnvs(List<EnvModelPostModel> envs)
    {
        List<string> messages = new();

        var allEnvs = CacheManager.Get<EnvModel>();

        var updateEnvs = new List<EnvModel>();
        var addEnvs = new List<EnvModel>();

        foreach (var env in envs)
        {
            try
            {
                var e = allEnvs.SingleOrDefault(n => n.Id == env.Id);
                if (e != null)
                {
                    if (env.UpdateTime == null)
                    {
                        env.UpdateTime = DateTime.Now;
                    }
                    env.CreateTime = e.CreateTime;
                    updateEnvs.Add(env);
                }
                else
                {
                    if (allEnvs.Any(n => n.Name == env.Name && env.Value == n.Value))
                    {
                        messages.Add($"环境变量名称：{env.Name}，值：{env.Value}，重复，已跳过。");
                        continue;
                    }
                    env.Id = Guid.NewGuid().ToString().Replace("-", "");
                    if (env.UpdateTime == null)
                    {
                        env.UpdateTime = DateTime.Now;
                    }
                    env.CreateTime = DateTime.Now;
                    addEnvs.Add(env);
                }
            }
            catch (Exception e)
            {
                log.Error("添加或更新环境变量异常", e);
            }
        }
        await _dbContext.UpdateRangeAsync(updateEnvs);
        await _dbContext.Envs.AddRangeAsync(addEnvs);
        await _dbContext.SaveChangesAsync();

        CacheManager.Refresh<EnvModel>();
        return messages;
    }

    public async Task Delete(List<string> ids)
    {
        // §2-12：待删行按 ids 直查数据库，不再依赖 CacheManager——缓存陈旧/未命中会让旧实现的 `envs.Any()` 判空后
        // 直接 return，DB 目标行永久残留（删除静默 no-op）。取到的实体同时用于删除前备份落盘。
        var envs = await _dbContext.Envs.AsNoTracking()
            .Where(n => ids != null && ids.Contains(n.Id)).ToListAsync();
        if (envs.Count == 0)
        {
            return;
        }
        var dir = "./logs/deleteEnvs";
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var logName = DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".log";
        var logPath = dir + "/" + logName;
        if (!File.Exists(logPath))
        {
            File.Create(logPath).Close();
        }

        await using (StreamWriter writer = new(logPath))
        {
            foreach (var item in envs)
            {
                await writer.WriteLineAsync(JsonConvert.SerializeObject(item));
            }
        }
        LogServiceHelper.Logs.Enqueue(new LogModel
        {
            CreateTime = DateTime.Now,
            LogType = LogType.操作日志,
            Operator = "System",
            Remark = "具体参数请看详情日志。",
            Success = true,
            Title = "删除环境变量",
            DirectoryName = "deleteEnvs",
            LogPath = logName
        });
        // 服务端按 ids 直删，与备份同源，免去跟踪态 RemoveRange + 冗余二次 SaveChanges
        await _dbContext.Envs.Where(n => ids.Contains(n.Id)).ExecuteDeleteAsync();
        CacheManager.Refresh<EnvModel>();
    }


    #region 环境变量管理（自 EnvController 下沉）

    /// <summary>
    /// 环境变量列表数据（缓存组装视图）
    /// </summary>
    public PageResult<EnvModel> GetPage(EnvQuery query)
    {
        var envs = FilterEnvs(query).OrderByDescending(n => n.UpdateTime).ToList();

        return new PageResult<EnvModel>
        {
            Data = envs.Skip(query.Skip).Take(query.PageSize).ToList(),
            TotalCount = envs.Count
        };
    }

    /// <summary>
    /// 导出环境变量值：与列表同筛选条件、全量，每行一个变量值（按更新时间倒序）
    /// </summary>
    public byte[] ExportValues(EnvQuery query)
    {
        var envs = FilterEnvs(query).OrderByDescending(n => n.UpdateTime).ToList();
        return Encoding.UTF8.GetBytes(string.Concat(envs.Select(n => (n.Value ?? string.Empty) + "\n")));
    }

    /// <summary>
    /// 列表/导出共用的筛选：关键字匹配名称/值/备注 + 启用状态
    /// </summary>
    private static IEnumerable<EnvModel> FilterEnvs(EnvQuery query)
    {
        if (!string.IsNullOrEmpty(query.Key))
        {
            query.Key = query.Key.ToLower();
        }
        return CacheManager.Get<EnvModel>().Where(n =>
            (query.Enable == null || query.Enable.Value == n.Enable)
        && (string.IsNullOrEmpty(query.Key)
        || (!string.IsNullOrEmpty(n.Name) && n.Name.ToLower().Contains(query.Key))
        || (!string.IsNullOrEmpty(n.Value) && n.Value.ToLower().Contains(query.Key))
        || (!string.IsNullOrEmpty(n.Remark) && n.Remark.ToLower().Contains(query.Key))));
    }

    private static List<string> SplitIds(string ids)
    {
        return string.IsNullOrEmpty(ids) ? [] : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>
    /// 批量保存环境变量（领域动作：按名称合并，存在即更新、不存在即新增）
    /// </summary>
    public async Task<List<string>> Save(List<EnvModelPostModel> envs)
    {
        foreach (var env in envs)
        {
            if (!RegexHelper.Code(env.Name))
            {
                throw new BusinessException("环境变量名称只能包含数字和字母,长度最大64");
            }
        }
        return await AddOrUpdateEnvs(envs);
    }

    /// <summary>
    /// 删除指定的环境变量(按值匹配)
    /// </summary>
    public async Task<bool> DeleteByValuesAsync(string values)
    {
        var valueList = string.IsNullOrEmpty(values) ? [] : values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        // §2-12：待删 Id 直查数据库（旧实现从缓存取，缓存陈旧会让删除静默 no-op）
        var ts = await _dbContext.Envs.AsNoTracking().Where(n => valueList.Contains(n.Value)).Select(n => n.Id).ToListAsync();
        await Delete(ts);
        return true;
    }

    /// <summary>
    /// 通过环境变量id 删除
    /// </summary>
    public async Task<bool> DeleteByIdsAsync(string ids)
    {
        await Delete(SplitIds(ids));
        return true;
    }

    /// <summary>
    /// 查询环境变量
    /// </summary>
    public List<EnvModel> Query(string name, string key)
    {
        if (!string.IsNullOrEmpty(name))
        {
            name = name.ToLower();
        }
        if (!string.IsNullOrEmpty(key))
        {
            key = key.ToLower();
        }
        return [.. CacheManager.Get<EnvModel>().Where(n => (string.IsNullOrEmpty(name) || n.Name.ToLower() == name)
                                      && (string.IsNullOrEmpty(key) || (!string.IsNullOrEmpty(n.Value) && n.Value.ToLower().Contains(key))))];
    }

    /// <summary>
    /// 按名称直查数据库（缓存刚失效/未命中时仍准确）：任务门面「同名按名称合并」依赖本方法取既有 Id
    /// </summary>
    public Task<EnvModel> GetByNameAsync(string name)
    {
        return _dbContext.Envs.AsNoTracking().SingleOrDefaultAsync(n => n.Name == name);
    }

    /// <summary>
    /// 禁用环境变量(id)
    /// </summary>
    public Task<bool> DisableAsync(string ids) => SetEnableByIdsAsync(ids, false);

    /// <summary>
    /// 启用环境变量(id)
    /// </summary>
    public Task<bool> EnableAsync(string ids) => SetEnableByIdsAsync(ids, true);

    /// <summary>
    /// 禁用环境变量(按值)
    /// </summary>
    public Task<bool> DisableValuesAsync(string values) => SetEnableByValuesAsync(values, false);

    /// <summary>
    /// 启用环境变量(按值)
    /// </summary>
    public Task<bool> EnableValuesAsync(string values) => SetEnableByValuesAsync(values, true);

    /// <summary>
    /// §2-3：Enable/Disable 不再就地改 CacheManager 共享实例（并发读者会看到未提交的半写状态，写失败还会把缓存钉在脏值上）——
    /// 改跟踪查询取上下文实例（门面同作用域内 Save 已跟踪同 Id 时 EF 返回同一实例，避免二次 Attach 冲突）、改字段、SaveChanges，
    /// 提交后 Refresh 失效缓存。
    /// </summary>
    private async Task<bool> SetEnableByIdsAsync(string ids, bool enable)
    {
        var idList = SplitIds(ids);
        if (idList.Count == 0)
        {
            return true;
        }
        var rows = await _dbContext.Envs.Where(n => idList.Contains(n.Id)).ToListAsync();
        if (rows.Count == 0)
        {
            return true;
        }
        foreach (var row in rows)
        {
            row.Enable = enable;
        }
        await _dbContext.SaveChangesAsync();
        CacheManager.Refresh<EnvModel>();
        return true;
    }

    private async Task<bool> SetEnableByValuesAsync(string values, bool enable)
    {
        var valueList = SplitIds(values);
        if (valueList.Count == 0)
        {
            return true;
        }
        var rows = await _dbContext.Envs.Where(n => valueList.Contains(n.Value)).ToListAsync();
        if (rows.Count == 0)
        {
            return true;
        }
        foreach (var row in rows)
        {
            row.Enable = enable;
        }
        await _dbContext.SaveChangesAsync();
        CacheManager.Refresh<EnvModel>();
        return true;
    }

    #endregion

}
