using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace SPTarkov.Server.Core.BattlePass.Controllers;

/// <summary>通行证抽奖玩家端 API。</summary>
[Injectable]
[ApiController]
[Route("battlepass/api/lottery")]
public class LotteryController(
    LotteryService lotteryService,
    LotteryWalletService walletService,
    LotteryDrawService drawService,
    LotteryExchangeShopService exchangeShopService,
    ItemControl.ItemSearchService itemSearchService,
    BattlePassService battlePassService
) : ControllerBase
{
    [HttpGet("overview")]
    public object Overview([FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        var settings = BattlePassStore.GetLotterySettings();
        if (!settings.Enabled || !settings.PlayerEntryEnabled)
        {
            return new { success = false, message = "抽奖模块未启用" };
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var wallet = walletService.GetWallet(profileId);
        var pools = lotteryService
            .GetPools()
            .Where(p => lotteryService.IsVisibleToPlayer(p, now))
            .OrderBy(p => p.SortOrder)
            .Select(p => ToPoolSummary(profileId, p, now))
            .ToList();

        var limit = Math.Clamp(settings.PlayerRecordDisplayLimit, 1, 500);
        var records = BattlePassStore
            .GetLotteryDrawRecords()
            .Where(r => string.Equals(r.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.CreatedUtc)
            .Take(limit)
            .ToList();

        var broadcasts = settings.BroadcastEnabled
            ? BattlePassStore
                .GetLotteryDrawRecords()
                .Where(r => r.IsGrandPrize)
                .OrderByDescending(r => r.CreatedUtc)
                .Take(30)
                .Select(r => ToBroadcast(r, settings))
                .ToList()
            : [];

        return new
        {
            success = true,
            settings,
            wallet,
            pools,
            records,
            broadcasts,
            shop = settings.ExchangeShopEnabled ? exchangeShopService.GetCatalog(profileId) : [],
        };
    }

    [HttpGet("pools/{poolId}")]
    public object PoolDetail(string poolId, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        var pool = lotteryService.GetPool(poolId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (pool is null || !lotteryService.IsVisibleToPlayer(pool, now))
        {
            return new { success = false, message = "奖池不存在或不可见" };
        }

        var progress = BattlePassStore.GetLotteryPoolProgress(profileId, pool.Id);
        var candidates = lotteryService.GetCandidatePrizes(pool, progress);
        var probabilities = lotteryService.CalculateProbabilities(pool, candidates);
        return new
        {
            success = true,
            pool = ToPoolSummary(profileId, pool, now),
            wallet = walletService.GetWallet(profileId),
            progress,
            prizes = pool.Prizes.Select(p => ToPrizeView(p, progress, probabilities)).ToList(),
        };
    }

    [HttpPost("pools/{poolId}/draw-once")]
    public object DrawOnce(string poolId, [FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        return Draw(poolId, request, token, ten: false);
    }

    [HttpPost("pools/{poolId}/draw-ten")]
    public object DrawTen(string poolId, [FromBody] JsonElement request, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        return Draw(poolId, request, token, ten: true);
    }

    [HttpGet("records")]
    public object Records([FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        var settings = BattlePassStore.GetLotterySettings();
        var limit = Math.Clamp(settings.PlayerRecordDisplayLimit, 1, 500);
        var records = BattlePassStore
            .GetLotteryDrawRecords()
            .Where(r => string.Equals(r.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.CreatedUtc)
            .Take(limit)
            .ToList();
        return new { success = true, records };
    }

    [HttpGet("shop")]
    public object Shop([FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        var settings = BattlePassStore.GetLotterySettings();
        if (!settings.ExchangeShopEnabled)
        {
            return new { success = false, message = "兑换商店未启用" };
        }

        return new { success = true, wallet = walletService.GetWallet(profileId), items = exchangeShopService.GetCatalog(profileId) };
    }

    [HttpPost("shop/{itemId}/purchase")]
    public object PurchaseShopItem(string itemId, [FromHeader(Name = "X-BP-Token")] string? token = null)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        var settings = BattlePassStore.GetLotterySettings();
        if (!settings.ExchangeShopEnabled)
        {
            return new { success = false, message = "兑换商店未启用" };
        }

        var (ok, message, wallet) = exchangeShopService.Purchase(profileId, itemId);
        return new { success = ok, message, wallet };
    }

    private object Draw(string poolId, JsonElement request, string? token, bool ten)
    {
        var profileId = BattlePassSession.Resolve(token);
        if (profileId is null)
        {
            return new { success = false, message = "未登录或会话已过期" };
        }

        var requestId = request.TryGetProperty("requestId", out var id) ? id.GetString() : null;
        var outcome = ten
            ? drawService.DrawTen(profileId, poolId, requestId)
            : drawService.DrawOnce(profileId, poolId, requestId);
        return new
        {
            success = outcome.Success,
            message = outcome.Message,
            records = outcome.Records,
            wallet = outcome.Wallet,
            progress = outcome.Progress,
        };
    }

    private object ToPoolSummary(string profileId, BpLotteryPool pool, long now)
    {
        var (startUtc, endUtc) = lotteryService.GetPoolWindow(pool);
        var progress = BattlePassStore.GetLotteryPoolProgress(profileId, pool.Id);
        var candidates = lotteryService.GetCandidatePrizes(pool, progress);
        var canDraw = lotteryService.CanDraw(pool, now);
        return new
        {
            pool.Id,
            pool.Name,
            pool.Description,
            pool.IconUrl,
            pool.CoverUrl,
            pool.SortOrder,
            pool.Status,
            pool.PoolType,
            pool.ProbabilityMode,
            pool.CostType,
            pool.PityEnabled,
            pool.PityCount,
            singleCost = ToCostView(pool.SingleCost),
            tenDrawCostOverride = pool.TenDrawCostOverride is null ? null : ToCostView(pool.TenDrawCostOverride),
            stepCosts = pool.StepCosts.Select(ToStepCostView).ToList(),
            startUtc,
            endUtc,
            canDraw = canDraw.ok,
            drawMessage = canDraw.message,
            remainingPrizes = candidates.Count,
            totalPrizes = pool.Prizes.Count,
            progress.SuccessfulDrawCount,
            progress.PityCounter,
            poolTickets = walletService.GetWallet(profileId).PoolTickets.GetValueOrDefault(pool.Id),
        };
    }

    private object ToStepCostView(BpLotteryStepCost step)
    {
        return new
        {
            step.DrawNumber,
            cost = ToCostView(step.Cost),
        };
    }

    private object ToCostView(BpLotteryCost cost)
    {
        return new
        {
            cost.CostType,
            cost.TicketAmount,
            stashItems = cost.StashItems.Select(ToCostItemView).ToList(),
        };
    }

    private object ToCostItemView(BpBarterCost cost)
    {
        var tpl = cost.Tpl?.Trim() ?? "";
        var name = "";
        if (MongoId.IsValidMongoId(tpl))
        {
            name = itemSearchService.ResolveItemNameZh(new MongoId(tpl));
        }

        return new
        {
            cost.Tpl,
            cost.Count,
            name = string.IsNullOrWhiteSpace(name) ? null : name,
            iconUrl = MongoId.IsValidMongoId(tpl) ? $"/battlepass/api/icons/{tpl}" : null,
        };
    }

    private object ToPrizeView(BpLotteryPrize prize, BpLotteryPoolProgress progress, Dictionary<string, decimal> probabilities)
    {
        return new
        {
            prize.Id,
            name = lotteryService.ResolvePrizeName(prize),
            prize.Reward,
            prize.Weight,
            prize.Rarity,
            prize.IsGrandPrize,
            prize.BroadcastWhenWon,
            prize.ConvertDuplicateToExchangeCoin,
            prize.DuplicateExchangeCoinAmount,
            drawn = progress.DrawnPrizeIds.Contains(prize.Id),
            probability = probabilities.GetValueOrDefault(prize.Id),
        };
    }

    private object ToBroadcast(BpLotteryDrawRecord record, BpLotterySettings settings)
    {
        var nickname = battlePassService.GetNickname(record.ProfileId);
        if (string.IsNullOrWhiteSpace(nickname))
        {
            nickname = "玩家";
        }
        if (string.Equals(settings.BroadcastNameMode, "masked", StringComparison.OrdinalIgnoreCase) && nickname.Length > 2)
        {
            nickname = nickname[0] + "***" + nickname[^1];
        }

        return new
        {
            nickname,
            record.PoolNameSnapshot,
            record.PrizeNameSnapshot,
            record.PrizeType,
            record.RaritySnapshot,
            record.CreatedUtc,
        };
    }
}
