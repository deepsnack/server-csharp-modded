using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     静态数据端点（items/globals/handbook/settings/customization/hideout/locales/dialogue）响应缓存。
///     DB 表启动时一次性加载（DatabaseServer.SetTables 仅允许一次），TTL 仅兜底运行时原地突变。
///     key 约定：data:{endpoint}，locale 类端点带 localeId。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class DataCacheService
{
    protected readonly RequestCoalescingCache cache = new(TimeSpan.FromMinutes(5));
    // 字节级缓存以【内容哈希】为键（XXH64 of UTF-16 码元），不依赖字符串实例身份：
    // DataCallbacks 缓存重建/实例变化（哪怕每请求新实例）只要内容不变即命中，零重编码零重压缩。
    private readonly ConcurrentDictionary<ulong, EntryBase> _utf8Cache = new();
    private readonly ConcurrentDictionary<ulong, EntryBase> _zlibCache = new();
    // 引用快速路径：同一字符串实例命中即跳过哈希（静态端点实例稳定时零哈希开销）。
    // 引用键失效/不命中只退回内容哈希路径，不影响正确性。
    private readonly ConcurrentDictionary<string, EntryBase> _utf8RefCache = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, EntryBase> _zlibRefCache = new(ReferenceEqualityComparer.Instance);
    private readonly long _utf8TtlTicks = TimeSpan.FromMinutes(5).Ticks;
    private readonly long _zlibTtlTicks = TimeSpan.FromMinutes(5).Ticks;
    private const long SweepIntervalTicks = TimeSpan.TicksPerMinute;
    private long _lastUtf8SweepTicks;
    private long _lastZlibSweepTicks;

    public string GetOrCompute(string key, Func<string> compute)
    {
        return cache.GetOrCompute(key, compute);
    }

    /// <summary>
    ///     返回 output 的 UTF-8 字节。内容哈希命中即零编码零分配（LOH 级大响应避免每次 12.7MB 级编码分配）。
    /// </summary>
    public byte[] GetOrComputeUtf8(string output)
    {
        return GetOrComputeBytes(output, _utf8RefCache, _utf8Cache, _utf8TtlTicks, ref _lastUtf8SweepTicks, () => Encoding.UTF8.GetBytes(output));
    }

    /// <summary>
    ///     返回 output 的 zlib(SmallestSize) 压缩字节。内容哈希命中即零重压缩
    ///     （哈希本身为一次无分配 UTF-16 直扫，items 级实测约 5-8ms，远低于 140ms 级重压缩）。
    /// </summary>
    public byte[] GetOrComputeZlib(string output)
    {
        return GetOrComputeBytes(output, _zlibRefCache, _zlibCache, _zlibTtlTicks, ref _lastZlibSweepTicks, () => CompressZlib(output));
    }

    private static byte[] GetOrComputeBytes(
        string output,
        ConcurrentDictionary<string, EntryBase> refCache,
        ConcurrentDictionary<ulong, EntryBase> entries,
        long ttlTicks,
        ref long lastSweepTicks,
        Func<byte[]> compute
    )
    {
        SweepIfDue(refCache, entries, ttlTicks, ref lastSweepTicks);

        // 引用快速路径：同实例命中即返回（无哈希）。引用相等由 ReferenceEqualityComparer 保证，
        // 最坏情况只是 miss 退回内容哈希，不会返回错误内容。
        if (refCache.TryGetValue(output, out var refEntry) && DateTime.UtcNow.Ticks - refEntry.CreatedTicks <= ttlTicks)
        {
            try
            {
                return refEntry.Bytes.Value;
            }
            catch
            {
                refCache.TryRemove(new KeyValuePair<string, EntryBase>(output, refEntry));
                throw;
            }
        }

        // 无分配内容指纹：把 UTF-16 码元直接当字节扫（items 级 25MB 视窗实测约 5-8ms）。
        var key = XxHash64.HashToUInt64(MemoryMarshal.AsBytes(output.AsSpan()));

        while (true)
        {
            var entry = entries.GetOrAdd(
                key,
                _ => new EntryBase
                {
                    Bytes = new Lazy<byte[]>(compute, LazyThreadSafetyMode.ExecutionAndPublication),
                    CreatedTicks = DateTime.UtcNow.Ticks,
                }
            );

            // TTL 兜底：内容变化后旧条目可被回收，这里懒淘汰。
            if (DateTime.UtcNow.Ticks - entry.CreatedTicks > ttlTicks)
            {
                entries.TryRemove(new KeyValuePair<ulong, EntryBase>(key, entry));
                continue;
            }


            // 内容命中/新条目都关联到引用键：后续同实例调用走快速路径。
            refCache.TryAdd(output, entry);

            try
            {
                return entry.Bytes.Value;
            }
            catch
            {
                entries.TryRemove(new KeyValuePair<ulong, EntryBase>(key, entry));
                refCache.TryRemove(new KeyValuePair<string, EntryBase>(output, entry));
                throw;
            }
        }
    }

    private static void SweepIfDue(
        ConcurrentDictionary<string, EntryBase> refEntries,
        ConcurrentDictionary<ulong, EntryBase> contentEntries,
        long ttlTicks,
        ref long lastSweepTicks
    )
    {
        // 兜底清扫：内容变更/竞态产生的旧条目由周期任务移除，防滞留（items 级单条约 14MB）。
        var now = DateTime.UtcNow.Ticks;
        var last = System.Threading.Interlocked.Read(ref lastSweepTicks);
        if (now - last < SweepIntervalTicks)
        {
            return;
        }

        if (System.Threading.Interlocked.CompareExchange(ref lastSweepTicks, now, last) != last)
        {
            return;
        }

        foreach (var (key, entry) in contentEntries)
        {
            if (now - entry.CreatedTicks > ttlTicks)
            {
                contentEntries.TryRemove(key, out _);
            }
        }

        // 引用表条目与内容表共享同一 EntryBase；内容表清理后这里同步清理对应引用键（释放旧字符串实例）。
        foreach (var (key, entry) in refEntries)
        {
            if (now - entry.CreatedTicks > ttlTicks)
            {
                refEntries.TryRemove(key, out _);
            }
        }
    }

    private static byte[] CompressZlib(string output)
    {
        var input = Encoding.UTF8.GetBytes(output);
        using var stream = new MemoryStream(Math.Max(64, input.Length / 2));
        using (var zlib = new System.IO.Compression.ZLibStream(stream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(input);
        }

        return stream.ToArray();
    }

    private sealed class EntryBase
    {
        public required Lazy<byte[]> Bytes { get; init; }
        public long CreatedTicks { get; init; }
    }
}
