using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>奖励轨服装搜索：从运行时 customization/trader suits/locale 主动适配原版与 mod 注入服装。</summary>
[Injectable]
public class BattlePassClothingSearchService(
    Services.DatabaseService databaseService,
    Services.LocaleService localeService,
    BattlePassClothingCatalogService clothingCatalog
)
{
    public List<BattlePassClothingSearchResult> Search(string? q, int limit)
    {
        var query = (q ?? "").Trim();
        var capped = Math.Clamp(limit, 1, 100);
        var localeDbs = CollectLocaleDbs();

        var hits = new List<BattlePassClothingSearchResult>();
        foreach (var entry in clothingCatalog.GetEntries())
        {
            var offer = entry.Offers.FirstOrDefault(candidate => candidate.IsActive) ?? entry.Offers.FirstOrDefault();
            var candidates = BuildLocaleCandidates(entry);
            var names = ResolveNames(candidates, entry.Item, localeDbs);
            var searchTerms = BuildSearchTerms(entry, candidates, names);
            if (query.Length > 0 && !searchTerms.Any(term => term.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            hits.Add(new BattlePassClothingSearchResult
            {
                Id = entry.SuiteId,
                SuitId = entry.SuiteId,
                Name = string.IsNullOrWhiteSpace(names.Name) ? entry.SuiteId : names.Name,
                ShortName = names.ShortName,
                Parent = entry.Item.Parent,
                BodyPart = entry.Item.Properties?.BodyPart,
                Side = entry.Item.Properties?.Side ?? [],
                OfferId = offer?.OfferId,
                TraderId = offer?.TraderId,
                IsTraderSuit = entry.Offers.Count > 0,
                IsActive = offer?.IsActive ?? false,
                SearchTerms = searchTerms,
            });
        }

        return hits
            .OrderBy(hit => SearchRank(hit, query))
            .ThenBy(hit => hit.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(hit => hit.SuitId, StringComparer.OrdinalIgnoreCase)
            .Take(capped)
            .ToList();
    }

    private List<Dictionary<string, string>> CollectLocaleDbs()
    {
        var localeDbs = new List<Dictionary<string, string>>();
        var globalLocales = databaseService.GetLocales().Global;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localeOrder = new[] { localeService.GetDesiredGameLocale(), "ch", "en" }
            .Concat(globalLocales.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));

        foreach (var locale in localeOrder)
        {
            if (!visited.Add(locale) || !globalLocales.ContainsKey(locale))
            {
                continue;
            }

            localeDbs.Add(localeService.GetLocaleDb(locale));
        }

        return localeDbs;
    }

    private static HashSet<string> BuildLocaleCandidates(BattlePassClothingCatalogEntry entry)
    {
        var item = entry.Item;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(set, entry.SuiteId);
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
        return set;
    }

    private static ClothingNames ResolveNames(
        HashSet<string> candidates,
        CustomizationItem item,
        List<Dictionary<string, string>> localeDbs)
    {
        var localizedTerms = CollectLocalizedTerms(candidates, localeDbs);
        var name = ResolveLocaleValue(candidates, localeDbs, ["Name", ""], item.Properties?.Name, item.Name);
        var shortName = ResolveLocaleValue(
            candidates,
            localeDbs,
            ["ShortName", "Name", ""],
            item.Properties?.ShortName,
            item.Properties?.Name,
            item.Name
        );
        return new ClothingNames { Name = name, ShortName = shortName, LocalizedTerms = localizedTerms };
    }

    private static string ResolveLocaleValue(
        HashSet<string> candidates,
        List<Dictionary<string, string>> localeDbs,
        string[] suffixes,
        params string?[] fallbacks)
    {
        foreach (var db in localeDbs)
        {
            foreach (var id in candidates)
            {
                foreach (var suffix in suffixes)
                {
                    var key = suffix.Length == 0 ? id : $"{id} {suffix}";
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

    private static HashSet<string> CollectLocalizedTerms(
        HashSet<string> candidates,
        List<Dictionary<string, string>> localeDbs)
    {
        var localized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var db in localeDbs)
        {
            foreach (var id in candidates)
            {
                foreach (var suffix in new[] { "", "Name", "ShortName", "Description" })
                {
                    var key = suffix.Length == 0 ? id : $"{id} {suffix}";
                    if (db.TryGetValue(key, out var value))
                    {
                        Add(localized, value);
                    }
                }
            }
        }

        return localized;
    }

    private static List<string> BuildSearchTerms(
        BattlePassClothingCatalogEntry entry,
        HashSet<string> candidates,
        ClothingNames names)
    {
        var item = entry.Item;
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            Add(terms, candidate);
        }

        foreach (var localized in names.LocalizedTerms)
        {
            Add(terms, localized);
        }

        Add(terms, entry.SuiteId);
        Add(terms, names.Name);
        Add(terms, names.ShortName);
        Add(terms, item.Parent);
        Add(terms, item.Properties?.BodyPart);
        foreach (var offer in entry.Offers)
        {
            Add(terms, offer.OfferId);
            Add(terms, offer.TraderId);
        }
        foreach (var side in item.Properties?.Side ?? [])
        {
            Add(terms, side);
        }

        return terms.ToList();
    }

    private static int SearchRank(BattlePassClothingSearchResult hit, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (hit.SuitId.Equals(query, StringComparison.OrdinalIgnoreCase)
            || (hit.OfferId?.Equals(query, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return 0;
        }

        if (hit.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
            || (hit.ShortName?.Equals(query, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return 1;
        }

        return hit.SearchTerms.Any(term => term.StartsWith(query, StringComparison.OrdinalIgnoreCase)) ? 2 : 3;
    }

    private static void Add(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            set.Add(value.Trim());
        }
    }

    private sealed record ClothingNames
    {
        public string Name { get; init; } = "";
        public string ShortName { get; init; } = "";
        public HashSet<string> LocalizedTerms { get; init; } = new(StringComparer.OrdinalIgnoreCase);
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
