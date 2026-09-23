using System.Collections.Concurrent;
using log4net;

namespace Quantum.Utils;

/// <summary>
/// 有界并发队列：入队超容量时丢弃最旧元素。
/// 用于进程内日志缓冲等「生产快于消费也不能拖垮进程」的场景，
/// 替代无上限的裸 ConcurrentQueue（DB 长期不可用时曾无限膨胀）。
/// </summary>
public class BoundedConcurrentQueue<T>
{
    private static readonly ILog Log = LogManager.GetLogger("NETCoreRepository", typeof(BoundedConcurrentQueue<T>));

    private readonly ConcurrentQueue<T> queue = new();
    private readonly int capacity;

    public BoundedConcurrentQueue(int capacity)
    {
        this.capacity = Math.Max(1, capacity);
    }

    /// <summary>
    /// 元素因超容量被丢弃时的回调（可空）：由使用方注入留痕逻辑（如日志中心 Error），默认静默丢最旧。
    /// </summary>
    public Action<T> OnDropped { get; set; }

    public int Count => queue.Count;

    public void Enqueue(T item)
    {
        while (queue.Count >= capacity)
        {
            if (queue.TryDequeue(out var dropped) && OnDropped != null)
            {
                try
                {
                    OnDropped(dropped);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            }
        }
        queue.Enqueue(item);
    }

    public bool TryDequeue(out T item)
    {
        return queue.TryDequeue(out item);
    }

    /// <summary>
    /// 当前队列内容的快照（ConcurrentQueue 的枚举线程安全）。
    /// </summary>
    public List<T> Snapshot()
    {
        return queue.ToList();
    }
}
