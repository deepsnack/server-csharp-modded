using NUnit.Framework;
using SPTarkov.Server.Core.Utils;

namespace UnitTests.Tests.Utils;

[TestFixture]
public class RequestCoalescingCacheTests
{
    [Test]
    public void RepeatedKeyReusesComputedResponse()
    {
        var cache = new RequestCoalescingCache(TimeSpan.FromMinutes(1));
        var computeCalls = 0;

        var first = cache.GetOrCompute("flea:search:profile:request", () =>
        {
            computeCalls++;
            return "response-body";
        });
        var second = cache.GetOrCompute("flea:search:profile:request", () =>
        {
            computeCalls++;
            return "unexpected";
        });

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo("response-body"));
            Assert.That(second, Is.EqualTo("response-body"));
            Assert.That(computeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void InvalidatingFleaBucketKeepsTraderResponsesHot()
    {
        var cache = new RequestCoalescingCache(TimeSpan.FromMinutes(1));
        var fleaComputeCalls = 0;
        var traderComputeCalls = 0;

        cache.GetOrCompute("flea:search:profile:request", () => $"flea-{++fleaComputeCalls}");
        cache.GetOrCompute("trader:profile:assort:id", () => $"trader-{++traderComputeCalls}");

        cache.InvalidateBucket("flea");

        var flea = cache.GetOrCompute("flea:search:profile:request", () => $"flea-{++fleaComputeCalls}");
        var trader = cache.GetOrCompute("trader:profile:assort:id", () => $"trader-{++traderComputeCalls}");

        Assert.Multiple(() =>
        {
            Assert.That(flea, Is.EqualTo("flea-2"));
            Assert.That(trader, Is.EqualTo("trader-1"));
            Assert.That(fleaComputeCalls, Is.EqualTo(2));
            Assert.That(traderComputeCalls, Is.EqualTo(1));
        });
    }
}
