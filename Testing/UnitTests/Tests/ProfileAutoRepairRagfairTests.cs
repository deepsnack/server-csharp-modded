using NUnit.Framework;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests;

[TestFixture]
public class ProfileAutoRepairRagfairTests
{
    private const string KnownTpl = "5449016a4bdc2d6f028b456f";
    private ProfileAutoRepairService _service;

    [OneTimeSetUp]
    public void Initialize()
    {
        _service = DI.GetInstance().GetService<ProfileAutoRepairService>();
    }

    [Test]
    public void RepairProfile_RemovesOffersThatWouldCrashClientParser()
    {
        var offerId = new MongoId();
        var rootItem = Item(new MongoId(), KnownTpl);

        var valid = Offer(offerId, rootItem.Id, rootItem, Item(id: new MongoId(), template: "5d235b4d86f7742e017bc88a", parentId: rootItem.Id));
        var danglingRoot = Offer(new MongoId(), new MongoId(), Item(new MongoId(), KnownTpl));
        var emptyItems = Offer(new MongoId(), new MongoId());
        var unknownTemplate = Offer(new MongoId(), new MongoId(), Item(new MongoId(), "ffffffffffffffffffffffff"));

        var profile = Profile(valid, danglingRoot, emptyItems, unknownTemplate);

        var summary = _service.RepairProfile(profile, new MongoId(), "unit-test");

        Assert.That(summary.InvalidOffersRemoved, Is.EqualTo(3));
        Assert.That(profile.CharacterData!.PmcData!.RagfairInfo!.Offers!.Count, Is.EqualTo(1));
        Assert.That(profile.CharacterData.PmcData.RagfairInfo.Offers[0].Id, Is.EqualTo(offerId));
    }

    [Test]
    public void RepairProfile_KeepsWellFormedOffers()
    {
        var offerId = new MongoId();
        var rootItem = Item(new MongoId(), KnownTpl);
        var profile = Profile(Offer(offerId, rootItem.Id, rootItem));

        var summary = _service.RepairProfile(profile, new MongoId(), "unit-test");

        Assert.That(summary.InvalidOffersRemoved, Is.Zero);
        Assert.That(profile.CharacterData!.PmcData!.RagfairInfo!.Offers!.Count, Is.EqualTo(1));
    }

    [Test]
    public void RepairProfile_AlsoRepairsScavOffers()
    {
        var rootItem = Item(new MongoId(), KnownTpl);
        var profile = new SptProfile
        {
            CharacterData = new Characters
            {
                ScavData = new PmcData
                {
                    RagfairInfo = new RagfairInfo
                    {
                        Offers = [Offer(new MongoId(), rootItem.Id, rootItem)]
                    }
                }
            }
        };

        var summary = _service.RepairProfile(profile, new MongoId(), "unit-test");

        Assert.That(summary.InvalidOffersRemoved, Is.Zero);
        Assert.That(profile.CharacterData.ScavData.RagfairInfo.Offers!.Count, Is.EqualTo(1));
    }

    [Test]
    public void FilterClientSafeOffers_ReturnsOnlyParseableOffers()
    {
        var goodRoot = Item(new MongoId(), KnownTpl);
        var good = Offer(new MongoId(), goodRoot.Id, goodRoot);
        var badRoot = Offer(new MongoId(), new MongoId(), Item(new MongoId(), KnownTpl));
        var badTemplate = Offer(new MongoId(), new MongoId(), Item(new MongoId(), "ffffffffffffffffffffffff"));

        var filtered = _service.FilterClientSafeOffers([good, badRoot, badTemplate]);

        Assert.That(filtered.Count, Is.EqualTo(1));
        Assert.That(filtered[0].Id, Is.EqualTo(good.Id));
    }

    [Test]
    public void FilterClientSafeOffers_NullOrEmptyReturnsEmpty()
    {
        Assert.That(_service.FilterClientSafeOffers(null), Is.Empty);
        Assert.That(_service.FilterClientSafeOffers([]), Is.Empty);
    }

    private static SptProfile Profile(params RagfairOffer[] offers)
    {
        return new SptProfile
        {
            CharacterData = new Characters
            {
                PmcData = new PmcData
                {
                    RagfairInfo = new RagfairInfo { Offers = offers.ToList() }
                }
            }
        };
    }

    private static RagfairOffer Offer(MongoId id, MongoId root, params Item[] items)
    {
        return new RagfairOffer
        {
            Id = id,
            Root = root,
            Items = items.ToList()
        };
    }

    private static Item Item(MongoId id, string template, MongoId? parentId = null)
    {
        return new Item
        {
            Id = id,
            Template = new MongoId(template),
            ParentId = parentId?.ToString(),
            SlotId = parentId.HasValue ? "hideout" : null
        };
    }
}
