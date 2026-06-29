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
