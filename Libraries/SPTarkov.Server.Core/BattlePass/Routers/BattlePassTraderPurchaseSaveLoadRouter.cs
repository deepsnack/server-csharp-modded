using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Profile;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     Clears stale BattlePass trader purchase limits when a profile is materialized. This preserves the
///     reset-on-startup behavior without loading every offline profile when lazy profile loading is enabled.
/// </summary>
[Injectable]
public sealed class BattlePassTraderPurchaseSaveLoadRouter : SaveLoadRouter
{
    protected override List<HandledRoute> GetHandledRoutes()
    {
        return [new HandledRoute("spt-battlepass-trader-purchases", false)];
    }

    protected override SptProfile HandleLoadInternal(SptProfile profile)
    {
        BattlePassTraderSync.ResetNativeTraderPurchase(profile);
        return profile;
    }
}
