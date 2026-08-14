using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>按通行证自有购买权账本过滤商人货架。</summary>
[Injectable(InjectionType.Singleton)]
public class BattlePassTraderAccessService(ISptLogger<BattlePassTraderAccessService> logger)
{
    private const string PolicyKey = "battlepass-purchase-rights";

    public void Register()
    {
        TraderAssortAccessPolicy.Register(PolicyKey, Filter);
    }

    public TraderAssort Filter(MongoId sessionId, MongoId traderId, TraderAssort assort, bool isFlea)
    {
        if (traderId != new MongoId(BattlePassTraderSync.TraderIdHex))
        {
            return assort;
        }

        try
        {
            var profileId = sessionId.ToString();
            var season = BattlePassStore.GetSeason();
            var progress = BattlePassStore.GetProgress(profileId);
            if (!string.Equals(progress.SeasonId, season.SeasonId, StringComparison.Ordinal))
            {
                progress = new BpProgress { SeasonId = season.SeasonId, RewardLedgerInitialized = true };
                BattlePassStore.SaveProgress(profileId, progress);
            }

            var tracks = BattlePassStore.GetTracks();
            if (BattlePassPurchaseRights.Migrate(progress, tracks))
            {
                BattlePassStore.SaveProgress(profileId, progress);
            }

            return FilterOffers(progress, BattlePassStore.GetOffers(), assort, isFlea);
        }
        catch (Exception ex)
        {
            // 失败时保持默认关闭：移除全部通行证货架，避免权限系统异常导致商品向所有玩家开放。
            logger.Error($"[SPT-BattlePass] 购买权过滤失败 profile={sessionId}: {ex.Message}");
            foreach (var offer in BattlePassStore.GetOffers())
            {
                if (!string.IsNullOrWhiteSpace(offer.Id))
                {
                    assort.RemoveItemFromAssort(BattlePassTraderSync.OfferRootItemId(offer.Id), isFlea);
                }
            }
        }

        return assort;
    }

    internal static TraderAssort FilterOffers(
        BpProgress progress,
        IEnumerable<BpTraderOffer> offers,
        TraderAssort assort,
        bool isFlea
    )
    {
        foreach (var offer in offers)
        {
            if (string.IsNullOrWhiteSpace(offer.Id) || BattlePassPurchaseRights.Has(progress, offer.Id))
            {
                continue;
            }

            assort.RemoveItemFromAssort(BattlePassTraderSync.OfferRootItemId(offer.Id), isFlea);
        }

        return assort;
    }
}
