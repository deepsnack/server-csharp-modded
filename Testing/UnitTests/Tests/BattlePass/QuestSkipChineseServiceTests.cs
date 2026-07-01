using System.Text.RegularExpressions;
using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public partial class QuestSkipChineseServiceTests
{
    private QuestSkipChineseService _service;
    private DatabaseService _databaseService;

    [OneTimeSetUp]
    public void Initialize()
    {
        _service = DI.GetInstance().GetService<QuestSkipChineseService>();
        _databaseService = DI.GetInstance().GetService<DatabaseService>();
    }

    [Test]
    public void EveryDatabaseCompletionCondition_HasSafeChineseNameAndDescription()
    {
        var checkedCount = 0;
        foreach (var quest in _databaseService.GetQuests().Values)
        {
            foreach (var condition in quest.Conditions.AvailableForFinish ?? [])
            {
                if (!string.IsNullOrWhiteSpace(condition.ParentId))
                {
                    continue;
                }

                AssertSafeChinese(_service.ConditionName(condition));
                AssertSafeChinese(_service.ConditionDescription(condition, false));
                checkedCount++;
            }
        }

        Assert.That(checkedCount, Is.GreaterThan(1000));
    }

    [Test]
    public void EveryKnownNestedCounterType_HasChineseMapping()
    {
        var nestedTypes = _databaseService.GetQuests().Values
            .SelectMany(quest => quest.Conditions.AvailableForFinish ?? [])
            .SelectMany(condition => condition.Counter?.Conditions ?? [])
            .Select(condition => condition.ConditionType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.That(nestedTypes, Is.Not.Empty);
        foreach (var conditionType in nestedTypes)
        {
            var name = _service.ConditionNameForType(conditionType);
            AssertSafeChinese(name);
            Assert.That(name, Is.Not.EqualTo("特殊任务条件"), $"缺少嵌套条件类型映射: {conditionType}");
        }
    }

    [Test]
    public void CounterCreator_UsesConcreteNestedConditionName()
    {
        var counters = _databaseService.GetQuests().Values
            .SelectMany(quest => quest.Conditions.AvailableForFinish ?? [])
            .Where(condition => string.Equals(condition.ConditionType, "CounterCreator", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.That(counters, Is.Not.Empty);
        foreach (var counter in counters)
        {
            var name = _service.ConditionName(counter);
            AssertSafeChinese(name);
            Assert.That(name, Is.Not.EqualTo("累计进度"));
        }
    }

    [Test]
    public void FixedAndRepeatableQuestTitles_AreChineseAndContainNoIds()
    {
        var fixedQuest = _databaseService.GetQuests().Values.First();
        AssertSafeChinese(_service.QuestTitle(fixedQuest, false, null));
        AssertSafeChinese(_service.QuestTitle(fixedQuest, true, "Daily"));
        AssertSafeChinese(_service.QuestTitle(fixedQuest, true, "Weekly"));
    }

    private static void AssertSafeChinese(string text)
    {
        Assert.Multiple(() =>
        {
            Assert.That(text, Is.Not.Null.And.Not.Empty);
            Assert.That(ChineseRegex().IsMatch(text), Is.True, text);
            Assert.That(MongoIdRegex().IsMatch(text), Is.False, text);
            Assert.That(InternalTypeRegex().IsMatch(text), Is.False, text);
        });
    }

    [GeneratedRegex("[\\u3400-\\u9fff]")]
    private static partial Regex ChineseRegex();

    [GeneratedRegex("(?i)(?<![0-9a-f])[0-9a-f]{24}(?![0-9a-f])")]
    private static partial Regex MongoIdRegex();

    [GeneratedRegex("(?i)CounterCreator|HandoverItem|FindItem|LeaveItemAtLocation|PlaceBeacon|WeaponAssembly|SellItemToTrader|UNKNOWN")]
    private static partial Regex InternalTypeRegex();
}
