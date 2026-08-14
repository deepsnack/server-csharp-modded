using NUnit.Framework;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.Services;

[TestFixture]
public class LazyProfileRagfairRecoveryTests
{
    [Test]
    public void OrderProfileFilesForLoading_PutsMongoIdFilesBeforeUsernameFilesDeterministically()
    {
        var files = new[]
        {
            Path.Combine("profiles", "Zulu.json"),
            Path.Combine("profiles", "ffffffffffffffffffffffff.json"),
            Path.Combine("profiles", "alpha.json"),
            Path.Combine("profiles", "000000000000000000000000.json"),
        };

        var ordered = SaveServer.OrderProfileFilesForLoading(files);

        Assert.That(
            ordered.Select(Path.GetFileName),
            Is.EqualTo(
                new[]
                {
                    "000000000000000000000000.json",
                    "ffffffffffffffffffffffff.json",
                    "alpha.json",
                    "Zulu.json",
                }
            )
        );
    }

    [Test]
    public void RestorePlayerOffersForProfiles_IsolatesLoadAndAddFailuresAndContinues()
    {
        var firstId = new MongoId("000000000000000000000001");
        var loadFailureId = new MongoId("000000000000000000000002");
        var addFailureId = new MongoId("000000000000000000000003");
        var finalId = new MongoId("000000000000000000000004");
        var emptyId = new MongoId("000000000000000000000005");
        var addFailureOfferId = new MongoId("100000000000000000000003");
        var addedOffers = new List<RagfairOffer>();

        var offersByProfile = new Dictionary<MongoId, List<RagfairOffer>>
        {
            [firstId] = [new RagfairOffer { Id = new MongoId("100000000000000000000001") }],
            [addFailureId] = [new RagfairOffer { Id = addFailureOfferId }],
            [finalId] = [new RagfairOffer { Id = new MongoId("100000000000000000000004") }],
            [emptyId] = [],
        };

        var summary = RagfairOfferService.RestorePlayerOffersForProfiles(
            [firstId, loadFailureId, addFailureId, finalId, emptyId],
            sessionId =>
            {
                if (sessionId == loadFailureId)
                {
                    throw new InvalidOperationException("broken profile");
                }

                return offersByProfile[sessionId];
            },
            offers =>
            {
                var materializedOffers = offers.ToList();
                if (materializedOffers.Any(offer => offer.Id == addFailureOfferId))
                {
                    throw new InvalidOperationException("broken offer");
                }

                addedOffers.AddRange(materializedOffers);
            }
        );

        Assert.Multiple(() =>
        {
            Assert.That(summary.RestoredProfiles, Is.EqualTo(2));
            Assert.That(summary.RestoredOffers, Is.EqualTo(2));
            Assert.That(summary.FailedProfiles, Is.EqualTo(2));
            Assert.That(addedOffers.Select(offer => offer.Id), Is.EqualTo(new[] { offersByProfile[firstId][0].Id, offersByProfile[finalId][0].Id }));
            Assert.That(offersByProfile.Values.SelectMany(offers => offers), Has.All.Property(nameof(RagfairOffer.CreatedBy)).EqualTo(OfferCreator.Player));
        });
    }
}
