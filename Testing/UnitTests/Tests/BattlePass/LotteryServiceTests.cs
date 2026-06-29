using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class LotteryServiceTests
{
    private readonly LotteryService _service = new(null!);

    [Test]
    public void CalculateProbabilities_UsesEqualShareWhenConfigured()
    {
        var pool = new BpLotteryPool
        {
            ProbabilityMode = BpLotteryConstants.ProbabilityEqual,
            Prizes = [Prize("a"), Prize("b"), Prize("c")],
        };

        var probabilities = _service.CalculateProbabilities(pool, pool.Prizes);

        Assert.Multiple(() =>
        {
            Assert.That(probabilities, Has.Count.EqualTo(3));
            Assert.That(probabilities["a"], Is.EqualTo(1m / 3m));
            Assert.That(probabilities["b"], Is.EqualTo(1m / 3m));
            Assert.That(probabilities["c"], Is.EqualTo(1m / 3m));
        });
    }

    [Test]
    public void CalculateProbabilities_UsesWeightsWhenConfigured()
    {
        var pool = new BpLotteryPool
        {
            ProbabilityMode = BpLotteryConstants.ProbabilityWeight,
            Prizes = [Prize("small", weight: 1), Prize("grand", weight: 3, grand: true)],
        };

        var probabilities = _service.CalculateProbabilities(pool, pool.Prizes);

        Assert.Multiple(() =>
        {
            Assert.That(probabilities["small"], Is.EqualTo(0.25m));
            Assert.That(probabilities["grand"], Is.EqualTo(0.75m));
        });
    }

    [Test]
    public void GetCandidatePrizes_ExcludesDrawnPrizesForNonRepeatablePool()
    {
        var pool = new BpLotteryPool
        {
            PoolType = BpLotteryConstants.PoolTypeNonRepeatable,
            Prizes = [Prize("a"), Prize("b"), Prize("c")],
        };
        var progress = new BpLotteryPoolProgress
        {
            DrawnPrizeIds = ["b"],
        };

        var candidates = _service.GetCandidatePrizes(pool, progress);

        Assert.That(candidates.Select(x => x.Id), Is.EquivalentTo(new[] { "a", "c" }));
    }

    [Test]
    public void GetCandidatePrizes_AcceptsClothingRewardsWithSuitId()
    {
        var pool = new BpLotteryPool
        {
            Prizes =
            [
                new BpLotteryPrize
                {
                    Id = "valid_clothing",
                    Weight = 1,
                    Reward = new BpReward { Type = "clothing", SuitId = "aaaaaaaaaaaaaaaaaaaaaaaa" },
                },
                new BpLotteryPrize
                {
                    Id = "missing_suit",
                    Weight = 1,
                    Reward = new BpReward { Type = "clothing" },
                },
            ],
        };

        var candidates = _service.GetCandidatePrizes(pool, new BpLotteryPoolProgress());

        Assert.That(candidates.Select(x => x.Id), Is.EqualTo(new[] { "valid_clothing" }));
    }

    private static BpLotteryPrize Prize(string id, int weight = 1, bool grand = false) => new()
    {
        Id = id,
        Weight = weight,
        IsGrandPrize = grand,
        Reward = new BpReward
        {
            Type = "item",
            Tpl = "aaaaaaaaaaaaaaaaaaaaaaaa",
            Count = 1,
        },
    };
}
