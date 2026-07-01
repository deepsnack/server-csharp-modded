using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class QuestSkipMutationTests
{
    private QuestSkipService _service;
    private BattlePassStashService _stashService;

    [OneTimeSetUp]
    public void Initialize()
    {
        _service = DI.GetInstance().GetService<QuestSkipService>();
        _stashService = DI.GetInstance().GetService<BattlePassStashService>();
    }

    [Test]
    public void ApplyValidatedObjective_ConsumesExactlyOneTicketAndOneObjective()
    {
        var first = Condition(2);
        var second = Condition(4);
        var status = Status();
        var pmc = Profile(status, ticketCount: 2);

        var result = _service.ApplyValidatedObjective(pmc, new MongoId(), status, first, [first, second]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.True);
            Assert.That(result.AvailableForFinish, Is.False);
            Assert.That(_stashService.CountTpl(pmc, QuestSkipTicketService.TicketTpl.ToString(), false), Is.EqualTo(1));
            Assert.That(status.CompletedConditions, Is.EqualTo(new[] { first.Id.ToString() }));
            Assert.That(status.Status, Is.EqualTo(QuestStatusEnum.Started));
        });
    }

    [Test]
    public void ApplyValidatedObjective_LastObjectiveBecomesAvailableForFinish()
    {
        var first = Condition(2);
        var second = Condition(4);
        var status = Status(first.Id.ToString());
        var pmc = Profile(status, ticketCount: 1);

        var result = _service.ApplyValidatedObjective(pmc, new MongoId(), status, second, [first, second]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.True);
            Assert.That(result.AvailableForFinish, Is.True);
            Assert.That(status.Status, Is.EqualTo(QuestStatusEnum.AvailableForFinish));
            Assert.That(status.StatusTimers, Does.ContainKey(QuestStatusEnum.AvailableForFinish));
            Assert.That(status.CompletedConditions, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void RestoreMutationSnapshot_SimulatesSaveFailureWithoutInventoryOrQuestChanges()
    {
        var condition = Condition(3);
        var status = Status();
        var pmc = Profile(status, ticketCount: 1);
        var snapshot = _service.CaptureMutationSnapshot(pmc);

        var mutation = _service.ApplyValidatedObjective(pmc, new MongoId(), status, condition, [condition]);
        Assert.That(mutation.Applied, Is.True);

        QuestSkipService.RestoreMutationSnapshot(pmc, snapshot);
        var restoredStatus = pmc.Quests!.Single();
        Assert.Multiple(() =>
        {
            Assert.That(_stashService.CountTpl(pmc, QuestSkipTicketService.TicketTpl.ToString(), false), Is.EqualTo(1));
            Assert.That(restoredStatus.Status, Is.EqualTo(QuestStatusEnum.Started));
            Assert.That(restoredStatus.CompletedConditions, Is.Empty);
            Assert.That(restoredStatus.StatusTimers, Does.Not.ContainKey(QuestStatusEnum.AvailableForFinish));
        });
    }

    [Test]
    public void MainStashCount_ExcludesTicketInEquipmentAndSecureContainerOutsideStash()
    {
        var status = Status();
        var pmc = Profile(status, ticketCount: 0);
        var equipment = new MongoId();
        var secureContainer = new MongoId();
        pmc.Inventory!.Items.Add(Ticket(equipment));
        pmc.Inventory.Items.Add(Ticket(secureContainer));

        Assert.That(_stashService.CountTpl(pmc, QuestSkipTicketService.TicketTpl.ToString(), false), Is.Zero);
    }

    private static QuestCondition Condition(double value) => new()
    {
        Id = new MongoId(),
        DynamicLocale = false,
        ConditionType = "FindItem",
        Value = value,
    };

    private static QuestStatus Status(params string[] completed) => new()
    {
        QId = new MongoId(),
        StartTime = 1,
        Status = QuestStatusEnum.Started,
        StatusTimers = [],
        CompletedConditions = completed.ToList(),
    };

    private static PmcData Profile(QuestStatus status, int ticketCount)
    {
        var stash = new MongoId();
        var items = Enumerable.Range(0, ticketCount).Select(_ => Ticket(stash)).ToList();
        return new PmcData
        {
            Inventory = new BotBaseInventory { Stash = stash, Items = items },
            InsuredItems = [],
            Quests = [status],
            TaskConditionCounters = [],
        };
    }

    private static Item Ticket(MongoId parentId) => new()
    {
        Id = new MongoId(),
        Template = QuestSkipTicketService.TicketTpl,
        ParentId = parentId,
        Upd = new Upd { StackObjectsCount = 1 },
    };
}
