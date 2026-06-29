using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     从玩家存档仓库(Stash)按 tpl 清点/扣减物品的共享原语，供网页上交(<see cref="BattlePassHandoverService"/>)
///     与网页商店(<see cref="BattlePassShopService"/>)复用，保证两处的「够不够 / 怎么扣」判定完全一致。
///     <para>安全：只取仓库内、跳过带子物品的容器（避免连带删除嵌套战利品），支持堆叠部分扣减。</para>
/// </summary>
[Injectable]
public class BattlePassStashService(InventoryHelper inventoryHelper)
{
    /// <summary>仓库内可用于支付/上交的某 tpl 总数量（含堆叠）。<paramref name="requireFir"/>=true 时只数战局内找到的。</summary>
    public int CountTpl(PmcData pmc, string tpl, bool requireFir)
    {
        return CollectStashCandidates(pmc, tpl, requireFir).Sum(StackOf);
    }

    /// <summary>
    ///     从仓库移除至多 <paramref name="count"/> 个 <paramref name="tpl"/>（整堆走 <see cref="InventoryHelper.RemoveItem"/>，
    ///     不足整堆则部分扣减堆叠数）。返回实际移除数量。<b>不</b>负责落盘，调用方在成功后保存档案。
    /// </summary>
    public int RemoveTpl(PmcData pmc, MongoId sessionId, string tpl, int count, bool requireFir)
    {
        if (count <= 0)
        {
            return 0;
        }

        var removed = 0;
        // 每次重新取候选：移除会改动 Items 列表
        foreach (var item in CollectStashCandidates(pmc, tpl, requireFir).ToList())
        {
            if (removed >= count)
            {
                break;
            }

            var stack = StackOf(item);
            var take = Math.Min(stack, count - removed);
            if (take >= stack)
            {
                inventoryHelper.RemoveItem(pmc, item.Id, sessionId);
            }
            else
            {
                item.Upd!.StackObjectsCount = stack - take; // 部分扣减堆叠
            }

            removed += take;
        }

        return removed;
    }

    /// <summary>仓库内、匹配 tpl、（按需）FiR、且无子物品（非容器）的可取候选。</summary>
    public static List<Item> CollectStashCandidates(PmcData pmc, string tpl, bool requireFir)
    {
        var items = pmc.Inventory?.Items;
        var stashId = pmc.Inventory?.Stash?.ToString();
        if (items is null || string.IsNullOrEmpty(stashId))
        {
            return [];
        }

        var byId = items.ToDictionary(i => i.Id.ToString(), StringComparer.Ordinal);
        var parents = items.Where(i => i.ParentId is not null).Select(i => i.ParentId!).ToHashSet(StringComparer.Ordinal);

        return items.Where(i =>
                string.Equals(i.Template.ToString(), tpl, StringComparison.OrdinalIgnoreCase)
                && !parents.Contains(i.Id.ToString()) // 跳过带子物品的容器，避免连带删除嵌套战利品
                && (!requireFir || i.Upd?.SpawnedInSession == true)
                && IsInStash(i, byId, stashId))
            .ToList();
    }

    private static bool IsInStash(Item item, Dictionary<string, Item> byId, string stashId)
    {
        var cursor = item;
        var guard = 0;
        while (cursor is not null && guard++ < 64)
        {
            if (string.IsNullOrEmpty(cursor.ParentId))
            {
                return false;
            }

            if (string.Equals(cursor.ParentId, stashId, StringComparison.Ordinal))
            {
                return true;
            }

            byId.TryGetValue(cursor.ParentId, out cursor);
        }

        return false;
    }

    public static int StackOf(Item item)
    {
        return Math.Max(1, (int)Math.Floor(item.Upd?.StackObjectsCount ?? 1));
    }
}
