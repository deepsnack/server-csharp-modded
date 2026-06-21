using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Callbacks;

[Injectable(TypePriority = OnLoadOrder.TraderCallbacks)]
public class TraderCallbacks(
    HttpResponseUtil httpResponseUtil,
    TraderController traderController,
    FleaTraderCacheService fleaTraderCache,
    ConfigServer configServer
) : IOnLoad, IOnUpdate
{
    protected readonly TraderConfig TraderConfig = configServer.GetConfig<TraderConfig>();

    public Task OnLoad()
    {
        traderController.Load();
        return Task.CompletedTask;
    }

    public Task<bool> OnUpdate(long _)
    {
        // App 主循环每 5s 回调一次本方法；仅当本轮确实刷新了某商人 assort 时才失效缓存。
        // （此前无条件失效导致 trader 缓存最长只活 5s、命中率≈0，等于没有缓存）
        if (traderController.Update())
        {
            fleaTraderCache.InvalidateTrader();
        }

        // 周期回收超 TTL 的缓存条目，防止不再被访问的大响应体（下线玩家的 assort 等）常驻内存
        fleaTraderCache.SweepExpired();

        return Task.FromResult(true);
    }

    /// <summary>
    ///     Handle client/trading/api/traderSettings
    /// </summary>
    public ValueTask<string> GetTraderSettings(string url, EmptyRequestData _, MongoId sessionID)
    {
        // 高频只读端点缓存（原 TraderSettingsCachePatch 内联；开关关闭时直通）
        var body = fleaTraderCache.GetOrCompute(
            $"trader:{sessionID}:settings",
            () => httpResponseUtil.GetBody(traderController.GetAllTraders(sessionID))
        );
        return new ValueTask<string>(body);
    }

    /// <summary>
    ///     Handle client/trading/api/getTrader
    /// </summary>
    public ValueTask<string> GetTrader(string url, EmptyRequestData _, MongoId sessionID)
    {
        var traderID = url.Replace("/client/trading/api/getTrader/", "");

        // 原 TraderGetCachePatch 内联
        var body = fleaTraderCache.GetOrCompute(
            $"trader:{sessionID}:get:{traderID}",
            () => httpResponseUtil.GetBody(traderController.GetTrader(sessionID, traderID))
        );
        return new ValueTask<string>(body);
    }

    /// <summary>
    ///     Handle client/trading/api/getTraderAssort
    /// </summary>
    /// <returns></returns>
    public ValueTask<string> GetAssort(string url, EmptyRequestData _, MongoId sessionID)
    {
        var traderID = url.Replace("/client/trading/api/getTraderAssort/", "");

        // 原 TraderAssortCachePatch 内联
        var body = fleaTraderCache.GetOrCompute(
            $"trader:{sessionID}:assort:{traderID}",
            () => httpResponseUtil.GetBody(traderController.GetAssort(sessionID, traderID))
        );
        return new ValueTask<string>(body);
    }
}
