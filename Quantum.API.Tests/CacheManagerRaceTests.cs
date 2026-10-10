using Quantum.Data;
using Quantum.Entities.Model;

namespace Quantum.API.Tests;

/// <summary>
/// CacheManager 读穿竞态回归：读者「缓存未命中 → 回源查库 → 回填缓存」与写者「写库 → Refresh」交叠时，
/// 迟到的回填会把写前快照钉进缓存（Refresh 的 Remove 落空，因为此刻本就无缓存可删），
/// 过期数据一直留到该表下次写入——表现为脚本读到旧环境变量、启停判定用旧任务行。
/// 修复后代际比对：回源期间发生过 Refresh 的读者不再回填。
/// 触碰进程级静态缓存，与其它静态态用例同 Collection 串行。
/// </summary>
[Collection("ConstsState")]
public class CacheManagerRaceTests : IDisposable
{
    public CacheManagerRaceTests()
    {
        CacheManager.Refresh<TaskModel>();
    }

    public void Dispose()
    {
        CacheManager.TestQueryOverride = null;
        CacheManager.TestHookAfterQuery = null;
        CacheManager.Refresh<TaskModel>();
    }

    [Fact]
    public void Get_RefreshDuringQuery_DoesNotCachePreWriteSnapshot()
    {
        // 交错：读者未命中 → 回源（读到写前快照 v1）→ 【写者 Refresh】→ 读者尝试回填
        var oldSnapshot = Snapshot("v1");
        CacheManager.TestQueryOverride = type => type == typeof(TaskModel) ? oldSnapshot : null;
        CacheManager.TestHookAfterQuery = () => CacheManager.Refresh<TaskModel>();

        var read = CacheManager.Get<TaskModel>();

        // 读者自身返回本次真实读到的数据（不撒谎）
        Assert.Equal(["v1"], read.Select(n => n.Name));
        // 关键：过期快照不得被钉进缓存
        CacheManager.TestQueryOverride = type => type == typeof(TaskModel) ? Snapshot("v2") : null;
        Assert.Equal(["v2"], CacheManager.Get<TaskModel>().Select(n => n.Name));
    }

    [Fact]
    public void Get_RefreshAfterQuery_NextReadObservesNewerData()
    {
        // 写者先 Refresh、读者后回填（Refresh 先发生）：代际已变，读者同样不得回填旧快照
        var refreshed = false;
        CacheManager.TestQueryOverride = type => type == typeof(TaskModel) ? Snapshot("v1") : null;
        CacheManager.TestHookAfterQuery = () =>
        {
            if (refreshed) return;
            refreshed = true;
            CacheManager.Refresh<TaskModel>();
        };

        CacheManager.Get<TaskModel>();

        Assert.True(refreshed, "交错点未被触发");
        CacheManager.TestHookAfterQuery = null;
        CacheManager.TestQueryOverride = type => type == typeof(TaskModel) ? Snapshot("v2") : null;
        Assert.Equal(["v2"], CacheManager.Get<TaskModel>().Select(n => n.Name));
    }

    [Fact]
    public void Get_NoConcurrentRefresh_StillCachesSingleRead()
    {
        // 无并发写：正常路径必须照旧回填（否则每读一次都回源，加速语义丧失）
        var queries = 0;
        CacheManager.TestQueryOverride = type =>
        {
            if (type != typeof(TaskModel)) return null;
            queries++;
            return Snapshot("v1");
        };

        Assert.Equal(["v1"], CacheManager.Get<TaskModel>().Select(n => n.Name));
        Assert.Equal(["v1"], CacheManager.Get<TaskModel>().Select(n => n.Name));
        Assert.Equal(1, queries);

        // 写后 Refresh 失效：下次读取回源取新值
        CacheManager.Refresh<TaskModel>();
        CacheManager.TestQueryOverride = type => type == typeof(TaskModel) ? Snapshot("v2") : null;
        Assert.Equal(["v2"], CacheManager.Get<TaskModel>().Select(n => n.Name));
    }

    [Fact]
    public void Get_QueryFailure_IsNotCached_AndNextReadRetries()
    {
        // 既有约定不得破坏：回源失败（null）不写缓存，下次读取重试
        var queries = 0;
        CacheManager.TestQueryOverride = type =>
        {
            if (type != typeof(TaskModel)) return null;
            queries++;
            return null;
        };

        Assert.Empty(CacheManager.Get<TaskModel>());
        Assert.Empty(CacheManager.Get<TaskModel>());
        Assert.Equal(2, queries);

        CacheManager.TestQueryOverride = type => type == typeof(TaskModel) ? Snapshot("v1") : null;
        Assert.Equal(["v1"], CacheManager.Get<TaskModel>().Select(n => n.Name));
    }

    [Fact]
    public void SetThenRefresh_InvalidatesCachedSnapshot()
    {
        CacheManager.Set(Snapshot("v1"));
        Assert.Equal(["v1"], CacheManager.Get<TaskModel>().Select(n => n.Name));

        CacheManager.Refresh<TaskModel>();

        CacheManager.TestQueryOverride = type => type == typeof(TaskModel) ? Snapshot("v2") : null;
        Assert.Equal(["v2"], CacheManager.Get<TaskModel>().Select(n => n.Name));
    }

    [Fact]
    public void ConcurrentReadersAndWriters_NeverPinStaleAfterFinalWrite()
    {
        // 压力形态：读写并发下最终写入必须可见（不出现「旧快照被钉住直到该表下次写入」）
        var current = new List<TaskModel> { new() { Id = "id", Name = "seed" } };
        CacheManager.TestQueryOverride = type => type == typeof(TaskModel) ? new List<TaskModel>(current) : null;

        var stop = false;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                CacheManager.Get<TaskModel>();
            }
        })).ToArray();
        var writers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var name = "w" + i;
                current = [new TaskModel { Id = "id", Name = name }];
                CacheManager.Refresh<TaskModel>();
            }
        })).ToArray();
        Task.WaitAll(writers);
        current = [new TaskModel { Id = "id", Name = "FINAL" }];
        CacheManager.Refresh<TaskModel>();
        Volatile.Write(ref stop, true);
        Task.WaitAll(readers);

        // 最终写入后：无论此前有多少交错，读取必须见到最终值
        Assert.Equal(["FINAL"], CacheManager.Get<TaskModel>().Select(n => n.Name));
    }

    private static List<TaskModel> Snapshot(string name) => [new TaskModel { Id = "id", Name = name }];
}
