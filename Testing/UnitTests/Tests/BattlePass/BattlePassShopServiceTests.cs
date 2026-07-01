using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassShopServiceTests
{
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    private const string GpCoin = "5d235b4d86f7742e017bc88a";

    [Test]
    public void TryAggregateCosts_MergesDuplicateTplRows()
    {
        var valid = BattlePassShopService.TryAggregateCosts(
            [
                new BpBarterCost { Tpl = Roubles, Count = 10 },
                new BpBarterCost { Tpl = Roubles.ToUpperInvariant(), Count = 15 },
                new BpBarterCost { Tpl = GpCoin, Count = 2 },
            ],
            out var costs
        );

        Assert.That(valid, Is.True);
        Assert.That(costs, Has.Count.EqualTo(2));
        Assert.That(costs.Single(c => c.Tpl.Equals(Roubles, StringComparison.OrdinalIgnoreCase)).Count, Is.EqualTo(25));
        Assert.That(costs.Single(c => c.Tpl.Equals(GpCoin, StringComparison.OrdinalIgnoreCase)).Count, Is.EqualTo(2));
    }

    [TestCase("not-a-tpl", 1)]
    [TestCase(Roubles, 0)]
    [TestCase(Roubles, -1)]
    public void TryAggregateCosts_RejectsInvalidRows(string tpl, int count)
    {
        var valid = BattlePassShopService.TryAggregateCosts([new BpBarterCost { Tpl = tpl, Count = count }], out _);

        Assert.That(valid, Is.False);
    }

    [Test]
    public void TryScaleCosts_MultipliesMergedUnitCosts()
    {
        var valid = BattlePassShopService.TryScaleCosts(
            [
                new BpBarterCost { Tpl = Roubles, Count = 10 },
                new BpBarterCost { Tpl = Roubles, Count = 5 },
                new BpBarterCost { Tpl = GpCoin, Count = 2 },
            ],
            4,
            out var costs
        );

        Assert.That(valid, Is.True);
        Assert.That(costs.Single(c => c.Tpl == Roubles).Count, Is.EqualTo(60));
        Assert.That(costs.Single(c => c.Tpl == GpCoin).Count, Is.EqualTo(8));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(BattlePassShopService.MaxBatchQuantity + 1)]
    public void TryScaleCosts_RejectsInvalidBatchQuantity(int quantity)
    {
        var valid = BattlePassShopService.TryScaleCosts(
            [new BpBarterCost { Tpl = Roubles, Count = 1 }],
            quantity,
            out _
        );

        Assert.That(valid, Is.False);
    }

    [Test]
    public void TryScaleCosts_RejectsOverflow()
    {
        var valid = BattlePassShopService.TryScaleCosts(
            [new BpBarterCost { Tpl = Roubles, Count = int.MaxValue }],
            2,
            out _
        );

        Assert.That(valid, Is.False);
    }

    [Test]
    public void CalculateMaxPurchaseQuantity_UsesTightestPurchaseCondition()
    {
        var max = BattlePassShopService.CalculateMaxPurchaseQuantity(
            buyLimit: 10,
            bought: 3,
            remainingStock: 6,
            sold: 4,
            sellCount: 2,
            costs:
            [
                new ShopCostView { Count = 20, Have = 100 },
                new ShopCostView { Count = 3, Have = 30 },
            ]
        );

        Assert.That(max, Is.EqualTo(5));
    }

    [Test]
    public void CalculateMaxPurchaseQuantity_DoesNotCrossBuyLimit()
    {
        var max = BattlePassShopService.CalculateMaxPurchaseQuantity(
            buyLimit: 5,
            bought: 4,
            remainingStock: -1,
            sold: 0,
            sellCount: 1,
            costs: []
        );

        Assert.That(max, Is.EqualTo(1));
    }
}
