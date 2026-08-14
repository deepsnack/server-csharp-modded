using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>
///     物品获取途径「移除」的服务期屏蔽索引（最小破坏：屏蔽而非删除）。
///     从 <c>item-overrides.json</c> 的 remove 条目构建内存索引，供各 serve 链路在返回克隆/生成结果时即时过滤，
///     从而无需破坏式改动内存 DB（商人 assort / 任务奖励 / 藏身处配方 / 初始库存）。
///     索引仅在 <see cref="Rebuild"/>（启动 + 后台编辑后由 <see cref="ItemControlSync.Sync"/> 调用）时重建，
///     热路径查询纯内存、零磁盘 IO。loot 类移除仍由 <see cref="ItemControlSync"/> 的非破坏 transformer 处理。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ItemAcquisitionMaskService(ISptLogger<ItemAcquisitionMaskService> logger) : IItemAcquisitionMaskService
{
    private Dictionary<MongoId, HashSet<MongoId>> _trader = new(); // traderId -> 被移除 tpl
    private Dictionary<string, HashSet<MongoId>> _quest = new(); // "questId:group" -> 被移除 tpl
    private HashSet<MongoId> _recipeOutput = new(); // 被移除的产物 tpl（整条配方屏蔽）
    private Dictionary<string, HashSet<MongoId>> _ingredient = new(); // recipeId("" = 全部配方) -> 被移除原料 tpl
    private Dictionary<string, HashSet<MongoId>> _startInv = new(); // side(小写, 含 "both") -> 被移除 tpl
    private volatile bool _any;

    /// <summary>是否存在任一服务期移除项（无则各 serve 链路可零开销跳过过滤）。</summary>
    public bool HasAny => _any;

    /// <summary>从 override 存储重建索引（仅在启动 / 后台编辑后调用，非热路径）。</summary>
    public void Rebuild()
    {
        var trader = new Dictionary<MongoId, HashSet<MongoId>>();
        var quest = new Dictionary<string, HashSet<MongoId>>();
        var recipeOutput = new HashSet<MongoId>();
        var ingredient = new Dictionary<string, HashSet<MongoId>>();
        var startInv = new Dictionary<string, HashSet<MongoId>>();

        try
        {
            foreach (var ov in BattlePassStore.GetItemOverrides())
            {
                if (ov.Op != "remove" || !MongoIdEx.TryParse(ov.Tpl, out var tpl))
                {
                    continue;
                }

                switch (ov.Source)
                {
                    case AcqSource.Trader:
                        if (MongoIdEx.TryParse(ov.TraderId, out var tid))
                        {
                            Index(trader, tid, tpl);
                        }

                        break;
                    case AcqSource.Quest:
                        if (MongoIdEx.TryParse(ov.QuestId, out var qid))
                        {
                            IndexStr(quest, $"{qid}:{ov.RewardGroup}", tpl);
                        }

                        break;
                    case AcqSource.Hideout:
                        if (ov.Role == "ingredient")
                        {
                            IndexStr(ingredient, ov.RecipeId ?? "", tpl);
                        }
                        else
                        {
                            recipeOutput.Add(tpl);
                        }

                        break;
                    case AcqSource.StartInv:
                        IndexStr(startInv, (ov.Side ?? "both").ToLowerInvariant(), tpl);
                        break;
                    // loot：由 ItemControlSync 的 transformer 非破坏处理，不入此索引
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 服务期移除索引重建失败: {ex.Message}");
        }

        _trader = trader;
        _quest = quest;
        _recipeOutput = recipeOutput;
        _ingredient = ingredient;
        _startInv = startInv;
        _any = trader.Count > 0 || quest.Count > 0 || recipeOutput.Count > 0 || ingredient.Count > 0 || startInv.Count > 0;
    }

    public bool IsTraderItemRemoved(MongoId traderId, MongoId tpl)
    {
        return _trader.TryGetValue(traderId, out var set) && set.Contains(tpl);
    }

    public bool IsQuestRewardRemoved(MongoId questId, string rewardGroup, MongoId tpl)
    {
        return _quest.TryGetValue($"{questId}:{rewardGroup}", out var set) && set.Contains(tpl);
    }

    public bool IsRecipeOutputRemoved(MongoId endProduct)
    {
        return _recipeOutput.Contains(endProduct);
    }

    public bool IsIngredientRemoved(string? recipeId, MongoId tpl)
    {
        if (_ingredient.TryGetValue("", out var global) && global.Contains(tpl))
        {
            return true;
        }

        return recipeId is not null && _ingredient.TryGetValue(recipeId, out var set) && set.Contains(tpl);
    }

    public bool IsStartInvRemoved(string? side, MongoId tpl)
    {
        if (_startInv.TryGetValue("both", out var both) && both.Contains(tpl))
        {
            return true;
        }

        var key = (side ?? string.Empty).ToLowerInvariant();
        return key.Length > 0 && _startInv.TryGetValue(key, out var set) && set.Contains(tpl);
    }

    private static void Index(Dictionary<MongoId, HashSet<MongoId>> map, MongoId key, MongoId tpl)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<MongoId>();
            map[key] = set;
        }

        set.Add(tpl);
    }

    private static void IndexStr(Dictionary<string, HashSet<MongoId>> map, string key, MongoId tpl)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<MongoId>();
            map[key] = set;
        }

        set.Add(tpl);
    }
}
