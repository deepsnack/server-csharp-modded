using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     跳蚤/商人只读端点响应缓存（原 SPT-Optimizations FleaTraderCache 内联化）。
///     缓存最终 HTTP body（命中零计算零序列化），single-flight 合并并发；失效事件驱动 + 60s TTL 兜底。
///     key 约定：flea:search:{session}:{hash} / flea:prices / flea:mprice:{hash}
///               trader:{session}:settings / trader:{session}:get:{id} / trader:{session}:assort:{id}
///     开关 CoreConfig.Features.FleaTraderCache（默认开）；关闭时 GetOrCompute 直通不缓存。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class FleaTraderCacheService(ConfigServer configServer)
{
    protected readonly RequestCoalescingCache cache = new(TimeSpan.FromSeconds(60));
    protected bool? enabled;

    public bool Enabled => enabled ??= configServer.GetConfig<Models.Spt.Config.CoreConfig>().Features.FleaTraderCache;

    /// <summary>开关开启时走缓存/合并；关闭时直通执行。</summary>
    public string GetOrCompute(string key, Func<string> compute)
    {
        return Enabled ? cache.GetOrCompute(key, compute) : compute();
    }

    public void InvalidateFlea()
    {
        cache.InvalidateBucket("flea");
    }

    public void InvalidateTrader()
    {
        cache.InvalidateBucket("trader");
    }

    /// <summary>购买等会话级变化后只清该 session 的 trader 缓存。</summary>
    public void InvalidateTraderForSession(string sessionId)
    {
        cache.InvalidatePrefix($"trader:{sessionId}:");
    }
}
