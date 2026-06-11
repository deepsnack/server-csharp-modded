using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>物品获取图谱：聚合某 tpl 的全部获取途径（商人/任务+路径/藏身处/初始库存/战利品）。</summary>
[Injectable]
public class ItemGraphService(
    DatabaseService databaseService,
    ItemHelper itemHelper,
    ItemBaselineService baseline,
    LootIndexService lootIndex,
    ConfigServer configServer
)
{
    private const int QuestPathDepth = 4;

    public BpAcquisitionGraph? GetAcquisitions(string tplStr)
    {
        if (!MongoIdEx.TryParse(tplStr, out var tpl))
        {
            return null;
        }

        var items = databaseService.GetItems();
        if (!items.TryGetValue(tpl, out var item))
        {
            return null;
        }

        var entries = new List<BpAcquisitionEntry>();
        var rewardQuestIds = new List<string>();

        // ---- 商人货架 ----
        foreach (var (traderId, trader) in databaseService.GetTables().Traders)
        {
            var assortItems = trader.Assort?.Items;
            if (assortItems is null)
            {
                continue;
            }

            foreach (var ai in assortItems.Where(i => i.Template == tpl))
            {
                var loyalty = trader.Assort?.LoyalLevelItems is not null
                              && trader.Assort.LoyalLevelItems.TryGetValue(ai.Id, out var ll)
                    ? ll
                    : 0;
                entries.Add(new BpAcquisitionEntry
                {
                    Source = AcqSource.Trader,
                    Label = $"商人 {trader.Base?.Nickname ?? traderId.ToString()}" + (loyalty > 0 ? $" LL{loyalty}" : ""),
                    Ref = new() { ["traderId"] = traderId.ToString(), ["assortItemId"] = ai.Id.ToString() },
                });
            }
        }

        // ---- 任务奖励 ----
        var quests = databaseService.GetQuests();
        foreach (var (questId, quest) in quests)
        {
            if (quest.Rewards is null)
            {
                continue;
            }

            foreach (var (group, rewards) in quest.Rewards)
            {
                foreach (var rw in rewards)
                {
                    if (rw.Type == RewardType.Item && (rw.Items?.Any(it => it.Template == tpl) ?? false))
                    {
                        entries.Add(new BpAcquisitionEntry
                        {
                            Source = AcqSource.Quest,
                            Label = $"任务「{quest.QuestName ?? quest.Name ?? questId.ToString()}」{group} 奖励",
                            Ref = new() { ["questId"] = questId.ToString(), ["rewardGroup"] = group },
                        });
                        rewardQuestIds.Add(questId.ToString());
                    }
                }
            }
        }

        // ---- 藏身处制造 ----
        foreach (var recipe in databaseService.GetHideout().Production.Recipes ?? new List<HideoutProduction>())
        {
            if (recipe.EndProduct == tpl)
            {
                entries.Add(new BpAcquisitionEntry
                {
                    Source = AcqSource.Hideout,
                    Label = $"藏身处制造产物（配方 {recipe.Id}）",
                    Ref = new() { ["recipeId"] = recipe.Id.ToString(), ["role"] = "output" },
                });
            }

            if (recipe.Requirements?.Any(r => r.TemplateId == tpl) ?? false)
            {
                entries.Add(new BpAcquisitionEntry
                {
                    Source = AcqSource.Hideout,
                    Label = $"藏身处配方 {recipe.Id} 的原料",
                    Ref = new() { ["recipeId"] = recipe.Id.ToString(), ["role"] = "ingredient" },
                });
            }
        }

        // ---- 初始库存 ----
        foreach (var (profileType, sides) in databaseService.GetTables().Templates?.Profiles
                 ?? new Dictionary<string, ProfileSides>())
        {
            foreach (var (sideName, side) in new[] { ("Usec", sides.Usec), ("Bear", sides.Bear) })
            {
                var inv = side?.Character?.Inventory?.Items;
                if (inv?.Any(i => i.Template == tpl) ?? false)
                {
                    entries.Add(new BpAcquisitionEntry
                    {
                        Source = AcqSource.StartInv,
                        Label = $"初始库存（{profileType} / {sideName}）",
                        Ref = new() { ["profileType"] = profileType, ["side"] = sideName },
                    });
                }
            }
        }

        // ---- 战利品（索引 O(1)） ----
        foreach (var ls in lootIndex.GetLootSources(tpl))
        {
            entries.Add(new BpAcquisitionEntry
            {
                Source = AcqSource.Loot,
                Label = $"战利品：{ls.LocationId}（{ls.Kind}）",
                Ref = new() { ["locationId"] = ls.LocationId, ["lootKind"] = ls.Kind, ["containerOrSpawn"] = ls.ContainerOrSpawn },
            });
        }

        // 标记由本 mod add override 引入的途径
        var addOverrides = BattlePassStore.GetItemOverrides()
            .Where(o => o.Op == "add" && o.Tpl == tplStr).ToList();

        // ---- 任务前置路径 ----
        var questPaths = BuildQuestPaths(rewardQuestIds.Distinct().ToList(), quests);

        // ---- flea 状态 ----
        var ragfair = configServer.GetConfig<RagfairConfig>();
        var fleaCtl = BattlePassStore.GetFleaControl();
        var blacklisted = ragfair.Dynamic.Blacklist.Custom.Contains(tpl) || fleaCtl.BlacklistTpls.Contains(tplStr);

        return new BpAcquisitionGraph
        {
            Tpl = tplStr,
            Name = itemHelper.GetItemName(tpl) ?? "",
            Parent = item.Parent.ToString(),
            IsMod = baseline.IsModItem(tpl),
            CanSellOnRagfair = item.Properties?.CanSellOnRagfair ?? false,
            FleaBlacklisted = blacklisted,
            Entries = entries,
            QuestPaths = questPaths,
        };
    }

    /// <summary>对一组任务广度展开前置链（ConditionType=="Quest" 的 AvailableForStart），去重 + 深度上限。</summary>
    private List<BpQuestPathNode> BuildQuestPaths(List<string> startQuestIds, Dictionary<MongoId, Quest> quests)
    {
        var nodes = new List<BpQuestPathNode>();
        var seen = new HashSet<string>();
        var frontier = new Queue<(string id, int depth)>();
        foreach (var id in startQuestIds)
        {
            frontier.Enqueue((id, 0));
        }

        while (frontier.Count > 0)
        {
            var (id, depth) = frontier.Dequeue();
            if (!seen.Add(id) || depth > QuestPathDepth || !MongoIdEx.TryParse(id, out var qid) || !quests.TryGetValue(qid, out var quest))
            {
                continue;
            }

            var prereqs = new List<string>();
            foreach (var cond in quest.Conditions?.AvailableForStart ?? new List<QuestCondition>())
            {
                if (!string.Equals(cond.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase) || cond.Target is null)
                {
                    continue;
                }

                var targets = cond.Target.IsList ? cond.Target.List! : new List<string> { cond.Target.Item! };
                foreach (var t in targets.Where(x => !string.IsNullOrEmpty(x)))
                {
                    prereqs.Add(t);
                    frontier.Enqueue((t, depth + 1));
                }
            }

            nodes.Add(new BpQuestPathNode
            {
                QuestId = id,
                Name = quest.QuestName ?? quest.Name ?? id,
                Prerequisites = prereqs,
            });
        }

        return nodes;
    }
}
