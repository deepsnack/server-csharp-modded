using System.Text.Json;
using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassPurchaseRightsTests
{
    [Test]
    public void PurchaseRight_SurvivesProgressJsonRoundTrip()
    {
        var progress = new BpProgress { SeasonId = "S1" };
        Assert.That(BattlePassPurchaseRights.Grant(progress, "offer-alpha"), Is.True);

        var json = JsonSerializer.Serialize(progress);
        var reloaded = JsonSerializer.Deserialize<BpProgress>(json)!;

        Assert.Multiple(() =>
        {
            Assert.That(BattlePassPurchaseRights.Has(reloaded, "offer-alpha"), Is.True);
            Assert.That(BattlePassPurchaseRights.Grant(reloaded, "OFFER-ALPHA"), Is.False);
        });
    }

    [Test]
    public void Migrate_RestoresRightsFromRewardLedgerAndClaimedCycle()
    {
        var progress = new BpProgress
        {
            RewardLedgerInitialized = true,
            GrantedTrackRewards = new()
            {
                ["level:1:free"] = new HashSet<string> { "purchaseright:legacy-offer" },
            },
            ClaimedCyclePremium = [1],
        };
        var tracks = new Dictionary<int, BpLevelRewards>
        {
            [BattlePassStore.CycleRewardLevelKey] = new()
            {
                Premium = [new BpReward { Type = "purchaseRight", OfferId = "cycle-offer" }],
            },
        };

        Assert.That(BattlePassPurchaseRights.Migrate(progress, tracks), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(BattlePassPurchaseRights.Has(progress, "legacy-offer"), Is.True);
            Assert.That(BattlePassPurchaseRights.Has(progress, "cycle-offer"), Is.True);
        });
    }

    [Test]
    public void Migrate_DoesNotGrantNewTrackRewardBeforeCompensationClaim()
    {
        var progress = new BpProgress
        {
            RewardLedgerInitialized = true,
            ClaimedFree = [1],
        };
        var tracks = new Dictionary<int, BpLevelRewards>
        {
            [1] = new()
            {
                Free = [new BpReward { Type = "purchaseRight", OfferId = "newly-added-offer" }],
            },
        };

        Assert.That(BattlePassPurchaseRights.Migrate(progress, tracks), Is.False);
        Assert.That(BattlePassPurchaseRights.Has(progress, "newly-added-offer"), Is.False);
    }

    [Test]
    public void FilterOffers_RemovesUnauthorizedOfferAndKeepsAuthorizedOffer()
    {
        var locked = Offer("locked");
        var allowed = Offer("allowed");
        var progress = new BpProgress { PurchaseRights = new HashSet<string> { allowed.Id } };
        var assort = Assort(locked, allowed);

        var result = BattlePassTraderAccessService.FilterOffers(progress, [locked, allowed], assort, isFlea: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Items.Any(x => x.Id == BattlePassTraderSync.OfferRootItemId(locked.Id)), Is.False);
            Assert.That(result.Items.Any(x => x.Id == BattlePassTraderSync.OfferRootItemId(allowed.Id)), Is.True);
        });
    }

    [Test]
    public void FilterOffers_ForFleaMarksUnauthorizedOfferLocked()
    {
        var offer = Offer("locked");
        var assort = Assort(offer);

        var result = BattlePassTraderAccessService.FilterOffers(new BpProgress(), [offer], assort, isFlea: true);

        Assert.That(
            result.BarterScheme[BattlePassTraderSync.OfferRootItemId(offer.Id)][0][0].SptQuestLocked,
            Is.True
        );
    }

    private static BpTraderOffer Offer(string id) => new()
    {
        Id = id,
        Tpl = "aaaaaaaaaaaaaaaaaaaaaaaa",
    };

    private static TraderAssort Assort(params BpTraderOffer[] offers)
    {
        var items = new List<Item>();
        var barter = new Dictionary<MongoId, List<List<BarterScheme>>>();
        var loyal = new Dictionary<MongoId, int>();
        foreach (var offer in offers)
        {
            var rootId = BattlePassTraderSync.OfferRootItemId(offer.Id);
            items.Add(new Item
            {
                Id = rootId,
                Template = new MongoId(offer.Tpl),
                ParentId = "hideout",
                SlotId = "hideout",
            });
            barter[rootId] =
            [
                [
                    new BarterScheme
                    {
                        Count = 1,
                        Template = new MongoId("5449016a4bdc2d6f028b456f"),
                    },
                ],
            ];
            loyal[rootId] = 1;
        }

        return new TraderAssort
        {
            Items = items,
            BarterScheme = barter,
            LoyalLevelItems = loyal,
        };
    }
}
