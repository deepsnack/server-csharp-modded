using System.Text.Json;
using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class QuestSyncTests
{
    private QuestSync _service;

    [OneTimeSetUp]
    public void Initialize()
    {
        _service = DI.GetInstance().GetService<QuestSync>();
    }

    [Test]
    public void ObjectiveText_RoundTripsThroughJson()
    {
        var source = new BpQuestObjective
        {
            Type = "kills",
            Count = 3,
            TextZh = "击败三名指定目标",
            TextEn = "Eliminate three specified targets",
        };

        var json = JsonSerializer.Serialize(source);
        var result = JsonSerializer.Deserialize<BpQuestObjective>(json);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.TextZh, Is.EqualTo(source.TextZh));
            Assert.That(result.TextEn, Is.EqualTo(source.TextEn));
        });
    }

    [Test]
    public void ObjectiveLocaleText_PrefersConfiguredLanguageAndFallsBackToChinese()
    {
        var objective = new BpQuestObjective
        {
            Type = "kills",
            Count = 2,
            TextZh = "自定义中文目标",
            TextEn = "Custom English objective",
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                QuestSync.ObjectiveLocaleText(objective, false, new Dictionary<string, string>()),
                Is.EqualTo("自定义中文目标"));
            Assert.That(
                QuestSync.ObjectiveLocaleText(objective, true, new Dictionary<string, string>()),
                Is.EqualTo("Custom English objective"));
        });

        objective.TextEn = null;
        Assert.That(
            QuestSync.ObjectiveLocaleText(objective, true, new Dictionary<string, string>()),
            Is.EqualTo("自定义中文目标"));
    }

    [Test]
    public void ObjectiveLocaleText_WhenUnsetNeverLeaksTpl()
    {
        const string tpl = "64d4b23dc1b37504b41ac2b6";
        var objective = new BpQuestObjective
        {
            Type = "handoverItem",
            Tpl = tpl,
            Count = 4,
        };

        var text = QuestSync.ObjectiveLocaleText(objective, false, new Dictionary<string, string>());

        Assert.Multiple(() =>
        {
            Assert.That(text, Is.EqualTo("上交指定物品 ×4"));
            Assert.That(text, Does.Not.Contain(tpl));
        });
    }

    [Test]
    public void ObjectiveLocaleText_UsesKnownItemLocale()
    {
        const string tpl = "64d4b23dc1b37504b41ac2b6";
        var objective = new BpQuestObjective
        {
            Type = "weaponAssembly",
            Tpl = tpl,
            Count = 1,
        };
        var locale = new Dictionary<string, string>
        {
            [$"{tpl} Name"] = "测试步枪",
        };

        Assert.That(
            QuestSync.ObjectiveLocaleText(objective, false, locale),
            Is.EqualTo("上交符合要求的测试步枪 ×1"));
    }

    [Test]
    public void ItemReward_TargetPointsToRootRewardItem()
    {
        var rewards = _service.CompileRewards(
            new MongoId("64d4b23dc1b37504b41ac2b6"),
            "Started",
            [
                new BpQuestReward
                {
                    Type = "item",
                    Tpl = "5449016a4bdc2d6f028b456f",
                    Count = 2,
                    Name = "卢布",
                },
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(rewards, Has.Count.EqualTo(1));
            Assert.That(rewards[0].Type, Is.EqualTo(RewardType.Item));
            Assert.That(rewards[0].Items, Has.Count.EqualTo(1));
            Assert.That(rewards[0].Target, Is.EqualTo(rewards[0].Items[0].Id.ToString()));
        });
    }

    [Test]
    public void RewardItemNameFallbacks_PreserveModLocaleAndFillMissingShortName()
    {
        const string tpl = "64d4b23dc1b37504b41ac2b6";
        var locale = new Dictionary<string, string>
        {
            [$"{tpl} Name"] = "物品 Mod 自带名称",
        };

        QuestSync.AddRewardItemNameFallbacks(
            locale,
            [
                new BpQuestReward
                {
                    Type = "item",
                    Tpl = tpl,
                    Name = "后台保存的物品名称",
                },
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(locale[$"{tpl} Name"], Is.EqualTo("物品 Mod 自带名称"));
            Assert.That(locale[$"{tpl} ShortName"], Is.EqualTo("后台保存的物品名称"));
        });
    }
}
