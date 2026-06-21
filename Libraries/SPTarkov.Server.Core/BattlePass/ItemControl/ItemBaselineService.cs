using System.Collections.Frozen;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>
///     原版 vs mod 物品识别：在 <see cref="OnLoadOrder.PostDBModLoader"/>（+0，早于走
///     CustomItemService 的物品 mod，它们用 +1/+2）快照当前物品 id 集合作为「原版基线」。
///     原版 DB 已于 <see cref="OnLoadOrder.Database"/>(200000) 导入完成，故此刻仅含原版物品。
///     之后任何不在基线中的 tpl 即视为 mod 添加。
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader)]
public class ItemBaselineService(DatabaseService databaseService, ISptLogger<ItemBaselineService> logger) : IOnLoad
{
    private FrozenSet<MongoId> _vanillaItemIds = FrozenSet<MongoId>.Empty;
    private bool _captured;

    public Task OnLoad()
    {
        try
        {
            _vanillaItemIds = databaseService.GetItems().Keys.ToFrozenSet();
            _captured = true;
            logger.Success($"[SPT-BattlePass] 物品基线已快照：原版物品 {_vanillaItemIds.Count} 项。");
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 物品基线快照失败: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>该 tpl 是否为 mod 添加（不在原版基线内）。基线未就绪时保守返回 false。</summary>
    public bool IsModItem(MongoId tpl)
    {
        return _captured && !_vanillaItemIds.Contains(tpl);
    }

    public int VanillaCount => _vanillaItemIds.Count;
}
