using log4net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Quantum.Entities.Model;
using Quantum.Utils;
using System.Collections.Concurrent;
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
        // 代际自增先行：读者的「回源查库 → 回填缓存」之间若发生本刷新，读者据代际发现自己的快照已过期，
        // 不再把旧快照钉进缓存（否则 Remove 落空 + 迟到的 Set 会把过期数据一直留到该表下次写入）。
        TouchGeneration(typeof(T));
        MemoryCache.Remove(typeof(T).Name + "_cache");
    }

    private static List<T> QueryData<T>(string sql = null) where T : BaseModel
    {
        var type = typeof(T);
        // 单测观察点（默认 null）：免 DB 注入回源结果，使「写者与读者交叠」可确定性复现
        if (TestQueryOverride != null && TestQueryOverride(type) is List<T> injected)
        {
            return injected;
        }
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
        // 回源前取代际：若查库期间发生 Refresh（写者已删缓存，但此刻本就无缓存可删），
        // 本次读到的是写前快照，绝不能回填（否则过期数据钉在缓存里直到该表下次写入）。
        var generation = ReadGeneration(typeof(T));
        var data = QueryData<T>();
        // 单测观察点：在「查库完成」与「回填」之间插入确定性交错点（生产为 null，零开销）
        TestHookAfterQuery?.Invoke();
        if (data == null)
        {
            // DB 查询失败：不缓存空表，直接返回空列表，下次读取重试
            return [];
        }
        if (ReadGeneration(typeof(T)) == generation)
        {
            MemoryCache.Set(key, data);
        }
        // 代际已变（写者抢先刷新）：本次不缓存，直接返回刚读到的新数据；
        // 下次读取重新回源或命中写者/后续读者回填的新快照，不会长期停留在旧值。
        return data.ToList();
    }

    /// <summary>
    /// 类型代际：Refresh&lt;T&gt; 自增，Get&lt;T&gt; 回源前后比对。写者与读者各执一次计数即可发现交叠，
    /// 无需额外锁（ConcurrentDictionary.AddOrUpdate 原子自增；计数只增不减，无 ABA 风险）。
    /// </summary>
    private static readonly ConcurrentDictionary<Type, long> Generations = new();

    private static long ReadGeneration(Type type) => Generations.TryGetValue(type, out var value) ? value : 0;

    private static void TouchGeneration(Type type) => Generations.AddOrUpdate(type, 1, (_, current) => current + 1);

    /// <summary>
    /// 观察点（单测专用）：Get&lt;T&gt; 在「回源完成」与「回填缓存」之间的回调，
    /// 用于确定性构造「读者查库 → 写者 Refresh → 读者回填」的交错；默认 null，生产路径零开销、行为不变。
    /// </summary>
    internal static Action TestHookAfterQuery;

    /// <summary>观察点（单测专用）：按类型注入回源结果（返回 null 则走真实 DB）；默认 null，生产不受影响。</summary>
    internal static Func<Type, object> TestQueryOverride;
}
