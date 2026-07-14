using System.Security.Cryptography;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     商人任务管理的应用器：把 <c>custom-quests.json</c> / <c>quest-overrides.json</c> 重放到内存 DB。
///     - 自定义任务：把 mod 本地 record 编译成 core <see cref="Quest"/> 注入 DB，进程内追踪以便撤销即时移除。
///     - 奖励覆盖：整桶替换原版任务 Started/Success/Fail 奖励，快照原始桶以便还原。
///     - 禁用：不改 DB，交给 <see cref="QuestClientMaskService"/> 在下发链路服务期屏蔽（本类只负责触发重建）。
///     OnLoad 于 +90000（与 ItemControlSync 同批，晚于物品 mod）；后台保存后可运行时再 <see cref="Sync"/>。
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 90000)]
public class QuestSync(
    DatabaseService databaseService,
    ConfigServer configServer,
    QuestClientMaskService maskService,
    TraderAssortHelper traderAssortHelper,
    ICloner cloner,
    ISptLogger<QuestSync> logger
) : IOnLoad
{
    private bool _localeHooked;

    // 已注入的自定义任务 id（单例存活，进程内记录），撤销时从内存 DB 即时移除、并清理阵营限制。
    private readonly HashSet<MongoId> _injCustom = new();

    // 奖励覆盖的原始桶快照：questId -> (bucketKey -> 原始 List<Reward>；null 表示该桶原本不存在)。
    // 撤销覆盖时按快照还原：原本为 null 的桶删除、否则恢复原引用（替换用的是新建 list，原引用未被破坏）。
    private readonly Dictionary<MongoId, Dictionary<string, List<Reward>?>> _origRewards = new();

    // 本同步器写入的原版 Trader.QuestAssort 映射原值；删除/改奖时精确还原，不碰其它 mod/原版映射。
    private readonly Dictionary<QuestAssortKey, MongoId?> _origQuestAssort = new();

    public Task OnLoad()
    {
        Sync();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     全量对账式应用：以当前 custom-quests / quest-overrides 为唯一真源，注入缺失、撤销已删项、
    ///     还原被移除的奖励覆盖；禁用集交给 <see cref="QuestClientMaskService"/> 服务期屏蔽。可运行时重复调用。
    /// </summary>
    public void Sync()
    {
        try
        {
            HookLocaleTransformers(); // 仅首次真正注册

            var customs = BattlePassStore.GetCustomQuests();
            var overrides = BattlePassStore.GetQuestOverrides();

            ReconcileCustomQuests(customs);
            ReconcileRewardOverrides(overrides);
            ReconcileQuestAssortMappings(customs, overrides);

            // 禁用走服务期屏蔽（QuestHelper 下发链路克隆后过滤，不动 DB）
            maskService.Rebuild();

            logger.Success(
                $"[SPT-BattlePass] 商人任务已对账应用（自定义 {customs.Count} 条、覆盖 {overrides.Count} 条；注入/撤销即时，禁用走服务期屏蔽）。");
        }
        catch (Exception ex)
        {
            logger.Error($"[SPT-BattlePass] 商人任务应用失败: {ex.Message}");
        }
    }

    // ---- 自定义任务：对账 ----
    private void ReconcileCustomQuests(List<BpCustomQuest> customs)
    {
        var quests = databaseService.GetTables().Templates.Quests;
        var questConfig = configServer.GetConfig<QuestConfig>();

        var desired = new Dictionary<MongoId, BpCustomQuest>();
        foreach (var cq in customs)
        {
            if (MongoIdEx.TryParse(cq.Id, out var id) && MongoIdEx.TryParse(cq.TraderId, out _))
            {
                desired[id] = cq;
            }
        }

        // 撤销：移除已不在期望态的注入任务（连带阵营限制）
        foreach (var stale in _injCustom.Where(id => !desired.ContainsKey(id)).ToList())
        {
            quests.Remove(stale);
            questConfig.UsecOnlyQuests?.Remove(stale);
            questConfig.BearOnlyQuests?.Remove(stale);
            _injCustom.Remove(stale);
        }

        // 注入/刷新：全量重建该任务（幂等——先移除再加，保证运行时编辑即时反映条件/奖励变化）
        foreach (var (id, cq) in desired)
        {
            var quest = CompileCustomQuest(id, cq);
            quests[id] = quest; // 覆盖式写入（TryAdd 不足以反映编辑）

            // 阵营限制（Usec/Bear 加入对应白名单；Pmc 两派可见——先清理旧限制再按需加）
            questConfig.UsecOnlyQuests?.Remove(id);
            questConfig.BearOnlyQuests?.Remove(id);
            if (string.Equals(cq.Side, "Usec", StringComparison.OrdinalIgnoreCase))
            {
                questConfig.UsecOnlyQuests?.Add(id);
            }
            else if (string.Equals(cq.Side, "Bear", StringComparison.OrdinalIgnoreCase))
            {
                questConfig.BearOnlyQuests?.Add(id);
            }

            _injCustom.Add(id);
        }
    }

    /// <summary>把 mod 本地 <see cref="BpCustomQuest"/> 编译成 core <see cref="Quest"/>。</summary>
    private Quest CompileCustomQuest(MongoId id, BpCustomQuest cq)
    {
        MongoIdEx.TryParse(cq.TraderId, out var traderId);

        var start = new List<QuestCondition>();
        foreach (var pre in cq.Prerequisites)
        {
            if (!MongoIdEx.TryParse(pre.QuestId, out var preId))
            {
                continue;
            }

            start.Add(new QuestCondition
            {
                Id = DeterministicId($"{id}:start:{pre.QuestId}", "bp-quest-cond"),
                ConditionType = "Quest",
                DynamicLocale = false,
                Target = new ListOrT<string>(new List<string> { preId.ToString() }, null),
                Status = new HashSet<QuestStatusEnum>(
                    (pre.Status is { Count: > 0 } ? pre.Status : new List<int> { 4 })
                    .Select(s => (QuestStatusEnum)s)),
                AvailableAfter = pre.AvailableAfter > 0 ? pre.AvailableAfter : null,
            });
        }

        var finish = new List<QuestCondition>();
        var index = 0;
        MongoId? previousConditionId = null;
        foreach (var obj in cq.Objectives)
        {
            var cond = CompileObjective(id, obj, index++, previousConditionId);
            if (cond is not null)
            {
                finish.Add(cond);
                previousConditionId = cond.Id;
            }
        }

        var rewards = new Dictionary<string, List<Reward>>
        {
            ["Started"] = CompileRewards(id, "Started", cq.StartedRewards),
            ["Success"] = CompileRewards(id, "Success", cq.Rewards),
            ["Fail"] = new(),
        };

        return new Quest
        {
            Id = id,
            QuestName = string.IsNullOrWhiteSpace(cq.QuestName) ? cq.NameZh : cq.QuestName,
            CanShowNotificationsInGame = true,
            TraderId = traderId,
            Location = string.IsNullOrWhiteSpace(cq.Location) ? "any" : cq.Location,
            Image = "",
            Type = QuestTypeEnum.Completion,
            Restartable = false,
            Side = "Pmc",
            Name = string.IsNullOrWhiteSpace(cq.NameZh) ? cq.QuestName : cq.NameZh,
            Description = cq.DescriptionZh,
            AcceptPlayerMessage = $"{id} acceptPlayerMessage",
            DeclinePlayerMessage = $"{id} declinePlayerMessage",
            CompletePlayerMessage = $"{id} completePlayerMessage",
            AcceptanceAndFinishingSource = "eft",
            Conditions = new QuestConditionTypes
            {
                Started = new List<QuestCondition>(),
                AvailableForStart = start,
                AvailableForFinish = finish,
                Success = new List<QuestCondition>(),
                Fail = new List<QuestCondition>(),
            },
            Rewards = rewards,
        };
    }

    /// <summary>把本地目标编译成原版 AvailableForFinish 条件。</summary>
    private QuestCondition? CompileObjective(MongoId questId, BpQuestObjective obj, int index, MongoId? previousConditionId)
    {
        var condId = DeterministicId($"{questId}:obj:{index}:{obj.Type}:{obj.Tpl}:{obj.Target}", "bp-quest-obj");
        var count = Math.Max(1, obj.Count);
        QuestCondition? result = null;

        if (string.Equals(obj.Type, "handoverItem", StringComparison.OrdinalIgnoreCase))
        {
            if (!MongoIdEx.TryParse(obj.Tpl, out var tpl))
            {
                return null;
            }

            result = new QuestCondition
            {
                Id = condId,
                Index = index,
                ConditionType = "HandoverItem",
                DynamicLocale = false,
                Target = new ListOrT<string>(new List<string> { tpl.ToString() }, null),
                Value = count,
                OnlyFoundInRaid = obj.OnlyFoundInRaid,
                IsNecessary = true,
            };
        }
        else if (string.Equals(obj.Type, "weaponAssembly", StringComparison.OrdinalIgnoreCase))
        {
            if (!MongoIdEx.TryParse(obj.Tpl, out var weaponTpl))
            {
                return null;
            }

            result = new QuestCondition
            {
                Id = condId,
                Index = index,
                ConditionType = "WeaponAssembly",
                Type = "WeaponAssembly",
                DynamicLocale = false,
                Target = new ListOrT<string>([weaponTpl.ToString()], null),
                Value = count,
                IsNecessary = true,
                ContainsItems = obj.ContainsItems.ToList(),
                HasItemFromCategory = obj.HasItemFromCategory.ToList(),
                BaseAccuracy = CompileCompare(obj.BaseAccuracy, ">=", 0),
                Durability = CompileCompare(obj.Durability, ">=", 0),
                EffectiveDistance = CompileCompare(obj.EffectiveDistance, ">=", 0),
                EmptyTacticalSlot = CompileCompare(obj.EmptyTacticalSlot, ">=", 0),
                Ergonomics = CompileCompare(obj.Ergonomics, ">=", 0),
                Height = CompileCompare(obj.Height, "<=", 100),
                MagazineCapacity = CompileCompare(obj.MagazineCapacity, ">=", 0),
                MuzzleVelocity = CompileCompare(obj.MuzzleVelocity, ">=", 0),
                Recoil = CompileCompare(obj.Recoil, "<=", 10000),
                Weight = CompileCompare(obj.Weight, "<=", 100),
                Width = CompileCompare(obj.Width, "<=", 100),
            };
        }
        else if (string.Equals(obj.Type, "kills", StringComparison.OrdinalIgnoreCase))
        {
            var targets = obj.Targets.Count > 0 ? obj.Targets : [string.IsNullOrWhiteSpace(obj.Target) ? "Any" : obj.Target];
            if (obj.SavageRoles.Count > 0)
            {
                targets = ["Savage"];
            }

            var killCond = new QuestConditionCounterCondition
            {
                Id = DeterministicId($"{condId}:kill", "bp-quest-killcond"),
                ConditionType = "Kills",
                DynamicLocale = false,
                Target = CompileTarget(targets, "Any"),
                Value = 1,
                CompareMethod = ">=",
                ResetOnSessionEnd = false,
                EnemyHealthEffects = [],
                Daytime = obj.Daytime is null
                    ? new DaytimeCounter { From = 0, To = 0 }
                    : new DaytimeCounter { From = obj.Daytime.From, To = obj.Daytime.To },
                Weapon = obj.Weapons.Count > 0 ? obj.Weapons.ToHashSet(StringComparer.OrdinalIgnoreCase) : [],
                WeaponModsInclusive = obj.WeaponMods.Select(mod => new List<string> { mod }).ToList(),
                WeaponModsExclusive = [],
                WeaponCaliber = obj.WeaponCalibers.ToList(),
                SavageRole = obj.SavageRoles.ToList(),
                BodyPart = obj.BodyParts.ToList(),
                Distance = obj.Distance is null
                    ? new CounterConditionDistance { CompareMethod = ">=", Value = 0 }
                    : new CounterConditionDistance
                    {
                        CompareMethod = obj.Distance.CompareMethod,
                        Value = obj.Distance.Value,
                    },
            };

            var nested = new List<QuestConditionCounterCondition> { killCond };
            AddLocationCondition(nested, condId, obj.Locations);

            result = new QuestCondition
            {
                Id = condId,
                Index = index,
                ConditionType = "CounterCreator",
                Type = "Elimination",
                DynamicLocale = false,
                Value = count,
                IsNecessary = true,
                OneSessionOnly = obj.OneLife,
                IsResetOnConditionFailed = false,
                DoNotResetIfCounterCompleted = false,
                Counter = new QuestConditionCounter
                {
                    Id = DeterministicId($"{condId}:counter", "bp-quest-counter").ToString(),
                    Conditions = nested,
                },
            };
        }
        else if (string.Equals(obj.Type, "transit", StringComparison.OrdinalIgnoreCase))
        {
            var nested = new List<QuestConditionCounterCondition>
            {
                new()
                {
                    Id = DeterministicId($"{condId}:transit", "bp-quest-transitcond"),
                    DynamicLocale = false,
                    ConditionType = "ExitStatus",
                    Status = ["Transit"],
                },
            };
            AddLocationCondition(nested, condId, obj.Locations);

            result = new QuestCondition
            {
                Id = condId,
                Index = index,
                ConditionType = "CounterCreator",
                Type = "Completion",
                DynamicLocale = false,
                Value = count,
                IsNecessary = true,
                OneSessionOnly = obj.OneLife,
                IsResetOnConditionFailed = false,
                DoNotResetIfCounterCompleted = false,
                Counter = new QuestConditionCounter
                {
                    Id = DeterministicId($"{condId}:counter", "bp-quest-counter").ToString(),
                    Conditions = nested,
                },
            };
        }

        if (result is not null && obj.DependsOnPrevious && previousConditionId is not null)
        {
            result.VisibilityConditions =
            [
                new VisibilityCondition
                {
                    Id = DeterministicId($"{condId}:visibility:{previousConditionId}", "bp-quest-visible").ToString(),
                    Target = previousConditionId.Value.ToString(),
                    ConditionType = "CompleteCondition",
                    DynamicLocale = false,
                },
            ];
        }

        return result;
    }

    private static void AddLocationCondition(
        ICollection<QuestConditionCounterCondition> nested,
        MongoId conditionId,
        IReadOnlyCollection<string> locations)
    {
        if (locations.Count == 0)
        {
            return;
        }

        nested.Add(new QuestConditionCounterCondition
        {
            Id = DeterministicId($"{conditionId}:location:{string.Join(',', locations)}", "bp-quest-locationcond"),
            DynamicLocale = false,
            ConditionType = "Location",
            Target = new ListOrT<string>(locations.ToList(), null),
        });
    }

    private static ListOrT<string> CompileTarget(IReadOnlyList<string> targets, string fallback)
    {
        return targets.Count switch
        {
            0 => new ListOrT<string>(null, fallback),
            1 => new ListOrT<string>(null, targets[0]),
            _ => new ListOrT<string>(targets.ToList(), null),
        };
    }

    private static ValueCompare CompileCompare(BpQuestValueCompare? source, string defaultMethod, double defaultValue)
    {
        return new ValueCompare
        {
            CompareMethod = source?.CompareMethod ?? defaultMethod,
            Value = source?.Value ?? defaultValue,
        };
    }

    // ---- 奖励覆盖：对账 ----
    private void ReconcileRewardOverrides(List<BpQuestOverride> overrides)
    {
        // 期望态：questId -> 要替换的 bucket 集（仅 Rewards != null 的覆盖参与）
        var desired = new Dictionary<MongoId, Dictionary<string, List<BpQuestReward>>>();
        foreach (var ov in overrides)
        {
            if (ov.Rewards is null || !MongoIdEx.TryParse(ov.QuestId, out var questId))
            {
                continue;
            }

            desired[questId] = ov.Rewards;
        }

        foreach (var questId in _origRewards.Keys.Concat(desired.Keys).Distinct().ToList())
        {
            if (!databaseService.GetQuests().TryGetValue(questId, out var quest))
            {
                _origRewards.Remove(questId);
                continue;
            }

            quest.Rewards ??= new Dictionary<string, List<Reward>>();
            var want = desired.TryGetValue(questId, out var w) ? w : new Dictionary<string, List<BpQuestReward>>();
            var snapshot = _origRewards.TryGetValue(questId, out var s) ? s : null;

            // 撤销：还原快照里已不再被覆盖的桶
            if (snapshot is not null)
            {
                foreach (var (bucket, orig) in snapshot.Where(kv => !want.ContainsKey(kv.Key)).ToList())
                {
                    if (orig is null)
                    {
                        quest.Rewards.Remove(bucket);
                    }
                    else
                    {
                        quest.Rewards[bucket] = orig;
                    }

                    snapshot.Remove(bucket);
                }
            }

            // 应用：整桶替换（首次替换前快照原始桶引用）
            if (want.Count > 0)
            {
                snapshot ??= _origRewards[questId] = new Dictionary<string, List<Reward>?>();
                foreach (var (bucket, list) in want)
                {
                    if (!snapshot.ContainsKey(bucket))
                    {
                        snapshot[bucket] = quest.Rewards.TryGetValue(bucket, out var existing) ? existing : null;
                    }

                    quest.Rewards[bucket] = CompileRewards(questId, bucket, list);
                }
            }

            if (snapshot is null || snapshot.Count == 0)
            {
                _origRewards.Remove(questId);
            }
        }
    }

    /// <summary>把 mod 本地奖励列表编译成原版 <see cref="Reward"/>。</summary>
    private List<Reward> CompileRewards(MongoId questId, string bucket, List<BpQuestReward> src)
    {
        var result = new List<Reward>();
        var index = 0;
        foreach (var r in src)
        {
            var rewardId = DeterministicId(
                $"{questId}:{bucket}:{index}:{r.Type}:{r.Tpl}:{r.TraderId}:{r.OfferId}:{r.RecipeId}",
                "bp-quest-reward");
            var type = (r.Type ?? "item").ToLowerInvariant();

            if (type == "item")
            {
                if (!MongoIdEx.TryParse(r.Tpl, out var tpl))
                {
                    continue;
                }

                var count = Math.Max(1, r.Count);
                result.Add(new Reward
                {
                    Id = rewardId,
                    Type = RewardType.Item,
                    Index = index++,
                    Value = count,
                    FindInRaid = r.FoundInRaid,
                    Items = new List<Item>
                    {
                        new()
                        {
                            Id = DeterministicId($"{rewardId}:item", "bp-quest-ritem"),
                            Template = tpl,
                            ParentId = null,
                            Upd = new Upd { StackObjectsCount = count },
                        },
                    },
                });
            }
            else if (type == "experience")
            {
                result.Add(new Reward
                {
                    Id = rewardId,
                    Type = RewardType.Experience,
                    Index = index++,
                    Value = r.Value > 0 ? r.Value : r.Count,
                });
            }
            else if (type == "traderstanding")
            {
                if (!MongoIdEx.TryParse(r.TraderId, out var trader))
                {
                    continue;
                }

                result.Add(new Reward
                {
                    Id = rewardId,
                    Type = RewardType.TraderStanding,
                    Index = index++,
                    Target = trader.ToString(),
                    Value = r.Value,
                });
            }
            else if (type == "traderunlock")
            {
                if (!MongoIdEx.TryParse(r.TraderId, out var trader))
                {
                    continue;
                }

                result.Add(new Reward
                {
                    Id = rewardId,
                    Type = RewardType.TraderUnlock,
                    Index = index++,
                    Target = trader.ToString(),
                });
            }
            else if (type == "assortmentunlock")
            {
                if (!MongoIdEx.TryParse(r.TraderId, out var trader)
                    || !MongoIdEx.TryParse(r.OfferId, out var offerId)
                    || !databaseService.GetTables().Traders.TryGetValue(trader, out var traderData)
                    || traderData.Assort is null)
                {
                    continue;
                }

                var rewardItems = traderData.Assort.Items.GetItemWithChildren(offerId);
                if (rewardItems.Count == 0)
                {
                    continue;
                }

                result.Add(new Reward
                {
                    Id = rewardId,
                    Type = RewardType.AssortmentUnlock,
                    Index = index++,
                    Target = offerId.ToString(),
                    TraderId = trader.ToString(),
                    LoyaltyLevel = traderData.Assort.LoyalLevelItems.GetValueOrDefault(offerId, 1),
                    Items = cloner.Clone(rewardItems),
                });
            }
            else if (type == "productionscheme")
            {
                if (!MongoIdEx.TryParse(r.RecipeId, out var recipeId))
                {
                    continue;
                }

                var recipe = databaseService.GetHideout().Production.Recipes.FirstOrDefault(candidate => candidate.Id == recipeId);
                if (recipe?.AreaType is null || !ProductionRewardCanResolve(questId, recipe.Id))
                {
                    logger.Warning($"[SPT-BattlePass] 跳过无法唯一解析的任务配方奖励: quest={questId}, recipe={r.RecipeId}");
                    continue;
                }

                var rewardItemId = DeterministicId($"{rewardId}:production-item", "bp-quest-production-item");
                var count = Math.Max(1, recipe.Count ?? 1);
                result.Add(new Reward
                {
                    Id = rewardId,
                    Type = RewardType.ProductionScheme,
                    Index = index++,
                    Target = rewardItemId.ToString(),
                    TraderId = (int)recipe.AreaType.Value,
                    LoyaltyLevel = ProductionRequiredLevel(recipe),
                    Items =
                    [
                        new Item
                        {
                            Id = rewardItemId,
                            Template = recipe.EndProduct,
                            Upd = new Upd { StackObjectsCount = count },
                        },
                    ],
                });
            }
        }

        return result;
    }

    internal bool ProductionRewardCanResolve(MongoId questId, MongoId recipeId)
    {
        var recipes = databaseService.GetHideout().Production.Recipes ?? [];
        var recipe = recipes.FirstOrDefault(candidate => candidate.Id == recipeId);
        if (recipe?.AreaType is null)
        {
            return false;
        }

        var byQuest = recipes.Where(candidate => candidate.Requirements?.Any(req => req.QuestId == questId) == true).ToList();
        if (byQuest.Count == 1 && byQuest[0].Id == recipe.Id)
        {
            return true;
        }

        var requiredLevel = ProductionRequiredLevel(recipe);
        var fallback = recipes.Where(candidate =>
                candidate.AreaType == recipe.AreaType
                && candidate.EndProduct == recipe.EndProduct
                && candidate.Locked.GetValueOrDefault(false)
                && candidate.Requirements?.Any(req => req.Type == "QuestComplete") == true
                && candidate.Requirements.Any(req => req.RequiredLevel == requiredLevel))
            .ToList();
        return fallback.Count == 1 && fallback[0].Id == recipe.Id;
    }

    private static int ProductionRequiredLevel(HideoutProduction recipe)
    {
        var levels = (recipe.Requirements ?? [])
            .Where(req => req.Type == "Area" && req.RequiredLevel is not null)
            .Select(req => req.RequiredLevel!.Value)
            .ToList();
        return levels.Count == 0 ? 1 : Math.Max(1, levels.Max());
    }

    // ---- 原版货架直购权映射：对账 ----
    private void ReconcileQuestAssortMappings(List<BpCustomQuest> customs, List<BpQuestOverride> overrides)
    {
        var desired = new Dictionary<QuestAssortKey, MongoId>();
        foreach (var custom in customs)
        {
            if (!MongoIdEx.TryParse(custom.Id, out var questId))
            {
                continue;
            }

            CollectQuestAssortMappings(desired, questId, "Started", custom.StartedRewards);
            CollectQuestAssortMappings(desired, questId, "Success", custom.Rewards);
        }

        foreach (var questOverride in overrides)
        {
            if (questOverride.Rewards is null || !MongoIdEx.TryParse(questOverride.QuestId, out var questId))
            {
                continue;
            }

            foreach (var (bucket, rewards) in questOverride.Rewards)
            {
                CollectQuestAssortMappings(desired, questId, bucket, rewards);
            }
        }

        var changed = false;
        foreach (var key in _origQuestAssort.Keys.Concat(desired.Keys).Distinct().ToList())
        {
            if (!databaseService.GetTables().Traders.TryGetValue(key.TraderId, out var trader))
            {
                continue;
            }

            if (trader.QuestAssort is null)
            {
                continue;
            }
            if (!trader.QuestAssort.TryGetValue(key.Bucket, out var bucketMap))
            {
                bucketMap = trader.QuestAssort[key.Bucket] = new Dictionary<MongoId, MongoId>();
            }

            if (desired.TryGetValue(key, out var questId))
            {
                if (!_origQuestAssort.ContainsKey(key))
                {
                    // MongoId has an implicit string conversion: `condition ? mongoId : null` is inferred as a
                    // non-null MongoId and turns the null branch into MongoId.Empty. Preserve a true nullable here,
                    // otherwise removing a newly-added unlock restores an empty-valued dictionary entry.
                    if (bucketMap.TryGetValue(key.OfferId, out var original))
                    {
                        _origQuestAssort[key] = original;
                    }
                    else
                    {
                        _origQuestAssort[key] = null;
                    }
                }

                if (!bucketMap.TryGetValue(key.OfferId, out var current) || current != questId)
                {
                    bucketMap[key.OfferId] = questId;
                    changed = true;
                }
            }
            else if (_origQuestAssort.Remove(key, out var original))
            {
                if (original is null)
                {
                    changed |= bucketMap.Remove(key.OfferId);
                }
                else if (!bucketMap.TryGetValue(key.OfferId, out var current) || current != original.Value)
                {
                    bucketMap[key.OfferId] = original.Value;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            traderAssortHelper.InvalidateQuestAssortCache();
        }
    }

    private void CollectQuestAssortMappings(
        IDictionary<QuestAssortKey, MongoId> desired,
        MongoId questId,
        string bucket,
        IEnumerable<BpQuestReward> rewards)
    {
        foreach (var reward in rewards.Where(reward =>
                     string.Equals(reward.Type, "assortmentUnlock", StringComparison.OrdinalIgnoreCase)))
        {
            if (!MongoIdEx.TryParse(reward.TraderId, out var traderId)
                || !MongoIdEx.TryParse(reward.OfferId, out var offerId)
                || !databaseService.GetTables().Traders.TryGetValue(traderId, out var trader)
                || trader.Assort?.Items.Any(item => item.Id == offerId) != true)
            {
                continue;
            }

            desired[new QuestAssortKey(traderId, bucket.ToLowerInvariant(), offerId)] = questId;
        }
    }

    private readonly record struct QuestAssortKey(MongoId TraderId, string Bucket, MongoId OfferId);

    // ---- 自定义任务本地化：仅注册一次，内部实时读 store ----
    private void HookLocaleTransformers()
    {
        if (_localeHooked)
        {
            return;
        }

        foreach (var (lang, lazy) in databaseService.GetLocales().Global)
        {
            var language = lang;
            lazy.AddTransformer(localeData =>
            {
                if (localeData is null)
                {
                    return localeData;
                }

                foreach (var cq in BattlePassStore.GetCustomQuests())
                {
                    if (string.IsNullOrWhiteSpace(cq.Id))
                    {
                        continue;
                    }

                    var isEn = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
                    var name = isEn ? cq.NameEn ?? cq.NameZh : cq.NameZh;
                    var desc = isEn ? cq.DescriptionEn ?? cq.DescriptionZh : cq.DescriptionZh;

                    localeData[$"{cq.Id} name"] = name;
                    localeData[$"{cq.Id} description"] = desc;
                    // 接取/完成对话文本占位（避免客户端显示原始 key）
                    localeData[$"{cq.Id} acceptPlayerMessage"] = name;
                    localeData[$"{cq.Id} declinePlayerMessage"] = name;
                    localeData[$"{cq.Id} completePlayerMessage"] = name;
                }

                return localeData;
            });
        }

        _localeHooked = true;
    }

    private static MongoId DeterministicId(string seed, string salt)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + seed));
        return new MongoId(Convert.ToHexString(bytes).ToLowerInvariant()[..24]);
    }
}
