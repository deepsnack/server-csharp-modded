using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using ProfileInsurance = SPTarkov.Server.Core.Models.Eft.Profile.Insurance;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     存档物品自修复（原 SPT-Optimizations ProfileAutoRepair 内置化）：
///     修复重复/空物品 id、孤儿 parentId、弹匣弹药槽位、悬空引用等存档损伤。
///     开关 CoreConfig.Features.AutoRepairProfiles（默认开）；关闭时 RepairProfile 直接空返。
///     调用点：启动修复（ProfileAutoRepairOnLoad）、存盘前（SaveServer.SaveProfileAsync）、
///     启动器存档列表（ProfileController.GetCompleteProfile）。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ProfileAutoRepairService(
    DatabaseService databaseService,
    JsonUtil jsonUtil,
    ConfigServer configServer,
    ISptLogger<ProfileAutoRepairService> logger
)
{
    protected readonly Models.Spt.Config.CoreConfig CoreConfig = configServer.GetConfig<Models.Spt.Config.CoreConfig>();
    // 仅剥离真正会损坏存档/客户端的字符：Unicode "Other" 类别（控制符 Cc、格式符 Cf、代理 Cs、私用 Co、未分配 Cn）。
    // 不再按 ASCII 白名单清洗——EFT 客户端原生支持中/俄/韩/日等多语言标签且输入框已自校验，
    // 旧正则 [^a-zA-Z0-9 -] 会把全部中文等非 ASCII 字符当非法删除，导致中文标签被整段清空。
    private static readonly Regex InvalidTagNameCharacters = new(@"\p{C}", RegexOptions.Compiled);

    public bool Enabled => CoreConfig.Features.AutoRepairProfiles;

    private sealed record ItemListContext(
        string Name,
        List<Item> Items,
        string? AdoptParentId,
        string AdoptSlotId,
        Action<MongoId, MongoId>? OnIdRemapped = null
    );

    public ProfileRepairSummary RepairProfile(SptProfile? profile, MongoId sessionId, string reason, bool logSummary = true)
    {
        var summary = new ProfileRepairSummary(sessionId, reason);
        if (!Enabled || profile is null || profile.ProfileInfo?.InvalidOrUnloadableProfile is true)
        {
            return summary;
        }

        RepairCharacter(profile.CharacterData?.PmcData, "pmc", summary);
        RepairCharacter(profile.CharacterData?.ScavData, "scav", summary);
        RepairRagfairOffers(profile.CharacterData?.PmcData, "pmc", summary);
        RepairRagfairOffers(profile.CharacterData?.ScavData, "scav", summary);
        RepairUserBuilds(profile.UserBuildData, summary);
        RepairDialogues(profile.DialogueRecords, summary);
        RepairInsurance(profile.InsuranceList, summary);
        RepairBtrDelivery(profile.BtrDeliveryList, summary);

        if (summary.Changed && logSummary)
        {
            logger.Warning(
                $"[ProfileAutoRepair] repaired profile {sessionId} during {reason}; {summary.Describe()}"
            );
        }

        return summary;
    }

    private void RepairCharacter(PmcData? character, string characterName, ProfileRepairSummary summary)
    {
        var inventory = character?.Inventory;
        if (inventory?.Items is not { Count: > 0 } items)
        {
            return;
        }

        var adoptParentId = ToRootId(inventory.Equipment) ?? ToRootId(inventory.Stash);
        RepairItemList(new ItemListContext($"{characterName}.inventory", items, adoptParentId, "hideout"), summary);
        RemoveBrokenCharacterReferences(character!, summary);
        RestoreInvalidCustomizations(character, summary);
    }

    /// <summary>
    ///     返回客户端可安全解析的挂单列表（剔除 root 悬空 / items 为空 / 物品模板未知的坏单）。
    ///     供 RepairProfile 与市场挂单恢复（AddPlayerOffers）共用，防止坏单进入档案或市场后
    ///     导致客户端 profile/list 或跳蚤列表反序列化抛 KeyNotFoundException。
    /// </summary>
    public List<RagfairOffer> FilterClientSafeOffers(List<RagfairOffer>? offers)
    {
        if (offers is not { Count: > 0 })
        {
            return offers ?? [];
        }

        var knownTemplates = databaseService.GetItems();
        return offers.Where(offer => RagfairOfferIsClientSafe(offer, knownTemplates)).ToList();
    }

    /// <summary>
    ///     校验档案 RagfairInfo 挂单，剔除会在客户端反序列化 Offer 时抛
    ///     KeyNotFoundException（"The given key 'xxx' was not present in the dictionary."）的坏单：
    ///     客户端 GClass2357.Deserialize 对每张单执行 FlatItemsToTree(items).Items[root]，
    ///     当 items 为空、root 不在 items 中、或物品模板不存在（模板未知的物品会被客户端
    ///     跳过后 root 悬空）时客户端即抛该异常并导致 profile/list 解析失败。
    /// </summary>
    private void RepairRagfairOffers(PmcData? character, string characterName, ProfileRepairSummary summary)
    {
        var ragfairInfo = character?.RagfairInfo;
        var offers = ragfairInfo?.Offers;
        if (offers is not { Count: > 0 })
        {
            return;
        }

        var validOffers = FilterClientSafeOffers(offers);
        if (validOffers.Count != offers.Count)
        {
            ragfairInfo!.Offers = validOffers;
            summary.InvalidOffersRemoved += offers.Count - validOffers.Count;
            logger.Warning(
                $"[ProfileAutoRepair] removed {offers.Count - validOffers.Count} invalid ragfair offer(s) from {characterName} profile"
            );
        }
    }

    private static bool RagfairOfferIsClientSafe(
        RagfairOffer offer,
        IReadOnlyDictionary<MongoId, TemplateItem> knownTemplates
    )
    {
        var items = offer.Items;
        if (items is not { Count: > 0 } || offer.Root.IsEmpty)
        {
            return false;
        }

        var itemIds = items.Select(item => item.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!itemIds.Contains(offer.Root.ToString()))
        {
            return false;
        }

        foreach (var item in items)
        {
            if (item.Template.IsEmpty || !knownTemplates.ContainsKey(item.Template))
            {
                return false;
            }
        }

        return true;
    }

    private void RepairUserBuilds(UserBuilds? userBuilds, ProfileRepairSummary summary)
    {
        if (userBuilds is null)
        {
            return;
        }

        foreach (var build in userBuilds.WeaponBuilds ?? [])
        {
            if (build.Items is not { Count: > 0 })
            {
                continue;
            }

            RepairItemList(
                new ItemListContext(
                    $"userbuild.weapon:{build.Name ?? build.Id.ToString()}",
                    build.Items,
                    null,
                    "hideout",
                    (oldId, newId) =>
                    {
                        if (build.Root == oldId.ToString())
                        {
                            build.Root = newId.ToString();
                        }
                    }
                ),
                summary
            );
        }

        foreach (var build in userBuilds.EquipmentBuilds ?? [])
        {
            if (build.Items is not { Count: > 0 })
            {
                continue;
            }

            RepairItemList(
                new ItemListContext(
                    $"userbuild.equipment:{build.Name ?? build.Id.ToString()}",
                    build.Items,
                    null,
                    "hideout",
                    (oldId, newId) =>
                    {
                        if (build.Root == oldId)
                        {
                            build.Root = newId;
                        }
                    }
                ),
                summary
            );
        }
    }

    private void RepairDialogues(Dictionary<MongoId, Dialogue>? dialogues, ProfileRepairSummary summary)
    {
        if (dialogues is null)
        {
            return;
        }

        foreach (var (dialogueId, dialogue) in dialogues)
        {
            foreach (var message in dialogue.Messages ?? [])
            {
                var messageItems = message.Items;
                if (messageItems?.Data is not { Count: > 0 } data)
                {
                    continue;
                }

                var stashId = ToRootId(messageItems.Stash);
                RepairItemList(
                    new ItemListContext(
                        $"dialogue:{dialogueId}:{message.Id}",
                        data,
                        stashId,
                        "main",
                        (oldId, newId) =>
                        {
                            if (messageItems.Stash == oldId)
                            {
                                messageItems.Stash = newId;
                            }
                        }
                    ),
                    summary
                );

                if (data.Count == 0 && message.HasRewards.GetValueOrDefault(false))
                {
                    message.HasRewards = false;
                    message.RewardCollected = true;
                    summary.BrokenReferencesRemoved++;
                }
            }
        }
    }

    private void RepairInsurance(List<ProfileInsurance>? insuranceList, ProfileRepairSummary summary)
    {
        if (insuranceList is null)
        {
            return;
        }

        for (var i = 0; i < insuranceList.Count; i++)
        {
            var items = insuranceList[i].Items;
            if (items is not { Count: > 0 })
            {
                continue;
            }

            RepairItemList(new ItemListContext($"insurance:{i}", items, null, "hideout"), summary);
        }
    }

    private void RepairBtrDelivery(List<BtrDelivery>? deliveries, ProfileRepairSummary summary)
    {
        if (deliveries is null)
        {
            return;
        }

        foreach (var delivery in deliveries)
        {
            if (delivery.Items is not { Count: > 0 })
            {
                continue;
            }

            RepairItemList(new ItemListContext($"btr:{delivery.Id}", delivery.Items, null, "hideout"), summary);
        }
    }

    private void RepairItemList(ItemListContext context, ProfileRepairSummary summary)
    {
        summary.ListsScanned++;

        RepairDuplicateIds(context, summary);
        RepairOrphanedParents(context, summary);
        RepackCartridgeStackSlots(context, summary);
        RepairStackCountsAndTags(context, summary);
    }

    private void RepairDuplicateIds(ItemListContext context, ProfileRepairSummary summary)
    {
        var items = context.Items;
        var usedIds = new HashSet<MongoId>();
        var firstById = new Dictionary<MongoId, Item>();
        var removeIndexes = new List<int>();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Id.IsEmpty)
            {
                var oldId = item.Id;
                item.Id = CreateUniqueId(usedIds);
                context.OnIdRemapped?.Invoke(oldId, item.Id);
                summary.EmptyIdsRemapped++;
            }

            if (!firstById.TryAdd(item.Id, item))
            {
                var first = firstById[item.Id];
                if (ItemsAreSerializedEqual(first, item) || ItemHasChildren(items, item.Id))
                {
                    removeIndexes.Add(i);
                    summary.DuplicateItemsRemoved++;
                    continue;
                }

                var oldId = item.Id;
                item.Id = CreateUniqueId(usedIds);
                firstById[item.Id] = item;
                context.OnIdRemapped?.Invoke(oldId, item.Id);
                summary.DuplicateIdsRemapped++;
            }

            usedIds.Add(item.Id);
        }

        for (var i = removeIndexes.Count - 1; i >= 0; i--)
        {
            items.RemoveAt(removeIndexes[i]);
        }
    }

    private void RepairOrphanedParents(ItemListContext context, ProfileRepairSummary summary)
    {
        var itemIds = context.Items.Select(item => item.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in context.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ParentId))
            {
                continue;
            }

            if (
                itemIds.Contains(item.ParentId)
                || string.Equals(item.ParentId, context.AdoptParentId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ParentId, "hideout", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(context.AdoptParentId))
            {
                item.ParentId = context.AdoptParentId;
                item.SlotId = context.AdoptSlotId;
                item.Location = null;
                summary.OrphanedItemsAdopted++;
                continue;
            }

            item.ParentId = null;
            item.SlotId = null;
            item.Location = null;
            summary.OrphanedItemsDetached++;
        }
    }

    private void RepackCartridgeStackSlots(ItemListContext context, ProfileRepairSummary summary)
    {
        var itemsById = context.Items.ToDictionary(item => item.Id.ToString(), item => item, StringComparer.OrdinalIgnoreCase);
        var cartridgeChildren = context
            .Items.Select((item, index) => new IndexedItem(item, index))
            .Where(indexed =>
                !string.IsNullOrWhiteSpace(indexed.Item.ParentId) && IsCartridgeSlot(indexed.Item.SlotId)
            )
            .GroupBy(indexed => $"{indexed.Item.ParentId}\u001f{indexed.Item.SlotId}", StringComparer.OrdinalIgnoreCase);

        foreach (var group in cartridgeChildren)
        {
            var first = group.First();
            if (!itemsById.TryGetValue(first.Item.ParentId!, out var parent) || !ParentHasStackSlot(parent, first.Item.SlotId))
            {
                continue;
            }

            var ordered = group
                .OrderBy(indexed => TryGetIntegerLocation(indexed.Item.Location, out var location) ? location : int.MaxValue)
                .ThenBy(indexed => indexed.Index)
                .ToList();

            for (var position = 0; position < ordered.Count; position++)
            {
                if (TryGetIntegerLocation(ordered[position].Item.Location, out var currentPosition) && currentPosition == position)
                {
                    continue;
                }

                ordered[position].Item.Location = position;
                summary.CartridgePositionsRepacked++;
            }
        }
    }

    private void RepairStackCountsAndTags(ItemListContext context, ProfileRepairSummary summary)
    {
        foreach (var item in context.Items)
        {
            var stackCountInvalid = item.Upd?.StackObjectsCount is null or <= 0;
            if (stackCountInvalid)
            {
                item.Upd ??= new Upd();
                item.Upd.StackObjectsCount = 1;
                summary.StackCountsFixed++;
            }

            var tagName = item.Upd?.Tag?.Name;
            if (string.IsNullOrEmpty(tagName) || !InvalidTagNameCharacters.IsMatch(tagName))
            {
                continue;
            }

            item.Upd!.Tag!.Name = InvalidTagNameCharacters.Replace(tagName, string.Empty);
            summary.TagsSanitized++;
        }
    }

    private void RemoveBrokenCharacterReferences(PmcData character, ProfileRepairSummary summary)
    {
        var inventory = character.Inventory;
        if (inventory?.Items is not { Count: > 0 } items)
        {
            return;
        }

        var validIds = items.Select(item => item.Id).ToHashSet();

        if (inventory.FastPanel is not null)
        {
            foreach (var key in inventory.FastPanel.Keys.ToList())
            {
                if (!validIds.Contains(inventory.FastPanel[key]))
                {
                    inventory.FastPanel.Remove(key);
                    summary.BrokenReferencesRemoved++;
                }
            }
        }

        if (inventory.FavoriteItems is not null)
        {
            var fixedFavorites = inventory.FavoriteItems.Where(validIds.Contains).Distinct().ToList();
            if (fixedFavorites.Count != inventory.FavoriteItems.Count())
            {
                inventory.FavoriteItems = fixedFavorites;
                summary.BrokenReferencesRemoved++;
            }
        }

        if (character.InsuredItems is not null)
        {
            var before = character.InsuredItems.Count;
            character.InsuredItems = character
                .InsuredItems.Where(insured => insured.ItemId.HasValue && validIds.Contains(insured.ItemId.Value))
                .ToList();
            summary.BrokenReferencesRemoved += before - character.InsuredItems.Count;
        }

        if (character.CheckedMagazines is not null)
        {
            foreach (var key in character.CheckedMagazines.Keys.ToList())
            {
                if (!MongoId.IsValidMongoId(key) || !validIds.Contains(new MongoId(key)))
                {
                    character.CheckedMagazines.Remove(key);
                    summary.BrokenReferencesRemoved++;
                }
            }
        }

        if (character.CheckedChambers is not null)
        {
            var fixedChambers = character
                .CheckedChambers.Where(id => MongoId.IsValidMongoId(id) && validIds.Contains(new MongoId(id)))
                .Distinct()
                .ToList();
            if (fixedChambers.Count != character.CheckedChambers.Count)
            {
                character.CheckedChambers = fixedChambers;
                summary.BrokenReferencesRemoved++;
            }
        }
    }

    private void RestoreInvalidCustomizations(PmcData character, ProfileRepairSummary summary)
    {
        if (character.Customization is null)
        {
            return;
        }

        var customizationDb = databaseService.GetTemplates().Customization;
        if (customizationDb is null || customizationDb.Count == 0)
        {
            return;
        }

        var playerIsUsec = string.Equals(character.Info?.Side, "usec", StringComparison.OrdinalIgnoreCase);

        if (
            RestoreCustomizationSlot(
                character.Customization.Head,
                value => character.Customization.Head = value,
                customizationDb,
                playerIsUsec ? "DefaultUsecHead" : "DefaultBearHead"
            )
        )
        {
            summary.CustomizationsRestored++;
        }

        if (
            RestoreCustomizationSlot(
                character.Customization.Body,
                value => character.Customization.Body = value,
                customizationDb,
                playerIsUsec ? "DefaultUsecBody" : "DefaultBearBody"
            )
        )
        {
            summary.CustomizationsRestored++;
        }

        if (
            RestoreCustomizationSlot(
                character.Customization.Hands,
                value => character.Customization.Hands = value,
                customizationDb,
                playerIsUsec ? "DefaultUsecHands" : "DefaultBearHands"
            )
        )
        {
            summary.CustomizationsRestored++;
        }

        if (
            RestoreCustomizationSlot(
                character.Customization.Feet,
                value => character.Customization.Feet = value,
                customizationDb,
                playerIsUsec ? "DefaultUsecFeet" : "DefaultBearFeet",
                playerIsUsec ? "DefaulUsecFeet" : "DefaultBearFeet"
            )
        )
        {
            summary.CustomizationsRestored++;
        }
    }

    private static bool RestoreCustomizationSlot(
        MongoId? currentValue,
        Action<MongoId> setValue,
        IReadOnlyDictionary<MongoId, CustomizationItem> customizationDb,
        params string[] defaultNames
    )
    {
        if (currentValue.HasValue && !currentValue.Value.IsEmpty && customizationDb.ContainsKey(currentValue.Value))
        {
            return false;
        }

        var defaultCustomization = defaultNames
            .Select(name => customizationDb.Values.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal)))
            .FirstOrDefault(item => item is not null);
        if (defaultCustomization is null)
        {
            return false;
        }

        setValue(defaultCustomization.Id);
        return true;
    }

    private bool ItemsAreSerializedEqual(Item first, Item second)
    {
        try
        {
            return jsonUtil.Serialize(first) == jsonUtil.Serialize(second);
        }
        catch (Exception ex)
        {
            logger.Warning($"[ProfileAutoRepair] failed comparing duplicate item json: {ex.Message}");
            return false;
        }
    }

    private bool ParentHasStackSlot(Item parent, string? slotId)
    {
        if (!databaseService.GetItems().TryGetValue(parent.Template, out var template))
        {
            return true;
        }

        return template.Properties?.StackSlots?.Any(slot => SlotMatches(slot, slotId)) == true
            || template.Properties?.Cartridges?.Any(slot => SlotMatches(slot, slotId)) == true
            || IsCartridgeSlot(slotId);
    }

    private static bool SlotMatches(StackSlot slot, string? slotId) =>
        string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(slot.Name, slotId, StringComparison.OrdinalIgnoreCase);

    private static bool SlotMatches(Slot slot, string? slotId) =>
        string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(slot.Name, slotId, StringComparison.OrdinalIgnoreCase);

    private static bool ItemHasChildren(IEnumerable<Item> items, MongoId itemId)
    {
        var id = itemId.ToString();
        return items.Any(item => string.Equals(item.ParentId, id, StringComparison.OrdinalIgnoreCase));
    }

    private static MongoId CreateUniqueId(HashSet<MongoId> usedIds)
    {
        MongoId newId;
        do
        {
            newId = new MongoId();
        } while (usedIds.Contains(newId));

        usedIds.Add(newId);
        return newId;
    }

    private static bool IsCartridgeSlot(string? slotId) =>
        slotId?.StartsWith("cartridges", StringComparison.OrdinalIgnoreCase) == true;

    private static string? ToRootId(MongoId? id) => id.HasValue && !id.Value.IsEmpty ? id.Value.ToString() : null;

    private static bool TryGetIntegerLocation(object? location, out int value)
    {
        switch (location)
        {
            case null:
                value = 0;
                return false;
            case int intValue:
                value = intValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                value = (int)longValue;
                return true;
            case double doubleValue when Math.Abs(doubleValue % 1) < double.Epsilon:
                value = (int)doubleValue;
                return true;
            case decimal decimalValue when decimalValue == Math.Truncate(decimalValue):
                value = (int)decimalValue;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                if (element.TryGetInt32(out value))
                {
                    return true;
                }

                if (element.TryGetDouble(out var elementDouble) && Math.Abs(elementDouble % 1) < double.Epsilon)
                {
                    value = (int)elementDouble;
                    return true;
                }

                break;
            case JsonElement { ValueKind: JsonValueKind.String } element
                when int.TryParse(element.GetString(), out var parsed):
                value = parsed;
                return true;
            case string stringValue when int.TryParse(stringValue, out var parsed):
                value = parsed;
                return true;
        }

        value = 0;
        return false;
    }

    private sealed record IndexedItem(Item Item, int Index);
}
