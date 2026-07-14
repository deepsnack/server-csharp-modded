using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>奖励轨服装搜索：从运行时 customization/trader suits/locale 主动适配原版与 mod 注入服装。</summary>
[Injectable]
public class BattlePassClothingSearchService(
    Services.DatabaseService databaseService,
    Services.LocaleService localeService
)
{
    public List<BattlePassClothingSearchResult> Search(string? q, int limit)
    {
        var query = (q ?? "").Trim();
        var capped = Math.Clamp(limit, 1, 100);
        var customization = databaseService.GetCustomization();
        var offers = CollectTraderSuitOffers();
        var offerBySuit = offers
            .GroupBy(offer => offer.SuitId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.FirstOrDefault(offer => offer.IsActive) ?? group.First(), StringComparer.OrdinalIgnoreCase);

        var hits = new List<BattlePassClothingSearchResult>();
        foreach (var (id, item) in customization)
        {
            var suitId = id.ToString();
            var isTraderSuit = offerBySuit.TryGetValue(suitId, out var offer);
            if (!isTraderSuit && !IsClothingCustomization(item))
            {
                continue;
            }

            var candidates = BuildLocaleCandidates(suitId, item, offer);
            var names = ResolveNames(candidates, item);
            var searchTerms = BuildSearchTerms(suitId, item, offer, candidates, names);
            if (query.Length > 0 && !searchTerms.Any(term => term.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            hits.Add(new BattlePassClothingSearchResult
            {
                Id = suitId,
                SuitId = suitId,
                Name = string.IsNullOrWhiteSpace(names.Name) ? suitId : names.Name,
                ShortName = names.ShortName,
                Parent = item.Parent,
                BodyPart = item.Properties?.BodyPart,
                Side = item.Properties?.Side ?? [],
                OfferId = offer?.OfferId,
                TraderId = offer?.TraderId,
                IsTraderSuit = isTraderSuit,
                IsActive = offer?.IsActive ?? false,
                SearchTerms = searchTerms,
            });

            if (hits.Count >= capped)
            {
                break;
            }
        }

        return hits;
    }

    private List<TraderSuitOfferInfo> CollectTraderSuitOffers()
    {
        return databaseService
            .GetTraders()
            .SelectMany(trader => (trader.Value.Suits ?? [])
                .Select(suit => new TraderSuitOfferInfo
                {
                    SuitId = suit.SuiteId.ToString(),
                    OfferId = suit.Id.ToString(),
                    TraderId = trader.Key.ToString(),
                    IsActive = suit.IsActive ?? true,
                }))
            .Where(offer => !string.IsNullOrWhiteSpace(offer.SuitId))
            .ToList();
    }

    private static bool IsClothingCustomization(CustomizationItem item)
    {
        return item.Parent is CustomisationTypeId.SUITS or CustomisationTypeId.UPPER or CustomisationTypeId.LOWER;
    }

    private static HashSet<string> BuildLocaleCandidates(string suitId, CustomizationItem item, TraderSuitOfferInfo? offer)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(set, suitId);
        Add(set, item.Id.ToString());
        Add(set, item.Name);
        Add(set, item.Properties?.Name);
        Add(set, item.Properties?.ShortName);
        Add(set, item.Properties?.Description);
        Add(set, item.Properties?.BodyPart);
        Add(set, item.Properties?.Body?.ToString());
        Add(set, item.Properties?.Feet?.ToString());
        Add(set, item.Properties?.Hands?.ToString());
        Add(set, item.Properties?.UsecTemplateId?.ToString());
        Add(set, item.Properties?.BearTemplateId?.ToString());
        Add(set, offer?.OfferId);
        Add(set, offer?.TraderId);
        return set;
    }

    private ClothingNames ResolveNames(HashSet<string> candidates, CustomizationItem item)
    {
        var name = ResolveLocaleValue(candidates, "Name", item.Properties?.Name, item.Name);
        var shortName = ResolveLocaleValue(candidates, "ShortName", item.Properties?.ShortName, item.Properties?.Name, item.Name);
        return new ClothingNames { Name = name, ShortName = shortName };
    }

    private string ResolveLocaleValue(HashSet<string> candidates, string suffix, params string?[] fallbacks)
    {
        var localeDbs = new[]
        {
            localeService.GetLocaleDb(),
            localeService.GetLocaleDb("ch"),
            localeService.GetLocaleDb("en"),
        };

        foreach (var id in candidates)
        {
            foreach (var key in new[] { $"{id} {suffix}", id })
            {
                foreach (var db in localeDbs)
                {
                    if (db.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }

        foreach (var fallback in fallbacks)
        {
            if (string.IsNullOrWhiteSpace(fallback))
            {
                continue;
            }

            foreach (var db in localeDbs)
            {
                if (db.TryGetValue(fallback, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return fallback;
        }

        return "";
    }

    private static List<string> BuildSearchTerms(
        string suitId,
        CustomizationItem item,
        TraderSuitOfferInfo? offer,
        HashSet<string> candidates,
        ClothingNames names)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            Add(terms, candidate);
        }

        Add(terms, suitId);
        Add(terms, names.Name);
        Add(terms, names.ShortName);
        Add(terms, item.Parent);
        Add(terms, item.Properties?.BodyPart);
        Add(terms, offer?.OfferId);
        Add(terms, offer?.TraderId);
        foreach (var side in item.Properties?.Side ?? [])
        {
            Add(terms, side);
        }

        return terms.ToList();
    }

    private static void Add(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            set.Add(value.Trim());
        }
    }

    private sealed record TraderSuitOfferInfo
    {
        public string SuitId { get; init; } = "";
        public string OfferId { get; init; } = "";
        public string TraderId { get; init; } = "";
        public bool IsActive { get; init; }
    }

    private sealed record ClothingNames
    {
        public string Name { get; init; } = "";
        public string ShortName { get; init; } = "";
    }
}

public sealed record BattlePassClothingSearchResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("suitId")]
    public string SuitId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("shortName")]
    public string? ShortName { get; init; }

    [JsonPropertyName("parent")]
    public string? Parent { get; init; }

    [JsonPropertyName("bodyPart")]
    public string? BodyPart { get; init; }

    [JsonPropertyName("side")]
    public List<string> Side { get; init; } = [];

    [JsonPropertyName("offerId")]
    public string? OfferId { get; init; }

    [JsonPropertyName("traderId")]
    public string? TraderId { get; init; }

    [JsonPropertyName("isTraderSuit")]
    public bool IsTraderSuit { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; }

    [JsonIgnore]
    public List<string> SearchTerms { get; init; } = [];
}
