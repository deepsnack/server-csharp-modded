using System.Collections.Concurrent;

namespace SPTarkov.Server.Core.Utils;

/// <summary>
///     按 key 取锁的注册表（原 SPT-Optimizations 公共基建内置化）——
///     把「per-session（或 per-key）协调」这一高频共用原语收敛到一处。
///     锁类型统一为 .NET9 <see cref="System.Threading.Lock"/>（lock(...) 编译为 EnterScope，比 Monitor 更轻）。
///     用法：每个需要独立锁域的功能各持有一个实例，避免不相干的临界区相互阻塞、过度串行化。
/// </summary>
public sealed class KeyedLockRegistry<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, System.Threading.Lock> _locks = new();

    /// <summary>取（或惰性创建）该 key 对应的锁对象。</summary>
    public System.Threading.Lock Get(TKey key)
    {
        return _locks.GetOrAdd(key, _ => new System.Threading.Lock());
    }
}
