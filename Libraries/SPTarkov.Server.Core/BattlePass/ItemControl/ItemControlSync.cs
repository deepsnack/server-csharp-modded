using System.Security.Cryptography;
using System.Text;
using SPTarkov.Server.Core.BattlePass.Administration;
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
    BattlePassItemBuilder itemBuilder,
    ISptLogger<ItemControlSync> logger
) : IOnLoad
{
    private bool _lootHooked;

    // 已注入项追踪：单例存活，进程内记录每条 add override 注入了哪些 DB 条目，
    // 以便撤销（override 被删）时即时从内存 DB 移除——无需重启。重启后字段为空、DB 也是干净的，重放自然对齐。
    private readonly Dictionary<MongoId, HashSet<MongoId>> _injTrader = new(); // traderId -> 注入的货架 rootId
    private readonly Dictionary<MongoId, HashSet<MongoId>> _injQuest = new(); // questId -> 注入的 rewardId
    private readonly HashSet<MongoId> _injRecipe = new(); // 注入的 output 配方 id
    private readonly Dictionary<MongoId, HashSet<MongoId>> _injIngredient = new(); // recipeId -> 注入的原料 tpl

    public Task OnLoad()
    {
        Sync();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     全量对账式应用 add override（持久且可即时撤销）：以当前 override 为唯一真源，
    ///     注入缺失项、移除已撤销（override 已删）的注入项——撤销立即从内存 DB 消失，无需重启。
    ///     remove 类走服务期屏蔽（<see cref="ItemAcquisitionMaskService"/>）；战利品类靠 transformer 实时生效。
    /// </summary>
    public void Sync()
    {
        try
        {
            var overrides = BattlePassStore.GetItemOverrides();
            HookLootTransformers(); // 仅首次真正注册

            var adds = overrides.Where(o => o.Op == "add").ToList();
            ReconcileTraders(adds);
            ReconcileQuests(adds);
            ReconcileHideout(adds);

            // remove 索引重建：商人/任务/藏身处/初始库存的移除在各 serve 链路即时生效（克隆后过滤，不动 DB）
            maskService.Rebuild();

            logger.Success($"[SPT-BattlePass] 物品获取 override 已对账应用（共 {overrides.Count} 条；add 注入/撤销即时，remove 走服务期屏蔽）。");
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 物品获取 override 应用失败: {ex.Message}");
        }
    }

    // ---- 商人：对账 ----
    private void ReconcileTraders(List<BpItemOverride> adds)
    {
        // 期望态：traderId -> { rootId -> override }
        var desired = new Dictionary<MongoId, Dictionary<MongoId, BpItemOverride>>();
        foreach (var ov in adds.Where(o => o.Source == AcqSource.Trader))
        {
            if (!MongoIdEx.TryParse(ov.Tpl, out _) || !MongoIdEx.TryParse(ov.TraderId, out var traderId))
            {
                continue;
            }

            var rootId = BattlePassSnapshotCodec.DeterministicId($"{ov.TraderId}:{ov.Tpl}", "bp-itemctrl-trader");
            (desired.TryGetValue(traderId, out var map) ? map : desired[traderId] = new())[rootId] = ov;
        }

        foreach (var traderId in _injTrader.Keys.Concat(desired.Keys).Distinct().ToList())
        {
            if (!databaseService.GetTables().Traders.TryGetValue(traderId, out var trader) || trader.Assort is null)
            {
                continue;
            }

            var assort = trader.Assort;
            assort.Items ??= new List<Item>();
            var want = desired.TryGetValue(traderId, out var m) ? m : new Dictionary<MongoId, BpItemOverride>();
            var have = _injTrader.TryGetValue(traderId, out var h) ? h : new HashSet<MongoId>();

            // 撤销：移除已不在期望态的注入货架（含子件/价格/忠诚）
            foreach (var stale in have.Where(id => !want.ContainsKey(id)).ToList())
            {
                RemoveTraderRoot(assort, stale);
            }

            // 注入缺失
            foreach (var (rootId, ov) in want)
            {
                if (MongoIdEx.TryParse(ov.Tpl, out var tpl) && assort.Items.All(i => i.Id != rootId))
                {
                    InjectTraderItem(assort, ov, tpl, rootId);
                }
            }

            _injTrader[traderId] = new HashSet<MongoId>(want.Keys);
        }
    }

    private void InjectTraderItem(TraderAssort assort, BpItemOverride ov, MongoId tpl, MongoId rootId)
    {
        // 枪/甲/盔按默认完整形态上架（枪=默认改装预设，甲盔=带插板内衬），其余物品为单件。
        var built = itemBuilder.Build(tpl, 1, rootId);
        var root = built[0];
        root.ParentId = "hideout";
        root.SlotId = "hideout";
        root.Upd = new Upd { StackObjectsCount = 999_999, UnlimitedCount = true };
        assort.Items!.AddRange(built);

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

    /// <summary>移除一条注入货架：连带其全部子件（预设/插板等），并清理价格与忠诚条目。</summary>
    private static void RemoveTraderRoot(TraderAssort assort, MongoId rootId)
    {
        if (assort.Items is not null)
        {
            var removeIds = new HashSet<string> { rootId.ToString() };
            bool grew;
            do
            {
                grew = false;
                foreach (var it in assort.Items)
                {
                    if (it.ParentId is not null && removeIds.Contains(it.ParentId) && removeIds.Add(it.Id.ToString()))
                    {
                        grew = true;
                    }
                }
            } while (grew);

            assort.Items.RemoveAll(i => removeIds.Contains(i.Id.ToString()));
        }

        assort.BarterScheme?.Remove(rootId);
        assort.LoyalLevelItems?.Remove(rootId);
    }

    // ---- 任务奖励：对账 ----
    private void ReconcileQuests(List<BpItemOverride> adds)
    {
        // 期望态：questId -> { rewardId -> (override, tpl) }
        var desired = new Dictionary<MongoId, Dictionary<MongoId, (BpItemOverride ov, MongoId tpl)>>();
        foreach (var ov in adds.Where(o => o.Source == AcqSource.Quest))
        {
            if (!MongoIdEx.TryParse(ov.Tpl, out var tpl) || !MongoIdEx.TryParse(ov.QuestId, out var questId))
            {
                continue;
            }

            var rewardId = BattlePassSnapshotCodec.DeterministicId($"{ov.QuestId}:{ov.RewardGroup}:{ov.Tpl}", "bp-itemctrl-quest");
            (desired.TryGetValue(questId, out var map) ? map : desired[questId] = new())[rewardId] = (ov, tpl);
        }

        foreach (var questId in _injQuest.Keys.Concat(desired.Keys).Distinct().ToList())
        {
            if (!databaseService.GetQuests().TryGetValue(questId, out var quest) || quest.Rewards is null)
            {
                continue;
            }

            var want = desired.TryGetValue(questId, out var m) ? m : new Dictionary<MongoId, (BpItemOverride ov, MongoId tpl)>();
            var have = _injQuest.TryGetValue(questId, out var h) ? h : new HashSet<MongoId>();

            // 撤销：移除已不在期望态的注入奖励（跨所有奖励组扫描）
            var stale = have.Where(id => !want.ContainsKey(id)).ToHashSet();
            if (stale.Count > 0)
            {
                foreach (var group in quest.Rewards.Values)
                {
                    group?.RemoveAll(r => stale.Contains(r.Id));
                }
            }

            // 注入缺失
            foreach (var (rewardId, entry) in want)
            {
                InjectQuestReward(quest, entry.ov, entry.tpl, rewardId);
            }

            _injQuest[questId] = new HashSet<MongoId>(want.Keys);
        }
    }

    private void InjectQuestReward(Quest quest, BpItemOverride ov, MongoId tpl, MongoId rewardId)
    {
        if (!quest.Rewards!.TryGetValue(ov.RewardGroup, out var rewards))
        {
            rewards = new List<Reward>();
            quest.Rewards[ov.RewardGroup] = rewards;
        }

        if (rewards.Any(r => r.Id == rewardId))
        {
            return; // 幂等
        }

        var itemId = BattlePassSnapshotCodec.DeterministicId($"{rewardId}:item", "bp-itemctrl-qitem");
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

    // ---- 藏身处制造：对账 ----
    private void ReconcileHideout(List<BpItemOverride> adds)
    {
        var recipes = databaseService.GetHideout().Production.Recipes;
        if (recipes is null)
        {
            return;
        }

        // 期望态：output 新建配方 id 集；ingredient 加料集 recipeId -> { tpl -> override }
        var desiredRecipes = new Dictionary<MongoId, BpItemOverride>();
        var desiredIngredients = new Dictionary<MongoId, Dictionary<MongoId, BpItemOverride>>();
        foreach (var ov in adds.Where(o => o.Source == AcqSource.Hideout))
        {
            if (!MongoIdEx.TryParse(ov.Tpl, out var tpl))
            {
                continue;
            }

            if (ov.Role == "ingredient" && MongoIdEx.TryParse(ov.RecipeId, out var rid))
            {
                (desiredIngredients.TryGetValue(rid, out var map) ? map : desiredIngredients[rid] = new())[tpl] = ov;
            }
            else if (ov.Role != "ingredient")
            {
                desiredRecipes[BattlePassSnapshotCodec.DeterministicId($"{ov.Tpl}", "bp-itemctrl-recipe")] = ov;
            }
        }

        // output 配方：撤销移除 + 注入缺失
        foreach (var stale in _injRecipe.Where(id => !desiredRecipes.ContainsKey(id)).ToList())
        {
            recipes.RemoveAll(r => r.Id == stale);
            _injRecipe.Remove(stale);
        }

        foreach (var (recipeId, ov) in desiredRecipes)
        {
            if (MongoIdEx.TryParse(ov.Tpl, out var tpl) && recipes.All(r => r.Id != recipeId))
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

            _injRecipe.Add(recipeId);
        }

        // ingredient 加料：撤销移除 + 注入缺失（按 recipeId 定位现有配方）
        foreach (var recipeId in _injIngredient.Keys.Concat(desiredIngredients.Keys).Distinct().ToList())
        {
            var recipe = recipes.FirstOrDefault(r => r.Id == recipeId);
            if (recipe is null)
            {
                _injIngredient.Remove(recipeId);
                continue;
            }

            recipe.Requirements ??= new List<Requirement>();
            var want = desiredIngredients.TryGetValue(recipeId, out var m) ? m : new Dictionary<MongoId, BpItemOverride>();
            var have = _injIngredient.TryGetValue(recipeId, out var h) ? h : new HashSet<MongoId>();

            foreach (var staleTpl in have.Where(t => !want.ContainsKey(t)).ToList())
            {
                recipe.Requirements.RemoveAll(rq => rq.TemplateId == staleTpl);
            }

            foreach (var (tpl, ov) in want)
            {
                if (recipe.Requirements.All(rq => rq.TemplateId != tpl))
                {
                    recipe.Requirements.Add(new Requirement { TemplateId = tpl, Count = Math.Max(1, ov.Count), Type = "Item" });
                }
            }

            _injIngredient[recipeId] = new HashSet<MongoId>(want.Keys);
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
}
