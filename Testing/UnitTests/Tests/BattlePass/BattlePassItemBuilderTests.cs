using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassItemBuilderTests
{
    private static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    private static readonly MongoId Salewa = new("544fb45d4bdc2dee738b4568");

    // 弹药箱 ammo_box_762x39_20_PS：装 20 发 7.62x39 PS（tpl 5656d7c34bdc2d9d198b4587）。
    private static readonly MongoId AmmoBox762x39 = new("5649ed104bdc2d3d1c8b458b");
    private static readonly MongoId Ammo762x39Ps = new("5656d7c34bdc2d9d198b4587");

    private BattlePassItemBuilder _builder;
    private ItemHelper _itemHelper;

    [OneTimeSetUp]
    public void Initialize()
    {
        _builder = DI.GetInstance().GetService<BattlePassItemBuilder>();
        _itemHelper = DI.GetInstance().GetService<ItemHelper>();
    }

    [Test]
    public void BuildRewardStacks_SplitsNonStackableRewardsIntoSeparateRootItems()
    {
        var items = _builder.BuildRewardStacks(Salewa, 3);
        var roots = items.Where(item => item.Template == Salewa && item.ParentId is null).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(roots, Has.Count.EqualTo(3));
            Assert.That(roots.Select(item => item.Upd?.StackObjectsCount), Is.All.EqualTo(1));
        });
    }

    [Test]
    public void Build_FillsAmmoBoxWithCartridges()
    {
        var rootId = new MongoId();
        var items = _builder.Build(AmmoBox762x39, 1, rootId);

        var cartridges = items.Where(item => item.SlotId == "cartridges" && item.ParentId == rootId).ToList();

        Assert.Multiple(() =>
        {
            // 箱子根件在首位，且带有装满的子弹子件（否则玩家买到/收到空箱）。
            Assert.That(items[0].Id, Is.EqualTo(rootId));
            Assert.That(cartridges, Is.Not.Empty);
            Assert.That(cartridges.Select(item => item.Template), Is.All.EqualTo(Ammo762x39Ps));
            Assert.That(cartridges.Sum(item => item.Upd?.StackObjectsCount ?? 0), Is.EqualTo(20));
        });
    }

    [Test]
    public void BuildRewardStacks_FillsAmmoBoxWithCartridges()
    {
        var items = _builder.BuildRewardStacks(AmmoBox762x39, 1);

        var boxes = items.Where(item => item.Template == AmmoBox762x39).ToList();
        var cartridges = items.Where(item => item.SlotId == "cartridges").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(boxes, Has.Count.EqualTo(1));
            Assert.That(cartridges.Sum(item => item.Upd?.StackObjectsCount ?? 0), Is.EqualTo(20));
            // 每个子弹子件都挂在箱子根件下。
            Assert.That(cartridges.Select(item => item.ParentId), Is.All.EqualTo(boxes[0].Id.ToString()));
        });
    }

    [Test]
    public void BuildRewardStacks_SplitsStackableRewardsByTemplateStackLimit()
    {
        var maxStack = _itemHelper.GetItem(Roubles).Value!.Properties!.StackMaxSize!.Value;
        var count = maxStack + 123;

        var items = _builder.BuildRewardStacks(Roubles, count);

        Assert.Multiple(() =>
        {
            Assert.That(items, Has.Count.EqualTo(2));
            Assert.That(items.Sum(item => item.Upd?.StackObjectsCount ?? 0), Is.EqualTo(count));
            Assert.That(items.Select(item => item.Upd?.StackObjectsCount ?? 0), Is.All.LessThanOrEqualTo(maxStack));
        });
    }
}
