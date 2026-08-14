using NUnit.Framework;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Utils;

namespace UnitTests.Tests.Callbacks;

[TestFixture]
public class RagfairCallbacksTests
{
    [TestCase(false, false, false)]
    [TestCase(true, false, true)]
    [TestCase(false, true, true)]
    [TestCase(true, true, true)]
    public void BackgroundUpdatesInvalidateOnlyWhenOffersChanged(
        bool playerOffersChanged,
        bool serverOffersChanged,
        bool expectedInvalidation
    )
    {
        var playerUpdateCalls = 0;
        var serverUpdateCalls = 0;
        var invalidations = 0;

        var changed = RagfairCallbacks.RunOfferUpdatesAndInvalidateIfChanged(
            () =>
            {
                playerUpdateCalls++;
                return playerOffersChanged;
            },
            () =>
            {
                serverUpdateCalls++;
                return serverOffersChanged;
            },
            () => invalidations++
        );

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(expectedInvalidation));
            Assert.That(playerUpdateCalls, Is.EqualTo(1));
            Assert.That(serverUpdateCalls, Is.EqualTo(1));
            Assert.That(invalidations, Is.EqualTo(expectedInvalidation ? 1 : 0));
        });
    }

    [Test]
    public void UnchangedBackgroundTickKeepsFleaResponseHot()
    {
        var cache = new RequestCoalescingCache(TimeSpan.FromMinutes(1));
        var computeCalls = 0;
        const string cacheKey = "flea:search:profile:request";

        cache.GetOrCompute(cacheKey, () => $"response-{++computeCalls}");

        RagfairCallbacks.RunOfferUpdatesAndInvalidateIfChanged(
            () => false,
            () => false,
            () => cache.InvalidateBucket("flea")
        );

        var response = cache.GetOrCompute(cacheKey, () => $"response-{++computeCalls}");

        Assert.Multiple(() =>
        {
            Assert.That(response, Is.EqualTo("response-1"));
            Assert.That(computeCalls, Is.EqualTo(1));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LegacyUpdateRunsBeforeMutationStateIsConsumed(bool expectedChanged)
    {
        var calls = new List<string>();

        var changed = RagfairCallbacks.RunLegacyUpdateAndConsumeChanges(
            () => calls.Add("update"),
            () =>
            {
                calls.Add("consume");
                return expectedChanged;
            }
        );

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(expectedChanged));
            Assert.That(calls, Is.EqualTo(new[] { "update", "consume" }));
        });
    }

    [Test]
    public void SearchCacheNormalizationIgnoresOnlyRefreshMetadata()
    {
        var request = new SearchRequestData
        {
            Page = 3,
            Limit = 15,
            PriceFrom = 1000,
            UpdateOfferCount = true,
            Tm = 123456,
            Reload = 7,
        };

        var normalized = RagfairCallbacks.NormalizeSearchRequestForCache(request);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.Tm, Is.Null);
            Assert.That(normalized.Reload, Is.Null);
            Assert.That(normalized.Page, Is.EqualTo(request.Page));
            Assert.That(normalized.Limit, Is.EqualTo(request.Limit));
            Assert.That(normalized.PriceFrom, Is.EqualTo(request.PriceFrom));
            Assert.That(normalized.UpdateOfferCount, Is.EqualTo(request.UpdateOfferCount));
            Assert.That(request.Tm, Is.EqualTo(123456));
            Assert.That(request.Reload, Is.EqualTo(7));
        });
    }
}
