using System.Reflection;
using NUnit.Framework;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace UnitTests.Tests.Helpers;

[TestFixture]
public class TraderAssortAccessTests
{
    private AssortHelper _assortHelper;
    private TraderAssortHelper _traderAssortHelper;
    private TraderHelper _traderHelper;

    [OneTimeSetUp]
    public void Initialize()
    {
        _assortHelper = DI.GetInstance().GetService<AssortHelper>();
        _traderAssortHelper = DI.GetInstance().GetService<TraderAssortHelper>();
        _traderHelper = DI.GetInstance().GetService<TraderHelper>();
    }

    [Test]
    public void StripLockedQuestAssort_WithoutUnlockQuestSuccess_RemovesOffer()
    {
        var rootId = new MongoId();
        var questId = new MongoId();
        var assort = CreateAssort(rootId);
        var profile = CreateProfile([]);

        var result = _assortHelper.StripLockedQuestAssort(
            profile,
            new MongoId(),
            assort,
            CreateQuestAssort(rootId, questId)
        );

        Assert.That(result.Items, Is.Empty);
        Assert.That(result.BarterScheme, Does.Not.ContainKey(rootId));
        Assert.That(result.LoyalLevelItems, Does.Not.ContainKey(rootId));
    }

    [Test]
    public void StripLockedQuestAssort_WithUnlockQuestSuccess_KeepsOffer()
    {
        var rootId = new MongoId();
        var questId = new MongoId();
        var assort = CreateAssort(rootId);
        var profile = CreateProfile(
            [
                new QuestStatus
                {
                    QId = questId,
                    StartTime = 1,
                    Status = QuestStatusEnum.Success,
                    StatusTimers = new Dictionary<QuestStatusEnum, double> { [QuestStatusEnum.Success] = 1 },
                },
            ]
        );

        var result = _assortHelper.StripLockedQuestAssort(
            profile,
            new MongoId(),
            assort,
            CreateQuestAssort(rootId, questId)
        );

        Assert.That(result.Items.Any(item => item.Id.ToString() == rootId.ToString()), Is.True);
        Assert.That(result.BarterScheme, Does.ContainKey(rootId));
        Assert.That(result.LoyalLevelItems, Does.ContainKey(rootId));
    }

    [Test]
    public void InvalidateQuestAssortCache_ClearsHydratedIndex()
    {
        var cacheField = typeof(TraderAssortHelper).GetField("_mergedQuestAssorts", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(cacheField, Is.Not.Null);
        cacheField!.SetValue(
            _traderAssortHelper,
            new Dictionary<string, Dictionary<MongoId, MongoId>> { ["success"] = new() }
        );

        _traderAssortHelper.InvalidateQuestAssortCache();

        Assert.That(cacheField.GetValue(_traderAssortHelper), Is.Null);
    }

    [Test]
    public void SetTraderUpdateSeconds_RegistersFixedNativeRefreshInterval()
    {
        var traderId = new MongoId();

        _traderHelper.SetTraderUpdateSeconds(traderId, 1234, "unit-test");

        Assert.That(_traderHelper.GetTraderUpdateSeconds(traderId), Is.EqualTo(1234));
    }

    private static PmcData CreateProfile(List<QuestStatus> quests)
    {
        return new PmcData
        {
            CheckedChambers = [],
            Quests = quests,
            TradersInfo = [],
            MoneyTransferLimitData = new MoneyTransferLimits(),
        };
    }

    private static TraderAssort CreateAssort(MongoId rootId)
    {
        return new TraderAssort
        {
            Items = [new Item { Id = rootId, Template = new MongoId(), ParentId = "hideout", SlotId = "hideout" }],
            BarterScheme = new Dictionary<MongoId, List<List<BarterScheme>>> { [rootId] = [[]] },
            LoyalLevelItems = new Dictionary<MongoId, int> { [rootId] = 1 },
        };
    }

    private static Dictionary<string, Dictionary<MongoId, MongoId>> CreateQuestAssort(MongoId rootId, MongoId questId)
    {
        return new Dictionary<string, Dictionary<MongoId, MongoId>>
        {
            ["success"] = new Dictionary<MongoId, MongoId> { [rootId] = questId },
        };
    }
}
