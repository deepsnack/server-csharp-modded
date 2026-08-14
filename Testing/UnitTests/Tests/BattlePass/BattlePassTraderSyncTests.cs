using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassTraderSyncTests
{
    [Test]
    public void ResetNativeTraderPurchaseRemovesOnlyBattlePassTrader()
    {
        var otherTraderId = new MongoId("777777777777777777777777");
        var profile = new SptProfile
        {
            TraderPurchases = new Dictionary<MongoId, Dictionary<MongoId, TraderPurchaseData>?>
            {
                [new MongoId(BattlePassTraderSync.TraderIdHex)] = null,
                [otherTraderId] = null,
            },
        };

        var removed = BattlePassTraderSync.ResetNativeTraderPurchase(profile);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(profile.TraderPurchases, Does.Not.ContainKey(new MongoId(BattlePassTraderSync.TraderIdHex)));
            Assert.That(profile.TraderPurchases, Does.ContainKey(otherTraderId));
        });
    }

    [Test]
    public void ResetNativeTraderPurchaseIsNoOpWhenRecordIsAbsent()
    {
        var profile = new SptProfile();

        var removed = BattlePassTraderSync.ResetNativeTraderPurchase(profile);

        Assert.That(removed, Is.False);
    }
}
