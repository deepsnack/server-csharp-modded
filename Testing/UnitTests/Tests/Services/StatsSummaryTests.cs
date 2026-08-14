using NUnit.Framework;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;

namespace UnitTests.Tests.Services;

[TestFixture]
public class StatsSummaryTests
{
    [Test]
    public void FromOverallCounters_ExtractsSingleKeyCountersOnly()
    {
        var items = new List<CounterKeyValue>
        {
            new() { Key = ["Sessions", "Pmc"], Value = 40 },
            new() { Key = ["Kills"], Value = 635 },
            new() { Key = ["Deaths"], Value = 5 },
            new() { Key = ["AmmoUsed"], Value = 6771 },
            new() { Key = ["CauseBodyDamage"], Value = 7776095 },
            new() { Key = ["CauseArmorDamage"], Value = 5559188 },
            new() { Key = ["HeadShots"], Value = 467 },
            new() { Key = ["KilledBoss"], Value = 48 },
        };

        var summary = StatsSummary.FromOverallCounters(items);

        Assert.That(summary, Is.Not.Null);
        Assert.That(summary!.Kills, Is.EqualTo(635));
        Assert.That(summary.Deaths, Is.EqualTo(5));
        Assert.That(summary.AmmoUsed, Is.EqualTo(6771));
        Assert.That(summary.BodyDamage, Is.EqualTo(7776095));
        Assert.That(summary.ArmorDamage, Is.EqualTo(5559188));
        Assert.That(summary.Headshots, Is.EqualTo(467));
        Assert.That(summary.BossKills, Is.EqualTo(48));
    }

    [Test]
    public void FromOverallCounters_MissingKeysDefaultToZero()
    {
        var items = new List<CounterKeyValue>
        {
            new() { Key = ["Kills"], Value = 1 },
        };

        var summary = StatsSummary.FromOverallCounters(items);

        Assert.That(summary, Is.Not.Null);
        Assert.That(summary!.Kills, Is.EqualTo(1));
        Assert.That(summary.Deaths, Is.EqualTo(0));
        Assert.That(summary.AmmoUsed, Is.EqualTo(0));
        Assert.That(summary.BodyDamage, Is.EqualTo(0));
        Assert.That(summary.ArmorDamage, Is.EqualTo(0));
        Assert.That(summary.Headshots, Is.EqualTo(0));
        Assert.That(summary.BossKills, Is.EqualTo(0));
    }

    [Test]
    public void FromOverallCounters_NullOrEmptyReturnsNull()
    {
        Assert.That(StatsSummary.FromOverallCounters(null), Is.Null);
        Assert.That(StatsSummary.FromOverallCounters([]), Is.Null);
    }

    [Test]
    public void FromOverallCounters_MultiKeyEntriesAreNotCounted()
    {
        var items = new List<CounterKeyValue>
        {
            new() { Key = ["Kills", "Pmc"], Value = 999 },
            new() { Key = ["Kills", "Scav"], Value = 999 },
            new() { Key = ["Kills"], Value = 3 },
        };

        var summary = StatsSummary.FromOverallCounters(items);

        Assert.That(summary, Is.Not.Null);
        Assert.That(summary!.Kills, Is.EqualTo(3));
    }
}
