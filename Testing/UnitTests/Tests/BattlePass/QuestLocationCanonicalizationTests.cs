using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
[NonParallelizable]
public class QuestLocationCanonicalizationTests
{
    private QuestSync _sync = null!;

    [OneTimeSetUp]
    public void Initialize()
    {
        var di = DI.GetInstance();
        _sync = di.GetService<QuestSync>();
    }

    /// <summary>
    ///     地图条件 target 必须规范化为客户端权威 <see cref="LocationBase.Id"/>（首字母大写，如 "Labyrinth"/"Woods"）。
    ///     客户端 GClass4045 用大小写敏感的 List.Contains 匹配 GameWorld.LocationId；
    ///     若服务端下发小写目录名（"labyrinth"），击杀 counter 永不累计，任务永远无法完成。
    /// </summary>
    [TestCase("labyrinth", "Labyrinth")]
    [TestCase("Woods", "Woods")]
    [TestCase("shoreline", "Shoreline")]
    [TestCase("factory4_day", "factory4_day")]
    [TestCase("bigmap", "bigmap")]
    [TestCase("unknown_map_xyz", "unknown_map_xyz")]
    public void CompileCustomQuest_CanonicalizesLocationTargetToBaseId(string input, string expected)
    {
        var quest = Compile(
            new MongoId("aaaaaaaaaaaaaaaaaaaaaaaa"),
            new BpCustomQuest
            {
                Id = "aaaaaaaaaaaaaaaaaaaaaaaa",
                TraderId = "54cb57776803fa99248b456e",
                NameZh = "地图规范化测试",
                Objectives =
                [
                    new BpQuestObjective { Type = "kills", Count = 3, Target = "Savage", Locations = [input] },
                ],
                Rewards = [new BpQuestReward { Type = "experience", Count = 100 }],
            });

        var kill = quest.Conditions.AvailableForFinish![0].Counter!.Conditions!
            .Single(condition => condition.ConditionType == "Kills");
        var location = quest.Conditions.AvailableForFinish[0].Counter.Conditions
            .Single(condition => condition.ConditionType == "Location");

        Assert.Multiple(() =>
        {
            Assert.That(kill.Target!.Item, Is.EqualTo("Savage"));
            Assert.That(location.Target!.List, Does.Contain(expected));
        });
    }

    [Test]
    public void CompileCustomQuest_NoLocations_OmitsLocationCondition()
    {
        var quest = Compile(
            new MongoId("bbbbbbbbbbbbbbbbbbbbbbbb"),
            new BpCustomQuest
            {
                Id = "bbbbbbbbbbbbbbbbbbbbbbbb",
                TraderId = "54cb57776803fa99248b456e",
                NameZh = "无地图测试",
                Objectives = [new BpQuestObjective { Type = "kills", Count = 1, Target = "Any" }],
                Rewards = [new BpQuestReward { Type = "experience", Count = 100 }],
            });

        var counter = quest.Conditions.AvailableForFinish![0].Counter!;
        Assert.That(counter.Conditions!.Any(condition => condition.ConditionType == "Location"), Is.False);
    }

    private Quest Compile(MongoId questId, BpCustomQuest custom)
    {
        var method = typeof(QuestSync).GetMethod("CompileCustomQuest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Quest)method.Invoke(_sync, [questId, custom])!;
    }
}
