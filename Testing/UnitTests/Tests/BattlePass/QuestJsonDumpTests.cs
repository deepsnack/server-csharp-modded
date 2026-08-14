using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
[NonParallelizable]
public class QuestJsonDumpTests
{
    private QuestSync _sync = null!;

    [OneTimeSetUp]
    public void Initialize()
    {
        var di = DI.GetInstance();
        _sync = di.GetService<QuestSync>();
    }

    [Test]
    public void DumpCompiledQuestJson()
    {
        var questId = new MongoId("aaaaaaaaaaaaaaaaaaaaaaaa");
        var custom = new BpCustomQuest
        {
            Id = questId.ToString(),
            TraderId = "54cb57776803fa99248b456e",
            QuestName = "Test kills quest",
            NameZh = "测试击杀任务",
            DescriptionZh = "desc",
            Objectives =
            [
                new BpQuestObjective { Type = "kills", Count = 3, Target = "Savage", Locations = ["labyrinth"] },
                new BpQuestObjective { Type = "handoverItem", Tpl = "54491c4f4bdc2db1078b4568", Count = 1 },
            ],
            Rewards = [new BpQuestReward { Type = "item", Tpl = "5449016a4bdc2d6f028b456f", Count = 100 }],
        };
        var method = typeof(QuestSync).GetMethod("CompileCustomQuest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var quest = (Quest)method.Invoke(_sync, [questId, custom])!;
        var json = JsonSerializer.Serialize(quest, JsonUtil.JsonSerializerOptionsIndented);
        System.IO.File.WriteAllText(@"F:\SPT dev\SPT\临时\compiled-quest-dump.json", json);
        TestContext.Progress.WriteLine(json);
    }
}
