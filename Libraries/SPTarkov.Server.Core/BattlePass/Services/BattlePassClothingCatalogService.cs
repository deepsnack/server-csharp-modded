using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     Discovers grantable clothing suites from the live customization and trader databases.
///     Mods are observed at call time, after their PostDB registration has completed.
/// </summary>
[Injectable]
public class BattlePassClothingCatalogService(DatabaseService databaseService)
{
    internal List<BattlePassClothingCatalogEntry> GetEntries()
    {
        var offersBySuite = CollectTraderSuitOffers()
            .GroupBy(offer => offer.SuitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var entries = new List<BattlePassClothingCatalogEntry>();
        foreach (var (id, item) in databaseService.GetCustomization())
        {
            var suiteId = id.ToString();
            offersBySuite.TryGetValue(suiteId, out var offers);
            if (!IsItemCustomization(item))
            {
                continue;
            }

            if (!IsSuiteCustomization(item))
            {
                continue;
            }

            entries.Add(
                new BattlePassClothingCatalogEntry
                {
                    SuiteId = suiteId,
                    Item = item,
                    Offers = offers ?? [],
                }
            );
        }

        return entries;
    }

    /// <summary>Checks that an id is a live suite, rather than a body/feet/hands component tpl.</summary>
    public bool IsRegisteredSuite(MongoId suiteId)
    {
        if (!databaseService.GetCustomization().TryGetValue(suiteId, out var item))
        {
            return false;
        }

        if (!IsItemCustomization(item))
        {
            return false;
        }

        return IsSuiteCustomization(item);
    }

    internal static bool IsSuiteCustomization(CustomizationItem item)
    {
        if (!IsItemCustomization(item))
        {
            return false;
        }

        if (item.Parent is CustomisationTypeId.UPPER or CustomisationTypeId.LOWER)
        {
            return true;
        }

        return item.Parent == CustomisationTypeId.SUITS
            && (HasValue(item.Properties?.Body) || HasValue(item.Properties?.Feet));
    }

    private static bool IsItemCustomization(CustomizationItem item)
    {
        return string.Equals(item.Type, "Item", StringComparison.OrdinalIgnoreCase);
    }

    private List<BattlePassTraderSuitOffer> CollectTraderSuitOffers()
    {
        return databaseService
            .GetTraders()
            .SelectMany(trader => (trader.Value.Suits ?? [])
                .Select(suit => new BattlePassTraderSuitOffer
                {
                    SuitId = suit.SuiteId.ToString(),
                    OfferId = suit.Id.ToString(),
                    TraderId = trader.Key.ToString(),
                    IsActive = suit.IsActive ?? true,
                }))
            .Where(offer => !string.IsNullOrWhiteSpace(offer.SuitId))
            .ToList();
    }

    private static bool HasValue(MongoId? id)
    {
        return id.HasValue && !id.Value.IsEmpty;
    }
}

internal sealed record BattlePassClothingCatalogEntry
{
    public string SuiteId { get; init; } = "";
    public CustomizationItem Item { get; init; } = null!;
    public List<BattlePassTraderSuitOffer> Offers { get; init; } = [];
}

internal sealed record BattlePassTraderSuitOffer
{
    public string SuitId { get; init; } = "";
    public string OfferId { get; init; } = "";
    public string TraderId { get; init; } = "";
    public bool IsActive { get; init; }
}
