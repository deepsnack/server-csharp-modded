using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.BattlePass.Administration;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
[NonParallelizable]
public class QuestManagementCompilationTests
{
    private QuestSync _sync = null!;
    private QuestChangeHandler _handler = null!;
    private DatabaseService _database = null!;

    [OneTimeSetUp]
    public void Initialize()
    {
        var di = DI.GetInstance();
        _sync = di.GetService<QuestSync>();
        _handler = di.GetService<QuestChangeHandler>();
        _database = di.GetService<DatabaseService>();
    }

    [Test]
    public void CompileCustomQuest_UsesVanillaAdvancedConditionsAndRewardBuckets()
    {
        var questId = new MongoId("aaaaaaaaaaaaaaaaaaaaaaaa");
        var (traderId, offerId) = FindAssortOffer();
        var recipeId = FindResolvableRecipe(questId);
        var quest = Compile(questId, CreateAdvancedQuest(questId, traderId, offerId, recipeId));

        Assert.Multiple(() =>
        {
            Assert.That(quest.Conditions.AvailableForFinish, Has.Count.EqualTo(4));
            Assert.That(quest.Rewards["Started"], Has.Count.EqualTo(1));
            Assert.That(quest.Rewards["Started"][0].Type, Is.EqualTo(RewardType.Item));
            Assert.That(quest.Rewards["Started"][0].Items![0].Upd!.StackObjectsCount, Is.EqualTo(5000));
            Assert.That(quest.Rewards["Success"].Any(reward => reward.Type == RewardType.AssortmentUnlock), Is.True);
            Assert.That(quest.Rewards["Success"].Any(reward => reward.Type == RewardType.ProductionScheme), Is.True);
        });

        var assembly = quest.Conditions.AvailableForFinish![0];
        Assert.Multiple(() =>
        {
            Assert.That(assembly.ConditionType, Is.EqualTo("WeaponAssembly"));
            Assert.That(assembly.ContainsItems, Does.Contain("59bffc1f86f77435b128b872"));
            Assert.That(assembly.Ergonomics?.CompareMethod, Is.EqualTo(">="));
            Assert.That(assembly.Ergonomics?.Value, Is.EqualTo(47));
            Assert.That(assembly.Recoil?.CompareMethod, Is.EqualTo("<="));
            Assert.That(assembly.Recoil?.Value, Is.EqualTo(850));
        });

        var firstKill = quest.Conditions.AvailableForFinish[1];
        var kill = firstKill.Counter!.Conditions!.Single(condition => condition.ConditionType == "Kills");
        var location = firstKill.Counter.Conditions.Single(condition => condition.ConditionType == "Location");
        Assert.Multiple(() =>
        {
            Assert.That(firstKill.OneSessionOnly, Is.True);
            Assert.That(kill.Target!.Item, Is.EqualTo("Savage"));
            Assert.That(kill.SavageRole, Does.Contain("bossKilla"));
            Assert.That(kill.Weapon, Does.Contain("54491c4f4bdc2db1078b4568"));
            Assert.That(kill.WeaponModsInclusive!.SelectMany(group => group), Does.Contain("59bffc1f86f77435b128b872"));
            Assert.That(kill.Distance?.Value, Is.EqualTo(100));
            Assert.That(kill.BodyPart, Does.Contain("Head"));
            Assert.That(kill.Daytime?.From, Is.EqualTo(22));
            Assert.That(kill.Daytime?.To, Is.EqualTo(6));
            Assert.That(location.Target!.List, Does.Contain("bigmap"));
        });

        var transit = quest.Conditions.AvailableForFinish[2];
        var finalKill = quest.Conditions.AvailableForFinish[3];
        Assert.Multiple(() =>
        {
            Assert.That(transit.Counter!.Conditions!.Any(condition =>
                condition.ConditionType == "ExitStatus" && condition.Status!.Contains("Transit")), Is.True);
            Assert.That(transit.VisibilityConditions![0].Target, Is.EqualTo(firstKill.Id.ToString()));
            Assert.That(finalKill.VisibilityConditions![0].Target, Is.EqualTo(transit.Id.ToString()));
        });

        var assortment = quest.Rewards["Success"].Single(reward => reward.Type == RewardType.AssortmentUnlock);
        var production = quest.Rewards["Success"].Single(reward => reward.Type == RewardType.ProductionScheme);
        Assert.Multiple(() =>
        {
            Assert.That(assortment.Target, Is.EqualTo(offerId.ToString()));
            Assert.That(assortment.TraderId?.ToString(), Is.EqualTo(traderId.ToString()));
            Assert.That(assortment.Items, Is.Not.Empty);
            Assert.That(production.Items, Is.Not.Empty);
            Assert.That(production.TraderId, Is.TypeOf<int>());
        });
    }

    [Test]
    public void ChangeHandler_NormalizesAndValidatesAdvancedQuestPayload()
    {
        var questId = new MongoId("bbbbbbbbbbbbbbbbbbbbbbbb");
        var (traderId, offerId) = FindAssortOffer();
        var recipeId = FindResolvableRecipe(questId);
        var input = JsonSerializer.SerializeToElement(CreateAdvancedQuest(questId, traderId, offerId, recipeId));

        var normalized = (BpCustomQuest)_handler.Normalize("quest.customUpsert", input);

        Assert.Multiple(() =>
        {
            Assert.That(_handler.Validate("quest.customUpsert", normalized), Is.Null);
            Assert.That(normalized.StartedRewards, Has.Count.EqualTo(1));
            Assert.That(normalized.Objectives[1].Locations, Is.EqualTo(new[] { "bigmap" }));
            Assert.That(normalized.Objectives[2].DependsOnPrevious, Is.True);
        });
    }

    [Test]
    public void LegacyQuestPayload_CompilesWithoutMigration()
    {
        var questId = new MongoId("dddddddddddddddddddddddd");
        var (traderId, _) = FindAssortOffer();
        var input = JsonSerializer.SerializeToElement(new
        {
            id = questId.ToString(),
            traderId = traderId.ToString(),
            nameZh = "旧版击杀任务",
            objectives = new[] { new { type = "kills", count = 2, target = "Savage" } },
            rewards = new[] { new { type = "experience", count = 100 } },
        });

        var normalized = (BpCustomQuest)_handler.Normalize("quest.customUpsert", input);
        var quest = Compile(questId, normalized);
        var kill = quest.Conditions.AvailableForFinish![0].Counter!.Conditions!
            .Single(condition => condition.ConditionType == "Kills");

        Assert.Multiple(() =>
        {
            Assert.That(_handler.Validate("quest.customUpsert", normalized), Is.Null);
            Assert.That(normalized.StartedRewards, Is.Empty);
            Assert.That(normalized.Objectives[0].Targets, Is.Empty);
            Assert.That(kill.Target!.Item, Is.EqualTo("Savage"));
            Assert.That(quest.Rewards["Started"], Is.Empty);
            Assert.That(quest.Rewards["Success"][0].Value, Is.EqualTo(100));
        });
    }

    [Test]
    public void ChangeHandler_RejectsInvalidSequenceAndUnknownAssortOffer()
    {
        var questId = new MongoId("eeeeeeeeeeeeeeeeeeeeeeee");
        var (traderId, _) = FindAssortOffer();
        var invalidSequence = new BpCustomQuest
        {
            Id = questId.ToString(),
            TraderId = traderId.ToString(),
            NameZh = "非法阶段链",
            Objectives = [new BpQuestObjective { Type = "kills", DependsOnPrevious = true }],
        };
        var invalidOffer = new BpCustomQuest
        {
            Id = questId.ToString(),
            TraderId = traderId.ToString(),
            NameZh = "非法直购权",
            Objectives = [new BpQuestObjective { Type = "kills" }],
            Rewards =
            [
                new BpQuestReward
                {
                    Type = "assortmentUnlock",
                    TraderId = traderId.ToString(),
                    OfferId = "ffffffffffffffffffffffff",
                },
            ],
        };

        Assert.Multiple(() =>
        {
            Assert.That(_handler.Validate("quest.customUpsert", invalidSequence), Does.Contain("第一个目标"));
            Assert.That(_handler.Validate("quest.customUpsert", invalidOffer), Does.Contain("不存在"));
        });
    }

    [Test]
    public void QuestAssortMapping_IsAppliedAndRestoredReversibly()
    {
        var questId = new MongoId("cccccccccccccccccccccccc");
        var (traderId, offerId) = FindAssortOffer(requireUnmappedSuccessOffer: true);
        var trader = _database.GetTables().Traders[traderId];
        trader.QuestAssort!.TryGetValue("success", out var success);
        success ??= trader.QuestAssort["success"] = [];
        var hadOriginal = success.TryGetValue(offerId, out var originalQuestId);
        var existingCustoms = BattlePassStore.GetCustomQuests();
        var existingOverrides = BattlePassStore.GetQuestOverrides();
        var custom = new BpCustomQuest
        {
            Id = questId.ToString(),
            TraderId = traderId.ToString(),
            NameZh = "直购权对账测试",
            Objectives = [new BpQuestObjective { Type = "kills", Count = 1 }],
            Rewards =
            [
                new BpQuestReward
                {
                    Type = "assortmentUnlock",
                    TraderId = traderId.ToString(),
                    OfferId = offerId.ToString(),
                },
            ],
        };

        var method = typeof(QuestSync).GetMethod("ReconcileQuestAssortMappings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            method.Invoke(_sync, [existingCustoms.Append(custom).ToList(), existingOverrides]);
            Assert.That(success[offerId], Is.EqualTo(questId));
        }
        finally
        {
            method.Invoke(_sync, [existingCustoms, existingOverrides]);
        }

        Assert.That(success.ContainsKey(offerId), Is.EqualTo(hadOriginal));
        if (hadOriginal)
        {
            Assert.That(success[offerId], Is.EqualTo(originalQuestId));
        }
    }

    private Quest Compile(MongoId questId, BpCustomQuest custom)
    {
        var method = typeof(QuestSync).GetMethod("CompileCustomQuest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Quest)method.Invoke(_sync, [questId, custom])!;
    }

    private BpCustomQuest CreateAdvancedQuest(MongoId questId, MongoId traderId, MongoId offerId, MongoId recipeId)
    {
        return new BpCustomQuest
        {
            Id = questId.ToString(),
            TraderId = traderId.ToString(),
            QuestName = "BattlePass advanced trader quest test",
            NameZh = "高级商人任务测试",
            DescriptionZh = "测试原版条件和奖励编译",
            Objectives =
            [
                new BpQuestObjective
                {
                    Type = "weaponAssembly",
                    Tpl = "54491c4f4bdc2db1078b4568",
                    ContainsItems = ["59bffc1f86f77435b128b872"],
                    Ergonomics = new BpQuestValueCompare { CompareMethod = ">=", Value = 47 },
                    Recoil = new BpQuestValueCompare { CompareMethod = "<=", Value = 850 },
                },
                new BpQuestObjective
                {
                    Type = "kills",
                    Count = 2,
                    Target = "Savage",
                    SavageRoles = ["bossKilla"],
                    Locations = ["bigmap"],
                    Weapons = ["54491c4f4bdc2db1078b4568"],
                    WeaponMods = ["59bffc1f86f77435b128b872"],
                    BodyParts = ["Head"],
                    Distance = new BpQuestValueCompare { CompareMethod = ">=", Value = 100 },
                    Daytime = new BpQuestDaytime { From = 22, To = 6 },
                    OneLife = true,
                },
                new BpQuestObjective
                {
                    Type = "transit",
                    Locations = ["bigmap"],
                    DependsOnPrevious = true,
                },
                new BpQuestObjective
                {
                    Type = "kills",
                    Count = 1,
                    Target = "AnyPmc",
                    Locations = ["Interchange"],
                    DependsOnPrevious = true,
                },
            ],
            StartedRewards =
            [
                new BpQuestReward { Type = "item", Tpl = "5449016a4bdc2d6f028b456f", Count = 5000 },
            ],
            Rewards =
            [
                new BpQuestReward
                {
                    Type = "assortmentUnlock",
                    TraderId = traderId.ToString(),
                    OfferId = offerId.ToString(),
                },
                new BpQuestReward { Type = "productionScheme", RecipeId = recipeId.ToString() },
            ],
        };
    }

    private (MongoId TraderId, MongoId OfferId) FindAssortOffer(bool requireUnmappedSuccessOffer = false)
    {
        foreach (var (traderId, trader) in _database.GetTables().Traders)
        {
            if (trader.Assort?.LoyalLevelItems is not { Count: > 0 } || trader.QuestAssort is null)
            {
                continue;
            }

            var success = trader.QuestAssort.GetValueOrDefault("success");
            var offerId = trader.Assort.LoyalLevelItems.Keys.FirstOrDefault(candidate =>
                !requireUnmappedSuccessOffer || success?.ContainsKey(candidate) != true);
            if (offerId != default)
            {
                return (traderId, offerId);
            }
        }

        throw new AssertionException("测试数据库中没有可用的商人货架商品");
    }

    private MongoId FindResolvableRecipe(MongoId questId)
    {
        var method = typeof(QuestSync).GetMethod("ProductionRewardCanResolve", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var recipe in _database.GetHideout().Production.Recipes ?? [])
        {
            if ((bool)method.Invoke(_sync, [questId, recipe.Id])!)
            {
                return recipe.Id;
            }
        }

        throw new AssertionException("测试数据库中没有可唯一匹配的任务解锁配方");
    }
}
