using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SPTarkov.Server.Core.Extensions;

public static class TraderAssortExtensions
{
    /// <summary>
    /// Remove an item from an assort
    /// Must be removed from the assorts; items + barterScheme + LoyaltyLevel
    /// </summary>
    /// <param name="assort">Assort to remove item from</param>
    /// <param name="itemId">Id of item to remove from assort</param>
    /// <param name="isFlea">Is the assort being modified the flea market assort</param>
    /// <returns>Modified assort</returns>
    public static TraderAssort RemoveItemFromAssort(this TraderAssort assort, MongoId itemId, bool isFlea = false)
    {
        // Flea assort needs special handling, item must remain in assort but be flagged as locked
        if (isFlea && assort.BarterScheme.TryGetValue(itemId, out var listToUse))
        {
            foreach (var barterScheme in listToUse.SelectMany(barterSchemes => barterSchemes))
            {
                barterScheme.SptQuestLocked = true;
            }

            return assort;
        }

        assort.BarterScheme.Remove(itemId);
        assort.LoyalLevelItems.Remove(itemId);

        // The item being removed may have children linked to it, find and remove them too
        var idsToRemove = assort.Items.GetItemWithChildrenTpls(itemId).ToHashSet();
        assort.Items.RemoveAll(item => idsToRemove.Contains(item.Id));

        return assort;
    }

    /// <summary>
    ///     Given the blacklist provided, remove root items from assort
    /// </summary>
    /// <param name="assortToFilter">Trader assort to modify</param>
    /// <param name="itemsTplsToRemove">Item TPLs the assort should not have</param>
    public static void RemoveItemsFromAssort(this TraderAssort assortToFilter, IReadOnlySet<MongoId> itemsTplsToRemove)
    {
        if (itemsTplsToRemove.Count == 0 || assortToFilter.Items.Count == 0)
        {
            return;
        }

        var itemsById = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in assortToFilter.Items)
        {
            itemsById.TryAdd(item.Id.ToString(), item);
        }

        var rootIdsToRemove = new HashSet<MongoId>();
        foreach (var blockedItem in assortToFilter.Items.Where(item => itemsTplsToRemove.Contains(item.Template)))
        {
            var current = blockedItem;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (
                !string.IsNullOrWhiteSpace(current.ParentId)
                && visited.Add(current.Id.ToString())
                && itemsById.TryGetValue(current.ParentId, out var parent)
            )
            {
                current = parent;
            }

            // A banned child (for example a mod attachment in a preset) makes the whole offer unavailable.
            if (string.Equals(current.ParentId, "hideout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.SlotId, "hideout", StringComparison.OrdinalIgnoreCase))
            {
                rootIdsToRemove.Add(current.Id);
            }
        }

        foreach (var rootId in rootIdsToRemove)
        {
            assortToFilter.RemoveItemFromAssort(rootId);
        }
    }
}
