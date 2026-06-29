using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils.Cloners;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     从裸 tpl 构建「默认完整形态」物品树，供商人货架（物品管控 add）与网页商店/奖励发货复用：
///     枪械 = 全局默认改装预设（完整转播台，而非仅一个枪机）；防弹衣/头盔 = 带默认插板与内衬的预设
///     （无预设则补必需子槽）；其余物品 = 单件。避免发出空枪、无插板的废甲废盔。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BattlePassItemBuilder(ItemHelper itemHelper, PresetHelper presetHelper, ICloner cloner)
{
    /// <summary>
    ///     构建 root 在首位的物品列表：root.Id = <paramref name="rootId"/>、堆叠 = <paramref name="count"/>，
    ///     子件已正确 parent 到 root。调用方按落点设置 root 的 ParentId/SlotId（商人货架=hideout；邮件=发信服务自行重整）。
    /// </summary>
    public List<Item> Build(MongoId tpl, int count, MongoId rootId)
    {
        // 枪械 / 有默认预设的装备：用全局默认预设组装（含枪机外配件、甲盔插板内衬）
        var preset = presetHelper.GetDefaultPreset(tpl);
        if (preset?.Items is { Count: > 1 })
        {
            var items = cloner.Clone(preset.Items).ReplaceIDs().ToList();
            items.RemapRootItemId(rootId);
            var root = items.First(i => i.Id == rootId);
            root.AddUpd();
            root.Upd!.StackObjectsCount = count;
            return items;
        }

        var rootItem = new Item { Id = rootId, Template = tpl, Upd = new Upd { StackObjectsCount = count } };

        // 无预设但可装插板/内衬的甲盔：补必需子槽（与 RewardHelper.GenerateArmorRewardChildSlots 同策略，避免裸甲）
        if (itemHelper.ArmorItemCanHoldMods(tpl)
            && itemHelper.ItemHasSlots(tpl)
            && itemHelper.GetItem(tpl) is { Key: true, Value: { } tpd })
        {
            return itemHelper.AddChildSlotItems([rootItem], tpd, null, true);
        }

        return [rootItem];
    }

    /// <summary>
    ///     为邮件奖励构建物品。与商人货架不同，邮件奖励必须遵守模板堆叠上限：
    ///     不可堆叠物品发多个时会生成多个独立根物品，可堆叠物品超过上限时拆成多组堆叠。
    /// </summary>
    public List<Item> BuildRewardStacks(MongoId tpl, int count)
    {
        var result = new List<Item>();
        foreach (var stackCount in RewardStackCounts(tpl, count))
        {
            result.AddRange(Build(tpl, stackCount, new MongoId()));
        }

        return result;
    }

    private IEnumerable<int> RewardStackCounts(MongoId tpl, int count)
    {
        var remaining = Math.Max(1, count);
        var maxStackSize = Math.Max(1, itemHelper.GetItem(tpl).Value?.Properties?.StackMaxSize ?? 1);

        while (remaining > 0)
        {
            var stackCount = Math.Min(remaining, maxStackSize);
            yield return stackCount;
            remaining -= stackCount;
        }
    }
}
