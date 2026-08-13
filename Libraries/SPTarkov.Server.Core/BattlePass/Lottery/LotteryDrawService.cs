using System.Collections.Concurrent;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>抽奖核心流程：幂等、并发锁、扣费、抽取、发奖、记录。</summary>
[Injectable(InjectionType.Singleton)]
public class LotteryDrawService(
    LotteryService lotteryService,
    LotteryWalletService walletService,
    BattlePassService battlePassService,
    BattlePassStashService stashService,
    BattlePassRewardService rewardService,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    ItemControl.ItemSearchService itemSearchService,
    ISptLogger<LotteryDrawService> logger
)
{
    private static readonly ConcurrentDictionary<string, object> DrawLocks = new();

    public BpLotteryDrawOutcome DrawOnce(string profileId, string poolId, string? requestId)
    {
        return Draw(profileId, poolId, requestId, BpLotteryConstants.DrawOnce, 1);
    }

    public BpLotteryDrawOutcome DrawTen(string profileId, string poolId, string? requestId)
    {
        return Draw(profileId, poolId, requestId, BpLotteryConstants.DrawTen, 10);
    }

    private BpLotteryDrawOutcome Draw(string profileId, string poolId, string? requestId, string action, int drawCount)
    {
        requestId = (requestId ?? "").Trim();
        if (requestId.Length == 0)
        {
            return BpLotteryDrawOutcome.Fail("缺少 requestId");
        }

        var lockKey = $"{profileId}:{poolId}".ToLowerInvariant();
        var gate = DrawLocks.GetOrAdd(lockKey, _ => new object());
        lock (gate)
        {
            var existing = BattlePassStore.FindLotteryTransaction(profileId, requestId, action);
            if (existing is not null && string.Equals(existing.Status, BpLotteryConstants.TransactionCommitted, StringComparison.OrdinalIgnoreCase))
            {
                return ExistingCommitted(profileId, poolId, requestId, action, "重复请求，已返回首次抽取结果");
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tx = new BpLotteryTransaction
            {
                Id = Guid.NewGuid().ToString("N"),
                ProfileId = profileId,
                PoolId = poolId,
                RequestId = requestId,
                Action = action,
                Status = BpLotteryConstants.TransactionPending,
                CreatedUtc = now,
                UpdatedUtc = now,
                ExpiresUtc = now + 24 * 3600,
            };
            BattlePassStore.UpsertLotteryTransaction(tx);

            try
            {
                var settings = BattlePassStore.GetLotterySettings();
                if (!settings.Enabled)
                {
                    return FailTransaction(tx, "抽奖模块未启用");
                }

                var pool = lotteryService.GetPool(poolId);
                if (pool is null)
                {
                    return FailTransaction(tx, "奖池不存在");
                }

                var canDraw = lotteryService.CanDraw(pool, now);
                if (!canDraw.ok)
                {
                    return FailTransaction(tx, canDraw.message);
                }

                if (drawCount == 10 && !string.Equals(pool.PoolType, BpLotteryConstants.PoolTypeRepeatable, StringComparison.OrdinalIgnoreCase))
                {
                    return FailTransaction(tx, "不可重复奖池不支持十连抽");
                }

                var publishErrors = lotteryService.ValidateForPublish(pool);
                if (publishErrors.Count > 0)
                {
                    return FailTransaction(tx, "奖池配置无效：" + string.Join("；", publishErrors));
                }

                var season = BattlePassStore.GetSeason();
                var bpProgress = battlePassService.GetOrResetProgress(profileId, season);
                var lotteryProgress = BattlePassStore.GetLotteryPoolProgress(profileId, pool.Id);

                var plans = PlanDraws(profileId, pool, lotteryProgress, bpProgress, drawCount);
                if (!plans.ok)
                {
                    return FailTransaction(tx, plans.message);
                }

                var cost = BuildCost(pool, lotteryProgress, drawCount);
                if (!cost.ok)
                {
                    return FailTransaction(tx, cost.message);
                }

                var deducted = TryDeductCost(profileId, pool.Id, cost.cost!);
                if (!deducted.ok)
                {
                    return FailTransaction(tx, deducted.message);
                }

                var records = CommitDraws(profileId, pool, lotteryProgress, bpProgress, requestId, action, plans.plans, deducted.snapshot);
                BattlePassStore.SaveProgress(profileId, bpProgress);
                BattlePassStore.SaveLotteryPoolProgress(profileId, lotteryProgress);
                BattlePassStore.AppendLotteryDrawRecords(records);

                tx.Status = BpLotteryConstants.TransactionCommitted;
                tx.Message = "ok";
                tx.ResultSnapshot = JsonSerializer.Serialize(records.Select(ToRecordView));
                tx.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                BattlePassStore.UpsertLotteryTransaction(tx);

                return new BpLotteryDrawOutcome
                {
                    Success = true,
                    Message = "抽取成功",
                    Records = records,
                    Wallet = walletService.GetWallet(profileId),
                    Progress = lotteryProgress,
                };
            }
            catch (Exception ex)
            {
                logger.Error($"[SPT-BattlePass] 抽奖失败 profile={profileId} pool={poolId}: {ex.Message}");
                tx.Status = BpLotteryConstants.TransactionNeedsManualReview;
                tx.Message = ex.Message;
                tx.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                BattlePassStore.UpsertLotteryTransaction(tx);
                return BpLotteryDrawOutcome.Fail("抽奖异常，事务已记录，请联系管理员");
            }
        }
    }

    private BpLotteryDrawOutcome ExistingCommitted(string profileId, string poolId, string requestId, string action, string message)
    {
        var records = BattlePassStore
            .GetLotteryDrawRecords()
            .Where(r =>
                string.Equals(r.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.PoolId, poolId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.RequestId, requestId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.DrawMode, action, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.DrawIndexInRequest)
            .ToList();

        return new BpLotteryDrawOutcome
        {
            Success = true,
            Message = message,
            Records = records,
            Wallet = walletService.GetWallet(profileId),
            Progress = BattlePassStore.GetLotteryPoolProgress(profileId, poolId),
        };
    }

    private BpLotteryDrawOutcome FailTransaction(BpLotteryTransaction tx, string message)
    {
        tx.Status = BpLotteryConstants.TransactionFailed;
        tx.Message = message;
        tx.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BattlePassStore.UpsertLotteryTransaction(tx);
        return BpLotteryDrawOutcome.Fail(message);
    }

    private (bool ok, string message, List<DrawPlan> plans) PlanDraws(
        string profileId,
        BpLotteryPool pool,
        BpLotteryPoolProgress progress,
        BpProgress bpProgress,
        int drawCount
    )
    {
        var plans = new List<DrawPlan>();
        var workingDrawn = new HashSet<string>(progress.DrawnPrizeIds, StringComparer.OrdinalIgnoreCase);
        var workingPity = progress.PityCounter;

        for (var i = 0; i < drawCount; i++)
        {
            var tempProgress = progress with
            {
                DrawnPrizeIds = new HashSet<string>(workingDrawn, StringComparer.OrdinalIgnoreCase),
                PityCounter = workingPity,
            };

            var candidates = lotteryService.GetCandidatePrizes(pool, tempProgress);
            if (candidates.Count == 0)
            {
                return (false, "奖池已抽空", plans);
            }

            var forceGrand = string.Equals(pool.PoolType, BpLotteryConstants.PoolTypeRepeatable, StringComparison.OrdinalIgnoreCase)
                && pool.PityEnabled
                && pool.PityCount >= 2
                && workingPity + 1 >= pool.PityCount;

            var probabilities = lotteryService.CalculateProbabilities(pool, candidates);
            var selected = SelectPrize(profileId, pool, candidates, bpProgress, forceGrand);
            var owned = battlePassService.HasReward(profileId, bpProgress, selected.Reward);
            var converted = owned && selected.ConvertDuplicateToExchangeCoin;
            var coinAmount = converted ? Math.Max(0, selected.DuplicateExchangeCoinAmount) : 0;

            plans.Add(new DrawPlan(
                selected,
                probabilities.GetValueOrDefault(selected.Id),
                selected.Weight,
                converted,
                coinAmount
            ));

            if (string.Equals(pool.PoolType, BpLotteryConstants.PoolTypeNonRepeatable, StringComparison.OrdinalIgnoreCase))
            {
                workingDrawn.Add(selected.Id);
            }

            if (string.Equals(pool.PoolType, BpLotteryConstants.PoolTypeRepeatable, StringComparison.OrdinalIgnoreCase)
                && pool.PityEnabled)
            {
                workingPity = selected.IsGrandPrize ? 0 : workingPity + 1;
            }
        }

        return (true, "", plans);
    }

    private BpLotteryPrize SelectPrize(
        string profileId,
        BpLotteryPool pool,
        List<BpLotteryPrize> candidates,
        BpProgress bpProgress,
        bool forceGrand
    )
    {
        if (forceGrand)
        {
            var grand = candidates.Where(p => p.IsGrandPrize).ToList();
            var unownedGrand = grand.Where(p => !battlePassService.HasReward(profileId, bpProgress, p.Reward)).ToList();
            var source = unownedGrand.Count > 0 ? unownedGrand : grand;
            if (source.Count > 0)
            {
                return source[Random.Shared.Next(source.Count)];
            }
        }

        if (!string.Equals(pool.ProbabilityMode, BpLotteryConstants.ProbabilityWeight, StringComparison.OrdinalIgnoreCase))
        {
            return candidates[Random.Shared.Next(candidates.Count)];
        }

        var total = candidates.Sum(p => Math.Max(0, p.Weight));
        if (total <= 0)
        {
            return candidates[Random.Shared.Next(candidates.Count)];
        }

        var roll = Random.Shared.Next(1, total + 1);
        var cursor = 0;
        foreach (var prize in candidates)
        {
            cursor += Math.Max(0, prize.Weight);
            if (roll <= cursor)
            {
                return prize;
            }
        }

        return candidates[^1];
    }

    private (bool ok, string message, BpLotteryCost? cost) BuildCost(BpLotteryPool pool, BpLotteryPoolProgress progress, int drawCount)
    {
        if (string.Equals(pool.PoolType, BpLotteryConstants.PoolTypeNonRepeatable, StringComparison.OrdinalIgnoreCase))
        {
            var drawNumber = progress.SuccessfulDrawCount + 1;
            var step = pool.StepCosts.FirstOrDefault(s => s.DrawNumber == drawNumber);
            return step is null
                ? (false, $"缺少第 {drawNumber} 抽代价配置", null)
                : (true, "", NormalizeCost(step.Cost, pool.CostType));
        }

        if (drawCount == 10
            && string.Equals(pool.CostType, BpLotteryConstants.CostLotteryTickets, StringComparison.OrdinalIgnoreCase)
            && pool.TenDrawCostOverride is not null)
        {
            return (true, "", NormalizeCost(pool.TenDrawCostOverride, pool.CostType));
        }

        var single = NormalizeCost(pool.SingleCost, pool.CostType);
        if (drawCount == 1)
        {
            return (true, "", single);
        }

        if (string.Equals(pool.CostType, BpLotteryConstants.CostLotteryTickets, StringComparison.OrdinalIgnoreCase))
        {
            return (true, "", single with { TicketAmount = single.TicketAmount * drawCount });
        }

        var multiplied = single.StashItems
            .Select(c => new BpBarterCost { Tpl = c.Tpl, Count = c.Count * drawCount })
            .ToList();
        return (true, "", single with { StashItems = multiplied });
    }

    private static BpLotteryCost NormalizeCost(BpLotteryCost cost, string costType)
    {
        return cost with { CostType = costType };
    }

    private (bool ok, string message, BpLotteryCostSnapshot snapshot) TryDeductCost(string profileId, string poolId, BpLotteryCost cost)
    {
        if (string.Equals(cost.CostType, BpLotteryConstants.CostLotteryTickets, StringComparison.OrdinalIgnoreCase))
        {
            return walletService.TryDeductTickets(profileId, poolId, cost.TicketAmount);
        }

        return TryDeductStashItems(profileId, cost.StashItems);
    }

    private (bool ok, string message, BpLotteryCostSnapshot snapshot) TryDeductStashItems(string profileId, List<BpBarterCost> source)
    {
        var snapshot = new BpLotteryCostSnapshot { CostType = BpLotteryConstants.CostStashItems };
        if (!MongoId.IsValidMongoId(profileId))
        {
            return (false, "玩家 profileId 无效", snapshot);
        }

        if (!BattlePassShopService.TryAggregateCosts(source, out var costs) || costs.Count == 0)
        {
            return (false, "仓库物品代价配置无效", snapshot);
        }

        var sessionId = new MongoId(profileId);
        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Inventory?.Items is null)
        {
            return (false, "未找到玩家仓库", snapshot);
        }

        foreach (var cost in costs)
        {
            var have = stashService.CountTpl(pmc, cost.Tpl, requireFir: false);
            if (have < cost.Count)
            {
                return (false, $"仓库物品不足：{ResolveCostItemName(cost.Tpl)} 需要 {cost.Count}，当前 {have}", snapshot);
            }
        }

        var refunded = new List<BpReward>();
        foreach (var cost in costs)
        {
            var removed = stashService.RemoveTpl(pmc, sessionId, cost.Tpl, cost.Count, requireFir: false);
            if (removed > 0)
            {
                refunded.Add(new BpReward { Type = "item", Tpl = cost.Tpl, Count = removed });
            }

            if (removed < cost.Count)
            {
                SaveProfile(profileId);
                if (refunded.Count > 0)
                {
                    rewardService.Deliver(profileId, refunded, "通行证抽奖扣费失败 · 退还");
                }

                return (false, "扣费未完成，已退还已扣物品，请重试", snapshot);
            }
        }

        SaveProfile(profileId);
        snapshot.StashItems = costs;
        return (true, "", snapshot);
    }

    private string ResolveCostItemName(string tpl)
    {
        tpl = (tpl ?? "").Trim();
        if (!MongoId.IsValidMongoId(tpl))
        {
            return string.IsNullOrWhiteSpace(tpl) ? "物品" : tpl;
        }

        var name = itemSearchService.ResolveItemNameZh(new MongoId(tpl));
        return string.IsNullOrWhiteSpace(name)
            ? tpl
            : name;
    }

    private List<BpLotteryDrawRecord> CommitDraws(
        string profileId,
        BpLotteryPool pool,
        BpLotteryPoolProgress lotteryProgress,
        BpProgress bpProgress,
        string requestId,
        string action,
        List<DrawPlan> plans,
        BpLotteryCostSnapshot costSnapshot
    )
    {
        var records = new List<BpLotteryDrawRecord>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nickname = battlePassService.GetNickname(profileId) ?? "玩家";

        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            if (plan.ConvertedToExchangeCoin)
            {
                if (plan.ExchangeCoinAmount > 0)
                {
                    walletService.Grant(profileId, exchangeCoins: plan.ExchangeCoinAmount);
                }
            }
            else
            {
                battlePassService.GrantRewards(
                    profileId,
                    bpProgress,
                    [plan.Prize.Reward],
                    $"【通行证抽奖】奖池「{pool.Name}」奖励，请查收。"
                );
            }

            lotteryProgress.SuccessfulDrawCount++;
            if (string.Equals(pool.PoolType, BpLotteryConstants.PoolTypeNonRepeatable, StringComparison.OrdinalIgnoreCase))
            {
                lotteryProgress.DrawnPrizeIds.Add(plan.Prize.Id);
            }
            else if (pool.PityEnabled)
            {
                lotteryProgress.PityCounter = plan.Prize.IsGrandPrize ? 0 : lotteryProgress.PityCounter + 1;
            }

            records.Add(new BpLotteryDrawRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ProfileId = profileId,
                NicknameSnapshot = nickname,
                PoolId = pool.Id,
                PoolNameSnapshot = pool.Name,
                RequestId = requestId,
                DrawMode = action,
                DrawIndexInRequest = i + 1,
                PrizeId = plan.Prize.Id,
                PrizeNameSnapshot = lotteryService.ResolvePrizeName(plan.Prize),
                PrizeType = (plan.Prize.Reward.Type ?? "item").Trim(),
                PrizeIconSnapshot = plan.Prize.Reward.Tpl,
                IsGrandPrize = plan.Prize.IsGrandPrize,
                RaritySnapshot = string.IsNullOrWhiteSpace(plan.Prize.Rarity) ? "common" : plan.Prize.Rarity,
                BroadcastWhenWon = plan.Prize.BroadcastWhenWon,
                ProbabilitySnapshot = plan.Probability,
                WeightSnapshot = plan.Weight,
                ConvertedToExchangeCoin = plan.ConvertedToExchangeCoin,
                ExchangeCoinAmount = plan.ExchangeCoinAmount,
                CostSnapshot = costSnapshot,
                CreatedUtc = now,
            });
        }

        return records;
    }

    private void SaveProfile(string profileId)
    {
        try
        {
            saveServer.SaveProfileAsync(new MongoId(profileId)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] 抽奖保存玩家档案失败 profile={profileId}: {ex.Message}");
        }
    }

    private static object ToRecordView(BpLotteryDrawRecord record)
    {
        return new
        {
            record.Id,
            record.ProfileId,
            record.PoolId,
            record.RequestId,
            record.DrawMode,
            record.DrawIndexInRequest,
            record.PrizeId,
            record.PrizeNameSnapshot,
            record.PrizeType,
            record.IsGrandPrize,
            record.RaritySnapshot,
            record.ConvertedToExchangeCoin,
            record.ExchangeCoinAmount,
            record.CreatedUtc,
        };
    }

    private sealed record DrawPlan(
        BpLotteryPrize Prize,
        decimal Probability,
        int Weight,
        bool ConvertedToExchangeCoin,
        int ExchangeCoinAmount
    );
}

public sealed record BpLotteryDrawOutcome
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public List<BpLotteryDrawRecord> Records { get; init; } = new();
    public BpLotteryWallet? Wallet { get; init; }
    public BpLotteryPoolProgress? Progress { get; init; }

    public static BpLotteryDrawOutcome Fail(string message)
    {
        return new BpLotteryDrawOutcome { Success = false, Message = message };
    }
}
