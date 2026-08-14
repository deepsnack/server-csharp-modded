using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Callbacks;

[Injectable(TypePriority = OnLoadOrder.RagfairCallbacks)]
public class RagfairCallbacks(
    HttpResponseUtil httpResponseUtil,
    RagfairServer ragfairServer,
    RagfairController ragfairController,
    RagfairTaxService ragfairTaxService,
    RagfairPriceService ragfairPriceService,
    FleaTraderCacheService fleaTraderCache,
    JsonUtil jsonUtil,
    ConfigServer configServer
) : IOnLoad, IOnUpdate
{
    protected readonly RagfairConfig RagfairConfig = configServer.GetConfig<RagfairConfig>();

    public Task OnLoad()
    {
        ragfairPriceService.Load();
        ragfairServer.Load();

        return Task.CompletedTask;
    }

    public Task<bool> OnUpdate(long secondsSinceLastRun)
    {
        if (secondsSinceLastRun < RagfairConfig.RunIntervalSeconds)
        {
            // Not enough time has passed since last run, exit early
            return Task.FromResult(false);
        }

        // There is a flag inside this class that only makes it run once.
        ragfairServer.AddPlayerOffers();

        RunOfferUpdatesAndInvalidateIfChanged(
            () => RunLegacyUpdateAndConsumeChanges(
                ragfairController.Update,
                ragfairController.ConsumeLastUpdateChanged
            ),
            () => RunLegacyUpdateAndConsumeChanges(ragfairServer.Update, ragfairServer.ConsumeLastUpdateChanged),
            fleaTraderCache.InvalidateFlea
        );

        return Task.FromResult(true);
    }

    /// <summary>
    ///     Run both background update sources every interval, but only invalidate cached HTTP responses when
    ///     at least one source actually changed the visible offer pool.
    /// </summary>
    internal static bool RunOfferUpdatesAndInvalidateIfChanged(
        Func<bool> updatePlayerOffers,
        Func<bool> updateServerOffers,
        Action invalidate
    )
    {
        var offersChanged = updatePlayerOffers();
        offersChanged |= updateServerOffers();
        if (offersChanged)
        {
            invalidate();
        }

        return offersChanged;
    }

    /// <summary>
    ///     Dispatch through the established virtual Update method so binary Mods retain their takeover point,
    ///     then consume the mutation result recorded by the base implementation.
    /// </summary>
    internal static bool RunLegacyUpdateAndConsumeChanges(Action update, Func<bool> consumeChanged)
    {
        update();
        return consumeChanged();
    }

    /// <summary>
    ///     Handle client/ragfair/search
    ///     Handle client/ragfair/find
    /// </summary>
    /// <param name="url"></param>
    /// <param name="info"></param>
    /// <param name="sessionID">Session/player id</param>
    /// <returns></returns>
    public ValueTask<string> Search(string url, SearchRequestData info, MongoId sessionID)
    {
        // 高频只读端点缓存 + 并发合并（原 RagfairSearchCachePatch 内联；开关关闭时直通）
        // key 直接用完整请求 JSON：32 位 GetHashCode 碰撞会让玩家拿到另一搜索条件的缓存响应
        //（客户端 selectedCategory 对不上 → 列表显示为空），完整串彻底消除碰撞
        var key = $"flea:search:{sessionID}:{jsonUtil.Serialize(NormalizeSearchRequestForCache(info))}";
        var body = fleaTraderCache.GetOrCompute(key, () => httpResponseUtil.GetBody(ragfairController.GetOffers(sessionID, info)));
        return new ValueTask<string>(body);
    }

    /// <summary>
    ///     Remove client refresh metadata that is not read by flea filtering/sorting, so semantically identical
    ///     requests share a cache entry. Keep all response-affecting fields unchanged.
    /// </summary>
    internal static SearchRequestData NormalizeSearchRequestForCache(SearchRequestData info)
    {
        return info with { Tm = null, Reload = null };
    }

    /// <summary>
    ///     Handle client/ragfair/itemMarketPrice
    /// </summary>
    /// <param name="url"></param>
    /// <param name="info"></param>
    /// <param name="sessionID">Session/player id</param>
    /// <returns></returns>
    public ValueTask<string> GetMarketPrice(string url, GetMarketPriceRequestData info, MongoId sessionID)
    {
        // 原 RagfairMarketPriceCachePatch 内联（全局价格，无 session 差异；完整 JSON 为键防碰撞）
        var key = $"flea:mprice:{jsonUtil.Serialize(info)}";
        var body = fleaTraderCache.GetOrCompute(
            key,
            () => httpResponseUtil.GetBody(ragfairController.GetItemMinAvgMaxFleaPriceValues(info))
        );
        return new ValueTask<string>(body);
    }

    /// <summary>
    ///     Handle RagFairAddOffer event
    /// </summary>
    /// <param name="pmcData">Players PMC profile</param>
    /// <param name="info"></param>
    /// <param name="sessionID">Session/player id</param>
    /// <returns></returns>
    public ItemEventRouterResponse AddOffer(PmcData pmcData, AddOfferRequestData info, MongoId sessionID)
    {
        var response = ragfairController.AddPlayerOffer(pmcData, info, sessionID);

        // 玩家上架 → offer 池变化，清跳蚤缓存（原 RagfairAddOfferInvalidatePatch 内联）
        fleaTraderCache.InvalidateFlea();

        return response;
    }

    /// <summary>
    ///     Handle RagFairRemoveOffer event
    /// </summary>
    /// <param name="pmcData">Players PMC profile</param>
    /// <param name="info"></param>
    /// <param name="sessionID">Session/player id</param>
    /// <returns></returns>
    public ItemEventRouterResponse RemoveOffer(PmcData pmcData, RemoveOfferRequestData info, MongoId sessionID)
    {
        var response = ragfairController.FlagOfferForRemoval(info.OfferId, sessionID);

        // 玩家下架 → 清跳蚤缓存（原 RagfairRemoveOfferInvalidatePatch 内联）
        fleaTraderCache.InvalidateFlea();

        return response;
    }

    /// <summary>
    ///     Handle RagFairRenewOffer event
    /// </summary>
    /// <param name="pmcData">Players PMC profile</param>
    /// <param name="info"></param>
    /// <param name="sessionID">Session/player id</param>
    /// <returns></returns>
    public ItemEventRouterResponse ExtendOffer(PmcData pmcData, ExtendOfferRequestData info, MongoId sessionID)
    {
        var response = ragfairController.ExtendOffer(info, sessionID);

        // 玩家续期 → 清跳蚤缓存（原 RagfairExtendOfferInvalidatePatch 内联）
        fleaTraderCache.InvalidateFlea();

        return response;
    }

    /// <summary>
    ///     Handle /client/items/prices
    ///     Called when clicking an item to list on flea
    /// </summary>
    /// <param name="url"></param>
    /// <param name="_"></param>
    /// <param name="sessionID">Session/player id</param>
    /// <returns></returns>
    public ValueTask<string> GetFleaPrices(string url, EmptyRequestData _, MongoId sessionID)
    {
        // 原 RagfairFleaPricesCachePatch 内联（全局，无 session 差异）
        var body = fleaTraderCache.GetOrCompute("flea:prices", () => httpResponseUtil.GetBody(ragfairController.GetAllFleaPrices()));
        return new ValueTask<string>(body);
    }

    /// <summary>
    ///     Handle client/reports/ragfair/send
    /// </summary>
    /// <param name="url"></param>
    /// <param name="info"></param>
    /// <param name="sessionID">Session/player id</param>
    /// <returns></returns>
    public ValueTask<string> SendReport(string url, SendRagfairReportRequestData info, MongoId sessionID)
    {
        return new ValueTask<string>(httpResponseUtil.NullResponse());
    }

    public ValueTask<string> StorePlayerOfferTaxAmount(string url, StorePlayerOfferTaxAmountRequestData info, MongoId sessionID)
    {
        ragfairTaxService.StoreClientOfferTaxValue(sessionID, info);
        return new ValueTask<string>(httpResponseUtil.NullResponse());
    }

    /// <summary>
    ///     Handle client/ragfair/offer/findbyid
    /// </summary>
    /// <param name="url"></param>
    /// <param name="info"></param>
    /// <param name="sessionID">Session/player id</param>
    /// <returns></returns>
    public ValueTask<string> GetFleaOfferById(string url, GetRagfairOfferByIdRequest info, MongoId sessionID)
    {
        return new ValueTask<string>(httpResponseUtil.GetBody(ragfairController.GetOfferByInternalId(sessionID, info)));
    }
}
