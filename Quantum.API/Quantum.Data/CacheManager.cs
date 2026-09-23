using log4net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Quantum.Entities.Model;
using Quantum.Utils;
using System.Diagnostics;

namespace Quantum.Data;

/// <summary>
/// 全表内存缓存（读多写少的小表加速）。
/// 约定：
/// 1. Get 返回缓存列表的副本，调用方对列表结构的增删不会污染缓存
///    （实体级修改在写路径迁移后不允许出现——写操作应查库→改→存→失效）；
/// 2. 查询失败时【不】把空列表写进缓存，避免 DB 一次抖动就基于空数据做出错误写入；
/// 3. 写路径统一「写库 → Refresh(T)」单点失效（Remove 幂等，无需类型登记）。
/// </summary>
public static class CacheManager
{

    private static readonly MemoryCache MemoryCache;
    private static readonly ILog Log = LogManager.GetLogger("NETCoreRepository", typeof(CacheManager));

    static CacheManager()
    {
        MemoryCache = new MemoryCache(new MemoryCacheOptions());
    }

    public static void InitCacheDatas()
    {
        Log.Info("开始缓存基本数据。");
        Set(QueryData<EnvModel>());
        Set(QueryData<TaskModel>());
        Set(QueryData<TaskSubModel>());
        Set(QueryData<CommandModel>());
        Set(QueryData<OpenTriggerTask>());
        Log.Info("缓存基本数据结束。");
    }

    public static void Refresh<T>() where T : BaseModel
    {
        MemoryCache.Remove(typeof(T).Name + "_cache");
    }

    private static List<T> QueryData<T>(string sql = null) where T : BaseModel
    {
        var type = typeof(T);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            List<T> data;
            using (DbContext db = SystemConfigHelper.GetSetting().DBType.ToLower() == "sqlite"
                ? new QuantumSqliteDbContext()
                : new QuantumMySqlDbContext())
            {
                // sql 参数为兼容旧签名保留，历史调用点从未传过自定义 SQL
                data = db.Set<T>().AsNoTracking().ToList();
            }
            stopwatch.Stop();
            Log.Debug($"获取缓存数据【{type.Name}】成功，数据条数：【{data.Count}】，用时：【{stopwatch.ElapsedMilliseconds}】");
            return data;
        }
        catch (Exception e)
        {
            Log.Error($"未获取到【{type.Name}】缓存数据：{e.Message}");
        }
        // 失败返回 null：调用方不得把 null/空列表写进缓存
        return null;
    }

    public static void Set<T>(List<T> values) where T : BaseModel
    {
        if (values == null)
        {
            return;
        }
        var key = typeof(T).Name + "_cache";
        MemoryCache.Remove(key);
        MemoryCache.Set(key, values);
    }

    public static List<T> Get<T>() where T : BaseModel
    {
        var key = typeof(T).Name + "_cache";
        if (MemoryCache.TryGetValue(key, out List<T> value))
        {
            // 返回副本：调用方对列表结构的修改不影响缓存本体
            return value.ToList();
        }
        var data = QueryData<T>();
        if (data == null)
        {
            // DB 查询失败：不缓存空表，直接返回空列表，下次读取重试
            return [];
        }
        MemoryCache.Set(key, data);
        return data.ToList();
    }
}
