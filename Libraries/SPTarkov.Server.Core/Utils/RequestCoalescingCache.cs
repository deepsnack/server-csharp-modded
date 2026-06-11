using System.Collections.Concurrent;

namespace SPTarkov.Server.Core.Utils;

/// <summary>
///     「单一高频请求」的合并/缓存原语（原 SPT-Optimizations 公共基建内置化）。
///
///     - 缓存的是【最终结果字符串】（在 flea/trader 场景即已序列化+压缩的 HTTP body），命中即零计算零序列化。
///     - single-flight：同一 key 并发未命中时，只有第一个调用真正执行 compute，其余线程阻塞在
///       同一个 <see cref="Lazy{T}"/> 上共享结果（ExecutionAndPublication）。
///     - 失效以【事件驱动】为主（见各 invalidate 调用点）；另带一个较长 TTL 兜底，仅防漏钩。
/// </summary>
public sealed class RequestCoalescingCache
{
    private sealed class Entry
    {
        public required Lazy<string> Value { get; init; }
        public long CreatedTicks { get; init; }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly long _ttlTicks;

    /// <param name="ttl">TTL 兜底时长（远大于事件失效频率，仅在某条失效钩子漏触发时防止永久陈旧）。</param>
    public RequestCoalescingCache(TimeSpan ttl)
    {
        _ttlTicks = ttl.Ticks;
    }

    /// <summary>
    ///     命中则返回缓存值；未命中则在 single-flight 保护下调用 <paramref name="compute"/> 计算并缓存。
    /// </summary>
    public string GetOrCompute(string key, Func<string> compute)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(
                key,
                _ => new Entry
                {
                    Value = new Lazy<string>(compute, LazyThreadSafetyMode.ExecutionAndPublication),
                    CreatedTicks = DateTime.UtcNow.Ticks,
                }
            );

            // TTL 兜底过期：丢弃旧条目后重建。
            if (DateTime.UtcNow.Ticks - entry.CreatedTicks > _ttlTicks)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                continue;
            }

            try
            {
                return entry.Value.Value;
            }
            catch
            {
                // 计算抛异常会被 Lazy 永久缓存——移除中毒条目，让下次请求重算，然后把异常抛回原调用方。
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                throw;
            }
        }
    }

    /// <summary>移除所有以 <paramref name="prefix"/> 开头的缓存（事件驱动失效）。</summary>
    public void InvalidatePrefix(string prefix)
    {
        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    /// <summary>清空一个 bucket（如 "flea" / "trader"）。</summary>
    public void InvalidateBucket(string bucket)
    {
        InvalidatePrefix(bucket + ":");
    }
}
