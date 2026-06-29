using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Models.Common;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>抽奖奖池查询、状态判断、发布校验和概率计算。</summary>
[Injectable]
public class LotteryService(ItemSearchService itemSearchService)
{
    public List<BpLotteryPool> GetPools()
    {
        return BattlePassStore.GetLotteryPools();
    }

    public BpLotteryPool? GetPool(string poolId)
    {
        return BattlePassStore
            .GetLotteryPools()
            .FirstOrDefault(p => string.Equals(p.Id, poolId, StringComparison.OrdinalIgnoreCase));
    }

    public (long startUtc, long endUtc) GetPoolWindow(BpLotteryPool pool)
    {
        if (!pool.FollowSeason)
        {
            return (pool.StartUtc, pool.EndUtc);
        }

        var season = BattlePassStore.GetSeason();
        return (season.StartUtc, season.EndUtc);
    }

    public bool IsVisibleToPlayer(BpLotteryPool pool, long nowUtc)
    {
        if (!pool.Enabled || !pool.VisibleToPlayers)
        {
            return false;
        }

        if (string.Equals(pool.Status, BpLotteryConstants.PoolStatusArchived, StringComparison.OrdinalIgnoreCase)
            || string.Equals(pool.Status, BpLotteryConstants.PoolStatusDraft, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(pool.Status, BpLotteryConstants.PoolStatusPaused, StringComparison.OrdinalIgnoreCase))
        {
            return pool.PauseVisibleToPlayers;
        }

        return true;
    }

    public (bool ok, string message) CanDraw(BpLotteryPool pool, long nowUtc)
    {
        if (!pool.Enabled)
        {
            return (false, "奖池未启用");
        }

        var scheduled = string.Equals(pool.Status, BpLotteryConstants.PoolStatusScheduled, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(pool.Status, BpLotteryConstants.PoolStatusActive, StringComparison.OrdinalIgnoreCase) && !scheduled)
        {
            return (false, "奖池当前不可抽取");
        }

        var (startUtc, endUtc) = GetPoolWindow(pool);
        if (startUtc > 0 && nowUtc < startUtc)
        {
            return (false, "奖池尚未开始");
        }

        if (endUtc > 0 && nowUtc >= endUtc)
        {
            return (false, "奖池已结束");
        }

        return (true, "");
    }

    public List<string> ValidateForPublish(BpLotteryPool pool)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(pool.Id))
        {
            errors.Add("奖池 ID 不能为空");
        }

        if (string.IsNullOrWhiteSpace(pool.Name))
        {
            errors.Add("奖池名称不能为空");
        }

        var poolType = NormalizePoolType(pool.PoolType);
        if (poolType is null)
        {
            errors.Add("奖池类型无效");
        }

        var probabilityMode = NormalizeProbabilityMode(pool.ProbabilityMode);
        if (probabilityMode is null)
        {
            errors.Add("概率模式无效");
        }

        var costType = NormalizeCostType(pool.CostType);
        if (costType is null)
        {
            errors.Add("代价类型无效");
        }

        if (!pool.FollowSeason)
        {
            if (pool.StartUtc <= 0 || pool.EndUtc <= 0 || pool.EndUtc <= pool.StartUtc)
            {
                errors.Add("不跟随赛季的奖池必须配置合法的开始和结束时间");
            }
        }

        var validPrizes = pool.Prizes.Where(IsPrizeValid).ToList();
        if (validPrizes.Count == 0)
        {
            errors.Add("至少需要配置一个有效奖项");
        }

        if (string.Equals(probabilityMode, BpLotteryConstants.ProbabilityWeight, StringComparison.OrdinalIgnoreCase))
        {
            if (validPrizes.Any(p => p.Weight <= 0))
            {
                errors.Add("权重模式下所有有效奖项权重必须大于 0");
            }

            if (validPrizes.Sum(p => Math.Max(0, p.Weight)) <= 0)
            {
                errors.Add("权重模式下奖项总权重必须大于 0");
            }
        }

        if (costType is not null)
        {
            ValidateCost(pool.SingleCost, costType, "单抽代价", errors);
        }

        if (string.Equals(poolType, BpLotteryConstants.PoolTypeNonRepeatable, StringComparison.OrdinalIgnoreCase))
        {
            if (pool.StepCosts.Count != validPrizes.Count)
            {
                errors.Add("不可重复奖池必须为每一抽完整配置阶梯代价");
            }

            var drawNumbers = pool.StepCosts.Select(s => s.DrawNumber).OrderBy(x => x).ToList();
            for (var i = 1; i <= validPrizes.Count; i++)
            {
                if (!drawNumbers.Contains(i))
                {
                    errors.Add($"不可重复奖池缺少第 {i} 抽代价");
                }
            }

            foreach (var step in pool.StepCosts)
            {
                if (costType is not null)
                {
                    ValidateCost(step.Cost, costType, $"第 {step.DrawNumber} 抽代价", errors);
                }
            }
        }

        if (string.Equals(poolType, BpLotteryConstants.PoolTypeRepeatable, StringComparison.OrdinalIgnoreCase)
            && pool.PityEnabled)
        {
            if (pool.PityCount < 2)
            {
                errors.Add("保底次数不能小于 2");
            }

            if (validPrizes.All(p => !p.IsGrandPrize))
            {
                errors.Add("开启保底时至少需要一个大奖");
            }
        }

        if (pool.TenDrawCostOverride is not null)
        {
            if (!string.Equals(costType, BpLotteryConstants.CostLotteryTickets, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("十连折扣价仅支持抽奖券代价");
            }
            else
            {
                ValidateCost(pool.TenDrawCostOverride, BpLotteryConstants.CostLotteryTickets, "十连折扣价", errors);
            }
        }

        return errors.Distinct().ToList();
    }

    public Dictionary<string, decimal> CalculateProbabilities(BpLotteryPool pool, IEnumerable<BpLotteryPrize> candidates)
    {
        var list = candidates.Where(IsPrizeValid).ToList();
        if (list.Count == 0)
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        if (string.Equals(pool.ProbabilityMode, BpLotteryConstants.ProbabilityWeight, StringComparison.OrdinalIgnoreCase))
        {
            var total = list.Sum(p => Math.Max(0, p.Weight));
            if (total <= 0)
            {
                return list.ToDictionary(p => p.Id, _ => 0m, StringComparer.OrdinalIgnoreCase);
            }

            return list.ToDictionary(
                p => p.Id,
                p => (decimal)Math.Max(0, p.Weight) / total,
                StringComparer.OrdinalIgnoreCase
            );
        }

        var equal = 1m / list.Count;
        return list.ToDictionary(p => p.Id, _ => equal, StringComparer.OrdinalIgnoreCase);
    }

    public List<BpLotteryPrize> GetCandidatePrizes(BpLotteryPool pool, BpLotteryPoolProgress progress)
    {
        var prizes = pool.Prizes.Where(IsPrizeValid);
        if (string.Equals(pool.PoolType, BpLotteryConstants.PoolTypeNonRepeatable, StringComparison.OrdinalIgnoreCase))
        {
            prizes = prizes.Where(p => !progress.DrawnPrizeIds.Contains(p.Id));
        }

        return prizes.ToList();
    }

    public string ResolvePrizeName(BpLotteryPrize prize)
    {
        var reward = prize.Reward;
        if (!string.IsNullOrWhiteSpace(reward.Name))
        {
            return reward.Name!;
        }

        var type = (reward.Type ?? "item").Trim().ToLowerInvariant();
        if (type == "item" && !string.IsNullOrWhiteSpace(reward.Tpl) && MongoId.IsValidMongoId(reward.Tpl))
        {
            var resolved = itemSearchService.ResolveItemNameZh(new MongoId(reward.Tpl));
            return string.IsNullOrWhiteSpace(resolved) ? reward.Tpl! : resolved;
        }

        return type switch
        {
            "purchaseright" => reward.OfferId ?? prize.Id,
            "recipe" => reward.RecipeId ?? prize.Id,
            "title" => reward.TitleId ?? prize.Id,
            "clothing" => reward.SuitId ?? prize.Id,
            _ => prize.Id,
        };
    }

    private static string? NormalizePoolType(string? value)
    {
        return value?.Trim() switch
        {
            BpLotteryConstants.PoolTypeRepeatable => BpLotteryConstants.PoolTypeRepeatable,
            BpLotteryConstants.PoolTypeNonRepeatable => BpLotteryConstants.PoolTypeNonRepeatable,
            _ => null,
        };
    }

    private static string? NormalizeProbabilityMode(string? value)
    {
        return value?.Trim() switch
        {
            BpLotteryConstants.ProbabilityEqual => BpLotteryConstants.ProbabilityEqual,
            BpLotteryConstants.ProbabilityWeight => BpLotteryConstants.ProbabilityWeight,
            _ => null,
        };
    }

    private static string? NormalizeCostType(string? value)
    {
        return value?.Trim() switch
        {
            BpLotteryConstants.CostStashItems => BpLotteryConstants.CostStashItems,
            BpLotteryConstants.CostLotteryTickets => BpLotteryConstants.CostLotteryTickets,
            _ => null,
        };
    }

    private static bool IsPrizeValid(BpLotteryPrize prize)
    {
        if (string.IsNullOrWhiteSpace(prize.Id))
        {
            return false;
        }

        var reward = prize.Reward;
        return (reward.Type ?? "item").Trim().ToLowerInvariant() switch
        {
            "item" => !string.IsNullOrWhiteSpace(reward.Tpl) && reward.Count > 0,
            "purchaseright" => !string.IsNullOrWhiteSpace(reward.OfferId),
            "recipe" => !string.IsNullOrWhiteSpace(reward.RecipeId),
            "title" => !string.IsNullOrWhiteSpace(reward.TitleId),
            "clothing" => !string.IsNullOrWhiteSpace(reward.SuitId),
            _ => false,
        };
    }

    private static void ValidateCost(BpLotteryCost? cost, string expectedCostType, string label, List<string> errors)
    {
        if (cost is null)
        {
            errors.Add($"{label}不能为空");
            return;
        }

        cost.CostType = expectedCostType;
        if (string.Equals(expectedCostType, BpLotteryConstants.CostLotteryTickets, StringComparison.OrdinalIgnoreCase))
        {
            if (cost.TicketAmount <= 0)
            {
                errors.Add($"{label}抽奖券数量必须大于 0");
            }

            return;
        }

        if (!BattlePassShopService.TryAggregateCosts(cost.StashItems, out _))
        {
            errors.Add($"{label}仓库物品代价配置无效");
        }
    }
}
