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

    [Test]
    public void GetPending_TracksLotteryResourceRewardsByResourceIdentity()
    {
        var progress = new BpProgress { ClaimedFree = [1] };
        var tracks = new Dictionary<int, BpLevelRewards>
        {
            [1] = new()
            {
                Free =
                [
                    new BpReward { Type = "lotteryGlobalTickets", Count = 1 },
                    new BpReward { Type = "lotteryPoolTickets", PoolId = "pool_alpha", Count = 1 },
                    new BpReward { Type = "lotteryExchangeCoins", Count = 10 },
                ],
            },
        };
        BattlePassRewardLedger.InitializeBaseline(progress, tracks);

        tracks[1].Free.Add(new BpReward { Type = "lotteryGlobalTickets", Count = 99 });
        tracks[1].Free.Add(new BpReward { Type = "lotteryPoolTickets", PoolId = "pool_alpha", Count = 5 });
        tracks[1].Free.Add(new BpReward { Type = "lotteryPoolTickets", PoolId = "pool_beta", Count = 5 });

        var pending = BattlePassRewardLedger.GetPending(progress, tracks);

        Assert.That(pending, Has.Count.EqualTo(1));
        Assert.That(pending.Single().Reward.PoolId, Is.EqualTo("pool_beta"));
    }

    [Test]
    public void GetPending_TracksClothingRewardsBySuitId()
    {
        var progress = new BpProgress { ClaimedFree = [1] };
        var tracks = new Dictionary<int, BpLevelRewards>
        {
            [1] = new() { Free = [new BpReward { Type = "clothing", SuitId = "aaaaaaaaaaaaaaaaaaaaaaaa" }] },
        };
        BattlePassRewardLedger.InitializeBaseline(progress, tracks);

        tracks[1].Free.Add(new BpReward { Type = "clothing", SuitId = "aaaaaaaaaaaaaaaaaaaaaaaa", Name = "重命名服装" });
        tracks[1].Free.Add(new BpReward { Type = "clothing", SuitId = "bbbbbbbbbbbbbbbbbbbbbbbb" });

        var pending = BattlePassRewardLedger.GetPending(progress, tracks);

        Assert.That(pending, Has.Count.EqualTo(1));
        Assert.That(pending.Single().Reward.SuitId, Is.EqualTo("bbbbbbbbbbbbbbbbbbbbbbbb"));
    }

    private static BpReward Item(string tpl, int count = 1) => new()
    {
        Type = "item",
        Tpl = tpl,
        Count = count,
    };
}
