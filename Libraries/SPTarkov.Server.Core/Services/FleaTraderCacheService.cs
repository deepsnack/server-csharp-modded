using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     跳蚤/商人只读端点响应缓存（原 SPT-Optimizations FleaTraderCache 内联化）。
///     缓存最终 HTTP body（命中零计算零序列化），single-flight 合并并发；失效事件驱动 + TTL 兜底。
///     key 约定：flea:search:{session}:{hash} / flea:prices / flea:mprice:{hash}
///               trader:{session}:settings / trader:{session}:get:{id} / trader:{session}:assort:{id}
///     开关 CoreConfig.Features.FleaTraderCache（默认开）；关闭时 GetOrCompute 直通不缓存。
///     flea 与 trader 独立 TTL：flea 桶默认 300s（FleaTraderCacheTtlSeconds 可调）。offer 池变化
///     （上架/下架/购买/周期刷新）均有事件失效钩子，长 TTL 仅兜底漏钩并延长全局价格端点
///     （flea:prices / flea:mprice）跨会话命中；trader 桶保持 60s（会话级变化由
///     InvalidateTraderForSession 事件失效，短 TTL 防下线玩家响应体常驻内存）。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class FleaTraderCacheService(ConfigServer configServer)
{
    protected readonly RequestCoalescingCache traderCache = new(TimeSpan.FromSeconds(60));
    protected RequestCoalescingCache? fleaCache;
    protected bool? enabled;

    public virtual bool Enabled => enabled ??= configServer.GetConfig<CoreConfig>().Features.FleaTraderCache;

    /// <summary>flea 桶 TTL 兜底（懒创建，避免构造期读配置）。</summary>
    protected virtual RequestCoalescingCache FleaCache => fleaCache ??= CreateFleaCache();

    /// <summary>解析 flea 桶 TTL：配置 &gt;0 用配置，否则默认 300s。</summary>
    internal static TimeSpan ResolveFleaTtl(int? configuredSeconds)
    {
        return TimeSpan.FromSeconds(configuredSeconds is > 0 ? configuredSeconds.Value : 300);
    }

    protected RequestCoalescingCache CreateFleaCache()
    {
        var features = configServer.GetConfig<CoreConfig>().Features;
        return new RequestCoalescingCache(ResolveFleaTtl(features.FleaTraderCacheTtlSeconds));
    }

    /// <summary>开关开启时走缓存/合并；关闭时直通执行。</summary>
    public string GetOrCompute(string key, Func<string> compute)
    {
        if (!Enabled)
        {
            return compute();
        }

        var target = key.StartsWith("flea:", StringComparison.Ordinal) ? FleaCache : traderCache;
        return target.GetOrCompute(key, compute);
    }

    /// <summary>offer 池变化（上架/下架/购买/周期刷新）→ 清整个 flea 桶。</summary>
    public void InvalidateFlea()
    {
        FleaCache.InvalidateBucket("flea");
    }

    /// <summary>仅清 flea 搜索响应：商人购买后 assort 实时库存/限购变化，不动全局价格缓存。</summary>
    public void InvalidateFleaSearch()
    {
        FleaCache.InvalidatePrefix("flea:search:");
    }

    public void InvalidateTrader()
    {
        traderCache.InvalidateBucket("trader");
    }

    /// <summary>购买等会话级变化后只清该 session 的 trader 缓存。</summary>
    public void InvalidateTraderForSession(string sessionId)
    {
        traderCache.InvalidatePrefix($"trader:{sessionId}:");
    }

    /// <summary>周期回收超 TTL 条目（失效改为事件驱动后，防不再被访问的响应体常驻内存）。</summary>
    public void SweepExpired()
    {
        FleaCache.SweepExpired();
        traderCache.SweepExpired();
    }
}
