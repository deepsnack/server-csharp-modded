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
}
