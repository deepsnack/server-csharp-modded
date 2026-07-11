using System.Security.Cryptography;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
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
    ISptLogger<QuestSync> logger
) : IOnLoad
{
    private bool _localeHooked;

    // 已注入的自定义任务 id（单例存活，进程内记录），撤销时从内存 DB 即时移除、并清理阵营限制。
    private readonly HashSet<MongoId> _injCustom = new();

    // 奖励覆盖的原始桶快照：questId -> (bucketKey -> 原始 List<Reward>；null 表示该桶原本不存在)。
    // 撤销覆盖时按快照还原：原本为 null 的桶删除、否则恢复原引用（替换用的是新建 list，原引用未被破坏）。
    private readonly Dictionary<MongoId, Dictionary<string, List<Reward>?>> _origRewards = new();

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
        foreach (var obj in cq.Objectives)
        {
            var cond = CompileObjective(id, obj, index++);
            if (cond is not null)
            {
                finish.Add(cond);
            }
        }

        var rewards = new Dictionary<string, List<Reward>>
        {
            ["Started"] = new(),
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

    /// <summary>把完成目标编译成 AvailableForFinish 的 <see cref="QuestCondition"/>（v1 支持 handoverItem / kills）。</summary>
    private QuestCondition? CompileObjective(MongoId questId, BpQuestObjective obj, int index)
    {
        var condId = DeterministicId($"{questId}:obj:{index}:{obj.Type}:{obj.Tpl}:{obj.Target}", "bp-quest-obj");
        var count = Math.Max(1, obj.Count);

        if (string.Equals(obj.Type, "handoverItem", StringComparison.OrdinalIgnoreCase))
        {
            if (!MongoIdEx.TryParse(obj.Tpl, out var tpl))
            {
                return null;
            }

            return new QuestCondition
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

        if (string.Equals(obj.Type, "kills", StringComparison.OrdinalIgnoreCase))
        {
            var killCond = new QuestConditionCounterCondition
            {
                Id = DeterministicId($"{condId}:kill", "bp-quest-killcond"),
                ConditionType = "Kills",
                Target = new ListOrT<string>(null, string.IsNullOrWhiteSpace(obj.Target) ? "Any" : obj.Target),
            };

            return new QuestCondition
            {
                Id = condId,
                Index = index,
                ConditionType = "CounterCreator",
                DynamicLocale = false,
                Value = count,
                IsNecessary = true,
                Counter = new QuestConditionCounter
                {
                    Id = DeterministicId($"{condId}:counter", "bp-quest-counter").ToString(),
                    Conditions = new List<QuestConditionCounterCondition> { killCond },
                },
            };
        }

        return null;
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

    /// <summary>把 mod 本地奖励列表编译成 core <see cref="Reward"/> 列表（item / experience / traderStanding / traderUnlock）。</summary>
    private List<Reward> CompileRewards(MongoId questId, string bucket, List<BpQuestReward> src)
    {
        var result = new List<Reward>();
        var index = 0;
        foreach (var r in src)
        {
            var rewardId = DeterministicId($"{questId}:{bucket}:{index}:{r.Type}:{r.Tpl}:{r.TraderId}", "bp-quest-reward");
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
                    Value = r.Count > 0 ? r.Count : r.Value,
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
        }

        return result;
    }

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
