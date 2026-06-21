using System.Security.Cryptography;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.ItemControl;

/// <summary>
///     物品获取途径编辑的应用器：把 <c>item-overrides.json</c> 重放到内存 DB。
///     结构类（商人/任务/藏身处/初始库存）直接幂等改集合；战利品类对各 Location 的 LazyLoad
///     注册一次 transformer（内部实时读 override → 运行时编辑即时反映、lazy 安全）。
///     OnLoad 于 +90000（晚于物品 mod、早于 TraderRegistration）；后台保存后可运行时再 <see cref="Sync"/>。
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 90000)]
public class ItemControlSync(
    DatabaseService databaseService,
    ItemAcquisitionMaskService maskService,
    ISptLogger<ItemControlSync> logger
) : IOnLoad
{
    private bool _lootHooked;

    public Task OnLoad()
    {
        Sync();
        return Task.CompletedTask;
    }

    /// <summary>应用全部 override（幂等）。结构类即时改 DB；战利品类靠已注册的 transformer 实时生效。</summary>
    public void Sync()
    {
        try
        {
            var overrides = BattlePassStore.GetItemOverrides();
            HookLootTransformers(); // 仅首次真正注册

            // 最小破坏原则：仅 add 类直接注入内存 DB（无中生有，无法服务期实现）；
            // remove 类一律改走服务期屏蔽（见 ItemAcquisitionMaskService），不再破坏式删 DB。
            foreach (var ov in overrides)
            {
                if (ov.Op != "add" || !MongoIdEx.TryParse(ov.Tpl, out var tpl))
                {
                    continue;
                }

                switch (ov.Source)
                {
                    case AcqSource.Trader:
                        ApplyTrader(ov, tpl);
                        break;
                    case AcqSource.Quest:
                        ApplyQuest(ov, tpl);
                        break;
                    case AcqSource.Hideout:
                        ApplyHideout(ov, tpl);
                        break;
                    // startInv 仅支持 remove（服务期屏蔽）；loot 由 transformer 处理
                }
            }

            // remove 索引重建：商人/任务/藏身处/初始库存的移除在各 serve 链路即时生效（克隆后过滤，不动 DB）
            maskService.Rebuild();

            logger.Success($"[SPT-BattlePass] 物品获取 override 已应用（共 {overrides.Count} 条；remove 走服务期屏蔽）。");
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 物品获取 override 应用失败: {ex.Message}");
        }
    }

    // ---- 商人 ----
    private void ApplyTrader(BpItemOverride ov, MongoId tpl)
    {
        if (string.IsNullOrWhiteSpace(ov.TraderId) || !MongoIdEx.TryParse(ov.TraderId, out var traderId))
        {
            return;
        }

        if (!databaseService.GetTables().Traders.TryGetValue(traderId, out var trader) || trader.Assort is null)
        {
            return;
        }

        var assort = trader.Assort;
        assort.Items ??= new List<Item>();

        // 仅处理 add（remove 改走服务期屏蔽，见 ItemAcquisitionMaskService）
        {
            var rootId = DeterministicId($"{ov.TraderId}:{ov.Tpl}", "bp-itemctrl-trader");
            if (assort.Items.Any(i => i.Id == rootId))
            {
                return; // 幂等
            }

            assort.Items.Add(new Item
            {
                Id = rootId,
                Template = tpl,
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd { StackObjectsCount = 999_999, UnlimitedCount = true },
            });

            var scheme = new List<BarterScheme>();
            foreach (var c in ov.Cost ?? new List<BpItemCost>())
            {
                if (MongoIdEx.TryParse(c.Tpl, out var ctpl) && c.Count > 0)
                {
                    scheme.Add(new BarterScheme { Template = ctpl, Count = c.Count });
                }
            }

            assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
            assort.BarterScheme[rootId] = new List<List<BarterScheme>> { scheme };
            assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();
            assort.LoyalLevelItems[rootId] = Math.Max(1, ov.Loyalty);
        }
    }

    // ---- 任务奖励 ----
    private void ApplyQuest(BpItemOverride ov, MongoId tpl)
    {
        if (string.IsNullOrWhiteSpace(ov.QuestId) || !MongoIdEx.TryParse(ov.QuestId, out var questId))
        {
            return;
        }

        if (!databaseService.GetQuests().TryGetValue(questId, out var quest) || quest.Rewards is null)
        {
            return;
        }

        if (!quest.Rewards.TryGetValue(ov.RewardGroup, out var rewards))
        {
            rewards = new List<Reward>();
            quest.Rewards[ov.RewardGroup] = rewards;
        }

        // 仅处理 add（remove 改走服务期屏蔽，见 ItemAcquisitionMaskService）
        {
            var rewardId = DeterministicId($"{ov.QuestId}:{ov.RewardGroup}:{ov.Tpl}", "bp-itemctrl-quest");
            if (rewards.Any(r => r.Id == rewardId))
            {
                return; // 幂等
            }

            var itemId = DeterministicId($"{rewardId}:item", "bp-itemctrl-qitem");
            rewards.Add(new Reward
            {
                Id = rewardId,
                Type = RewardType.Item,
                Index = rewards.Count,
                Value = ov.Count,
                FindInRaid = false,
                Items = new List<Item>
                {
                    new()
                    {
                        Id = itemId,
                        Template = tpl,
                        ParentId = null,
                        Upd = new Upd { StackObjectsCount = Math.Max(1, ov.Count) },
                    },
                },
            });
        }
    }

    // ---- 藏身处制造 ----
    private void ApplyHideout(BpItemOverride ov, MongoId tpl)
    {
        var recipes = databaseService.GetHideout().Production.Recipes;
        if (recipes is null)
        {
            return;
        }

        // 仅处理 add（remove 改走服务期屏蔽，见 ItemAcquisitionMaskService）
        {
            if (ov.Role == "ingredient" && ov.RecipeId is not null)
            {
                var recipe = recipes.FirstOrDefault(r => r.Id.ToString() == ov.RecipeId);
                if (recipe is not null)
                {
                    recipe.Requirements ??= new List<Requirement>();
                    if (recipe.Requirements.All(rq => rq.TemplateId != tpl))
                    {
                        recipe.Requirements.Add(new Requirement
                        {
                            TemplateId = tpl,
                            Count = Math.Max(1, ov.Count),
                            Type = "Item",
                        });
                    }
                }
            }
            else // output：新建一个简易配方
            {
                var recipeId = DeterministicId($"{ov.Tpl}", "bp-itemctrl-recipe");
                if (recipes.All(r => r.Id != recipeId))
                {
                    var reqs = new List<Requirement>();
                    foreach (var c in ov.Cost ?? new List<BpItemCost>())
                    {
                        if (MongoIdEx.TryParse(c.Tpl, out var ctpl) && c.Count > 0)
                        {
                            reqs.Add(new Requirement { TemplateId = ctpl, Count = c.Count, Type = "Item" });
                        }
                    }

                    recipes.Add(new HideoutProduction
                    {
                        Id = recipeId,
                        AreaType = HideoutAreas.Workbench,
                        EndProduct = tpl,
                        Count = Math.Max(1, ov.Count),
                        ProductionTime = 3600,
                        Requirements = reqs,
                        Locked = false,
                        Continuous = false,
                        NeedFuelForAllProductionTime = false,
                    });
                }
            }
        }
    }

    // 初始库存移除改走服务期屏蔽（CreateProfileService 克隆模板后过滤，见 ItemAcquisitionMaskService.IsStartInvRemoved）；
    // add 涉及 stash 网格摆放，v1 不支持（前端禁用）。

    // ---- 战利品 transformer（仅注册一次，内部实时读 override） ----
    private void HookLootTransformers()
    {
        if (_lootHooked)
        {
            return;
        }

        foreach (var (name, loc) in databaseService.GetLocations().GetDictionary())
        {
            if (loc is null)
            {
                continue;
            }

            var locId = name;

            loc.StaticLoot?.AddTransformer(dict =>
            {
                if (dict is null)
                {
                    return dict;
                }

                foreach (var ov in LootOverridesFor(locId))
                {
                    if (!MongoIdEx.TryParse(ov.Tpl, out var tpl))
                    {
                        continue;
                    }

                    foreach (var (containerTpl, details) in dict)
                    {
                        if (ov.ContainerOrSpawn is not null && containerTpl.ToString() != ov.ContainerOrSpawn)
                        {
                            continue;
                        }

                        if (ov.Op == "remove" && details.ItemDistribution is not null)
                        {
                            details.ItemDistribution = details.ItemDistribution.Where(d => d.Tpl != tpl).ToList();
                        }
                        else if (ov.Op == "add" && ov.LootKind == "static")
                        {
                            var list = (details.ItemDistribution ?? Enumerable.Empty<ItemDistribution>()).ToList();
                            if (list.All(d => d.Tpl != tpl))
                            {
                                list.Add(new ItemDistribution { Tpl = tpl, RelativeProbability = Math.Max(1, ov.Weight) });
                                details.ItemDistribution = list;
                            }
                        }
                    }
                }

                return dict;
            });

            loc.LooseLoot?.AddTransformer(loose =>
            {
                if (loose is null)
                {
                    return loose;
                }

                foreach (var ov in LootOverridesFor(locId).Where(o => o.Op == "remove"))
                {
                    if (!MongoIdEx.TryParse(ov.Tpl, out var tpl))
                    {
                        continue;
                    }

                    // 精确移除：只剔除该 tpl 的物品条目，保留同点位其它物品；
                    // 点位被掏空才整体丢弃。镜像 LocationLootGenerator 的 validComposedKeys 过滤逻辑。
                    loose.Spawnpoints = FilterLooseSpawnpoints(loose.Spawnpoints, tpl);
                    loose.SpawnpointsForced = FilterLooseSpawnpoints(loose.SpawnpointsForced, tpl);
                }

                return loose;
            });

            loc.StaticContainers?.AddTransformer(sc =>
            {
                if (sc?.StaticForced is null)
                {
                    return sc;
                }

                foreach (var ov in LootOverridesFor(locId).Where(o => o.Op == "remove"))
                {
                    if (MongoIdEx.TryParse(ov.Tpl, out var tpl))
                    {
                        sc.StaticForced = sc.StaticForced.Where(f => f.ItemTpl != tpl).ToList();
                    }
                }

                return sc;
            });
        }

        _lootHooked = true;
    }

    private static IEnumerable<BpItemOverride> LootOverridesFor(string locationId)
    {
        return BattlePassStore.GetItemOverrides()
            .Where(o => o.Source == AcqSource.Loot
                        && string.Equals(o.LocationId, locationId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     从散落点集合中精确剔除目标 tpl：仅删除匹配的物品条目，并同步裁剪 itemDistribution 中失效的
    ///     composedKey；点位无任何物品残留时才整体丢弃。不误伤同点位的其它物品（修正旧版整点删除的粗粒度）。
    /// </summary>
    private static IEnumerable<Spawnpoint>? FilterLooseSpawnpoints(IEnumerable<Spawnpoint>? points, MongoId tpl)
    {
        if (points is null)
        {
            return points;
        }

        var result = new List<Spawnpoint>();
        foreach (var sp in points)
        {
            var items = sp.Template?.Items?.ToList();
            if (items is null || items.All(i => i.Template != tpl))
            {
                result.Add(sp); // 该点不含目标物品，原样保留
                continue;
            }

            var kept = items.Where(i => i.Template != tpl).ToList();
            if (kept.Count == 0)
            {
                continue; // 掏空 → 丢弃整点
            }

            sp.Template!.Items = kept;

            // 仅保留仍存在于 Items 的 composedKey（与 LocationLootGenerator 的 validComposedKeys 一致）
            if (sp.ItemDistribution is not null)
            {
                var validKeys = kept.Select(i => i.ComposedKey).Where(k => k is not null).ToHashSet();
                sp.ItemDistribution = sp.ItemDistribution
                    .Where(d => d.ComposedKey?.Key is null || validKeys.Contains(d.ComposedKey.Key))
                    .ToList();
            }

            result.Add(sp);
        }

        return result;
    }

    private static MongoId DeterministicId(string seed, string salt)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + seed));
        return new MongoId(Convert.ToHexString(bytes).ToLowerInvariant()[..24]);
    }
}
