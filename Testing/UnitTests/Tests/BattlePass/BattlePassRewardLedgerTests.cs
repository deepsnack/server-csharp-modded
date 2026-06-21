using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassRewardLedgerTests
{
    [Test]
    public void InitializeBaseline_DoesNotTreatExistingRewardsAsCompensation()
    {
        var progress = new BpProgress { ClaimedFree = [1] };
        var tracks = new Dictionary<int, BpLevelRewards>
        {
            [1] = new() { Free = [Item("aaaaaaaaaaaaaaaaaaaaaaaa", 2)] },
        };

        BattlePassRewardLedger.InitializeBaseline(progress, tracks);

        Assert.Multiple(() =>
        {
            Assert.That(progress.RewardLedgerInitialized, Is.True);
            Assert.That(BattlePassRewardLedger.GetPending(progress, tracks), Is.Empty);
        });
    }

    [Test]
    public void GetPending_AggregatesNewRewardsAcrossAllClaimedTracks()
    {
        var progress = new BpProgress
        {
            ClaimedFree = [1],
            ClaimedPremium = [2],
        };
        var tracks = new Dictionary<int, BpLevelRewards>
        {
            [1] = new() { Free = [Item("aaaaaaaaaaaaaaaaaaaaaaaa")] },
            [2] = new() { Premium = [new BpReward { Type = "title", TitleId = "veteran" }] },
            [3] = new(),
        };
        BattlePassRewardLedger.InitializeBaseline(progress, tracks);

        tracks[1].Free.Add(Item("bbbbbbbbbbbbbbbbbbbbbbbb", 3));
        tracks[2].Premium.Add(new BpReward { Type = "recipe", RecipeId = "cccccccccccccccccccccccc" });
        tracks[3].Free.Add(Item("dddddddddddddddddddddddd"));

        var pending = BattlePassRewardLedger.GetPending(progress, tracks);

        Assert.That(pending, Has.Count.EqualTo(2));
        Assert.That(pending.Select(x => x.TrackKey), Is.EquivalentTo(new[] { "level:1:free", "level:2:premium" }));

        BattlePassRewardLedger.RecordPending(progress, pending);
        Assert.That(BattlePassRewardLedger.GetPending(progress, tracks), Is.Empty);
    }

    [Test]
    public void GetPending_DeduplicatesSameRewardAndIgnoresDisplayOrCountChanges()
    {
        var progress = new BpProgress { ClaimedFree = [1] };
        var original = Item("aaaaaaaaaaaaaaaaaaaaaaaa", 1);
        var tracks = new Dictionary<int, BpLevelRewards>
        {
            [1] = new() { Free = [original] },
        };
        BattlePassRewardLedger.InitializeBaseline(progress, tracks);

        original.Count = 99;
        original.Name = "管理员改名";
        original.Featured = true;
        tracks[1].Free.Add(Item("aaaaaaaaaaaaaaaaaaaaaaaa", 5));

        Assert.That(BattlePassRewardLedger.GetPending(progress, tracks), Is.Empty);
    }

    private static BpReward Item(string tpl, int count = 1) => new()
    {
        Type = "item",
        Tpl = tpl,
        Count = count,
    };
}
