using System.Reflection;
using NUnit.Framework;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace UnitTests.Tests.Services;

[TestFixture]
public class FleaTraderCacheServiceTests
{
    /// <summary>测试替身：注入已知 TTL 的双缓存并跳过 ConfigServer 读取。</summary>
    private sealed class TestableFleaTraderCacheService : FleaTraderCacheService
    {
        public TestableFleaTraderCacheService()
            : base(null!)
        {
        }

        public readonly RequestCoalescingCache Flea = new(TimeSpan.FromMinutes(5));
        public readonly RequestCoalescingCache Trader = new(TimeSpan.FromMinutes(1));

        public override bool Enabled => true;
        protected override RequestCoalescingCache FleaCache => Flea;
    }

    [Test]
    public void GetOrCompute_DispatchesFleaAndTraderKeysToSeparateCaches()
    {
        var service = new TestableFleaTraderCacheService();
        var fleaCalls = 0;
        var traderCalls = 0;

        var flea1 = service.GetOrCompute("flea:search:p1:req", () => $"flea-{++fleaCalls}");
        var flea2 = service.GetOrCompute("flea:search:p1:req", () => $"flea-{++fleaCalls}");
        var trader1 = service.GetOrCompute("trader:p1:settings", () => $"trader-{++traderCalls}");
        var trader2 = service.GetOrCompute("trader:p1:settings", () => $"trader-{++traderCalls}");

        Assert.Multiple(() =>
        {
            Assert.That(flea2, Is.EqualTo(flea1));
            Assert.That(trader2, Is.EqualTo(trader1));
            Assert.That(fleaCalls, Is.EqualTo(1));
            Assert.That(traderCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void InvalidateFlea_ClearsOnlyFleaBucket()
    {
        var service = new TestableFleaTraderCacheService();
        var searchCalls = 0;
        var priceCalls = 0;
        var traderCalls = 0;

        service.GetOrCompute("flea:search:p1:req", () => $"flea-{++searchCalls}");
        service.GetOrCompute("flea:prices", () => $"prices-{++priceCalls}");
        service.GetOrCompute("trader:p1:settings", () => $"trader-{++traderCalls}");

        service.InvalidateFlea();

        Assert.Multiple(() =>
        {
            Assert.That(service.GetOrCompute("flea:search:p1:req", () => $"flea-{++searchCalls}"), Is.EqualTo("flea-2"));
            Assert.That(service.GetOrCompute("flea:prices", () => $"prices-{++priceCalls}"), Is.EqualTo("prices-2"));
            Assert.That(service.GetOrCompute("trader:p1:settings", () => $"trader-{++traderCalls}"), Is.EqualTo("trader-1"));
        });
    }

    [Test]
    public void InvalidateFleaSearch_ClearsSearchButKeepsGlobalPriceEndpoints()
    {
        var service = new TestableFleaTraderCacheService();
        var searchACalls = 0;
        var searchBCalls = 0;
        var priceCalls = 0;
        var marketPriceCalls = 0;

        service.GetOrCompute("flea:search:p1:req-a", () => $"search-a-{++searchACalls}");
        service.GetOrCompute("flea:search:p1:req-b", () => $"search-b-{++searchBCalls}");
        service.GetOrCompute("flea:prices", () => $"prices-{++priceCalls}");
        service.GetOrCompute("flea:mprice:req", () => $"mprice-{++marketPriceCalls}");

        service.InvalidateFleaSearch();

        Assert.Multiple(() =>
        {
            Assert.That(service.GetOrCompute("flea:search:p1:req-a", () => $"search-a-{++searchACalls}"), Is.EqualTo("search-a-2"));
            Assert.That(service.GetOrCompute("flea:search:p1:req-b", () => $"search-b-{++searchBCalls}"), Is.EqualTo("search-b-2"));
            Assert.That(service.GetOrCompute("flea:prices", () => $"prices-{++priceCalls}"), Is.EqualTo("prices-1"));
            Assert.That(service.GetOrCompute("flea:mprice:req", () => $"mprice-{++marketPriceCalls}"), Is.EqualTo("mprice-1"));
        });
    }

    [Test]
    public void InvalidateTrader_ClearsOnlyTraderBucket()
    {
        var service = new TestableFleaTraderCacheService();
        var fleaCalls = 0;
        var traderCalls = 0;

        service.GetOrCompute("flea:prices", () => $"prices-{++fleaCalls}");
        service.GetOrCompute("trader:p1:settings", () => $"trader-{++traderCalls}");

        service.InvalidateTrader();

        Assert.Multiple(() =>
        {
            Assert.That(service.GetOrCompute("flea:prices", () => $"prices-{++fleaCalls}"), Is.EqualTo("prices-1"));
            Assert.That(service.GetOrCompute("trader:p1:settings", () => $"trader-{++traderCalls}"), Is.EqualTo("trader-2"));
        });
    }

    [Test]
    public void InvalidateTraderForSession_ClearsOnlyThatSessionsTraderCache()
    {
        var service = new TestableFleaTraderCacheService();
        var sessionACalls = 0;
        var sessionBCalls = 0;
        var fleaCalls = 0;

        service.GetOrCompute("trader:a:settings", () => $"a-{++sessionACalls}");
        service.GetOrCompute("trader:b:settings", () => $"b-{++sessionBCalls}");
        service.GetOrCompute("flea:prices", () => $"prices-{++fleaCalls}");

        service.InvalidateTraderForSession("a");

        Assert.Multiple(() =>
        {
            Assert.That(service.GetOrCompute("trader:a:settings", () => $"a-{++sessionACalls}"), Is.EqualTo("a-2"));
            Assert.That(service.GetOrCompute("trader:b:settings", () => $"b-{++sessionBCalls}"), Is.EqualTo("b-1"));
            Assert.That(service.GetOrCompute("flea:prices", () => $"prices-{++fleaCalls}"), Is.EqualTo("prices-1"));
        });
    }

    [Test]
    public void ResolveFleaTtl_UsesConfiguredValueWhenPositive()
    {
        Assert.That(FleaTraderCacheService.ResolveFleaTtl(600), Is.EqualTo(TimeSpan.FromSeconds(600)));
    }

    [Test]
    public void ResolveFleaTtl_FallsBackToDefaultWhenMissingOrNonPositive()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FleaTraderCacheService.ResolveFleaTtl(null), Is.EqualTo(TimeSpan.FromSeconds(300)));
            Assert.That(FleaTraderCacheService.ResolveFleaTtl(0), Is.EqualTo(TimeSpan.FromSeconds(300)));
            Assert.That(FleaTraderCacheService.ResolveFleaTtl(-5), Is.EqualTo(TimeSpan.FromSeconds(300)));
        });
    }

    [Test]
    public void DefaultTraderCacheTtl_RemainsSixtySeconds()
    {
        var service = new FleaTraderCacheService(null!);
        var traderCache = (RequestCoalescingCache)typeof(FleaTraderCacheService)
            .GetField("traderCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var ttlTicks = (long)typeof(RequestCoalescingCache)
            .GetField("_ttlTicks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(traderCache)!;

        Assert.That(ttlTicks, Is.EqualTo(TimeSpan.FromSeconds(60).Ticks));
    }
}
