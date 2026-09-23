using Microsoft.EntityFrameworkCore;
using Quantum.Entities.Model;

namespace Quantum.Data;

/// <summary>
/// IQuantumDbContext 上的统一数据读写辅助（替代已删除的静态 BaseRepository&lt;T&gt;）。
/// 与原仓储方法一一对应：短生命周期上下文的隐式提交语义改为「调用方作用域内显式 SaveChanges」，
/// 写操作完成后统一 CacheManager.Refresh 保持缓存失效语义不变。
/// 注意：这些方法面向分离态实体；同一请求内先 FindAsync（跟踪态）再 Update 同一实例是安全的，
/// 但不要对上下文中已存在的同键另一实例 Attach。
/// </summary>
public static class QuantumDbContextExtensions
{
    public static async Task<T> GetByIdAsync<T>(this IQuantumDbContext db, object id) where T : BaseModel
    {
        return await db.Set<T>().AsNoTracking().FirstOrDefaultAsync(n => n.Id == (string)id);
    }

    public static async Task<IEnumerable<T>> GetAllAsync<T>(this IQuantumDbContext db) where T : BaseModel
    {
        return await db.Set<T>().AsNoTracking().ToListAsync();
    }

    public static async Task<int> AddAsync<T>(this IQuantumDbContext db, T entity) where T : BaseModel
    {
        db.Set<T>().Add(entity);
        var changed = await db.SaveChangesAsync();
        CacheManager.Refresh<T>();
        return changed;
    }

    public static async Task<int> AddRangeAsync<T>(this IQuantumDbContext db, IEnumerable<T> entities) where T : BaseModel
    {
        db.Set<T>().AddRange(entities);
        var changed = await db.SaveChangesAsync();
        CacheManager.Refresh<T>();
        return changed;
    }

    public static async Task<bool> UpdateAsync<T>(this IQuantumDbContext db, T entity) where T : BaseModel
    {
        try
        {
            db.Update(entity);
            var changed = await db.SaveChangesAsync();
            return changed > 0;
        }
        finally
        {
            // §2-3：Refresh 放 finally——写中途异常时上下文可能已部分变更/实体已被跟踪，无条件失效缓存，
            // 避免抛出路径跳过失效导致后续读者拿到脏缓存
            CacheManager.Refresh<T>();
        }
    }

    public static async Task<bool> UpdateRangeAsync<T>(this IQuantumDbContext db, IEnumerable<T> entities) where T : BaseModel
    {
        foreach (var entity in entities)
        {
            db.Update(entity);
        }
        var changed = await db.SaveChangesAsync();
        CacheManager.Refresh<T>();
        return changed > 0;
    }

    public static async Task<bool> DeleteByIdAsync<T>(this IQuantumDbContext db, object id) where T : BaseModel
    {
        if (id == null)
        {
            return false;
        }
        var deleted = await db.Set<T>().Where(n => n.Id == (string)id).ExecuteDeleteAsync();
        CacheManager.Refresh<T>();
        return deleted > 0;
    }

    public static async Task<bool> DeleteByIdsAsync<T>(this IQuantumDbContext db, IEnumerable<string> ids) where T : BaseModel
    {
        if (ids == null || !ids.Any())
        {
            return false;
        }
        var deleted = 0;
        // SQLite 的 IN 参数上限 999，与原仓储行为一致按批删除
        foreach (var batch in ids.ToList().Chunk(500))
        {
            deleted += await db.Set<T>().Where(n => batch.Contains(n.Id)).ExecuteDeleteAsync();
        }
        CacheManager.Refresh<T>();
        return deleted > 0;
    }

    public static async Task<bool> DeleteRangeAsync<T>(this IQuantumDbContext db, IEnumerable<T> entities) where T : BaseModel
    {
        db.Set<T>().RemoveRange(entities);
        var changed = await db.SaveChangesAsync();
        CacheManager.Refresh<T>();
        return changed > 0;
    }
}
