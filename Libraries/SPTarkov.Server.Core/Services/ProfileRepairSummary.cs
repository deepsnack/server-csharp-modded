using SPTarkov.Server.Core.Models.Common;

namespace SPTarkov.Server.Core.Services;

public sealed class ProfileRepairSummary(MongoId sessionId, string reason)
{
    public MongoId SessionId { get; } = sessionId;
    public string Reason { get; } = reason;
    public int ListsScanned { get; set; }
    public int EmptyIdsRemapped { get; set; }
    public int DuplicateItemsRemoved { get; set; }
    public int DuplicateIdsRemapped { get; set; }
    public int OrphanedItemsAdopted { get; set; }
    public int OrphanedItemsDetached { get; set; }
    public int CartridgePositionsRepacked { get; set; }
    public int BrokenReferencesRemoved { get; set; }
    public int StackCountsFixed { get; set; }
    public int TagsSanitized { get; set; }
    public int CustomizationsRestored { get; set; }
    public int InvalidOffersRemoved { get; set; }

    public bool Changed =>
        EmptyIdsRemapped > 0
        || DuplicateItemsRemoved > 0
        || DuplicateIdsRemapped > 0
        || OrphanedItemsAdopted > 0
        || OrphanedItemsDetached > 0
        || CartridgePositionsRepacked > 0
        || BrokenReferencesRemoved > 0
        || StackCountsFixed > 0
        || TagsSanitized > 0
        || CustomizationsRestored > 0
        || InvalidOffersRemoved > 0;

    public string Describe()
    {
        var parts = new List<string>();
        AddPart(parts, "empty ids remapped", EmptyIdsRemapped);
        AddPart(parts, "duplicate items removed", DuplicateItemsRemoved);
        AddPart(parts, "duplicate ids remapped", DuplicateIdsRemapped);
        AddPart(parts, "orphans adopted", OrphanedItemsAdopted);
        AddPart(parts, "orphans detached", OrphanedItemsDetached);
        AddPart(parts, "cartridge positions repacked", CartridgePositionsRepacked);
        AddPart(parts, "stale references removed", BrokenReferencesRemoved);
        AddPart(parts, "stack counts fixed", StackCountsFixed);
        AddPart(parts, "tags sanitized", TagsSanitized);
        AddPart(parts, "customizations restored", CustomizationsRestored);
        AddPart(parts, "invalid ragfair offers removed", InvalidOffersRemoved);

        return parts.Count == 0 ? "no changes" : string.Join(", ", parts);
    }

    private static void AddPart(List<string> parts, string label, int count)
    {
        if (count > 0)
        {
            parts.Add($"{label}: {count}");
        }
    }
}
