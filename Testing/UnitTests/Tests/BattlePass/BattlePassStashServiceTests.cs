using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassStashServiceTests
{
    private const string TargetTpl = "5449016a4bdc2d6f028b456f";
    private BattlePassStashService _service;

    [OneTimeSetUp]
    public void Initialize()
    {
        _service = new BattlePassStashService(DI.GetInstance().GetService<InventoryHelper>());
    }

    [Test]
    public void CountTpl_UsesOnlyStashItemsAndHonorsFir()
    {
        var stashId = new MongoId();
        var equipmentId = new MongoId();
        var profile = Profile(stashId,
            Item(TargetTpl, stashId, 5, fir: true),
            Item(TargetTpl, stashId, 3, fir: false),
            Item(TargetTpl, equipmentId, 20, fir: true));

        Assert.That(_service.CountTpl(profile, TargetTpl, requireFir: false), Is.EqualTo(8));
        Assert.That(_service.CountTpl(profile, TargetTpl, requireFir: true), Is.EqualTo(5));
    }

    [Test]
    public void RemoveTpl_PartiallyReducesStack()
    {
        var stashId = new MongoId();
        var stack = Item(TargetTpl, stashId, 5, fir: true);
        var profile = Profile(stashId, stack);

        var removed = _service.RemoveTpl(profile, new MongoId(), TargetTpl, 3, requireFir: false);

        Assert.That(removed, Is.EqualTo(3));
        Assert.That(stack.Upd!.StackObjectsCount, Is.EqualTo(2));
        Assert.That(_service.CountTpl(profile, TargetTpl, requireFir: false), Is.EqualTo(2));
    }

    [Test]
    public void CountTpl_SkipsMatchingContainerWithChildren()
    {
        var stashId = new MongoId();
        var container = Item(TargetTpl, stashId, 1, fir: true);
        var child = Item("5d235b4d86f7742e017bc88a", container.Id, 1, fir: true);
        var profile = Profile(stashId, container, child);

        Assert.That(_service.CountTpl(profile, TargetTpl, requireFir: false), Is.Zero);
    }

    private static PmcData Profile(MongoId stashId, params Item[] items)
    {
        return new PmcData { Inventory = new BotBaseInventory { Stash = stashId, Items = items.ToList() } };
    }

    private static Item Item(string tpl, MongoId parentId, int count, bool fir)
    {
        return new Item
        {
            Id = new MongoId(),
            Template = new MongoId(tpl),
            ParentId = parentId,
            Upd = new Upd { StackObjectsCount = count, SpawnedInSession = fir ? true : null },
        };
    }
}
