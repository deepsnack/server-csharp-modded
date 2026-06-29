using System.Globalization;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>抽奖兑换商店服务。</summary>
[Injectable]
public class LotteryExchangeShopService(
    LotteryWalletService walletService,
    BattlePassService battlePassService,
    LotteryService lotteryService
)
{
    private static readonly object ShopGate = new();

    public List<object> GetCatalog(string profileId)
    {
        lock (ShopGate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var progress = BattlePassStore.GetLotteryShopPurchases(profileId);
            return BattlePassStore
                .GetLotteryShopItems()
                .Where(item => IsVisible(item, now))
                .OrderBy(item => item.SortOrder)
                .Select(item => ToView(profileId, item, progress, now))
                .ToList();
        }
    }

    public (bool ok, string message, BpLotteryWallet wallet) Purchase(string profileId, string itemId)
    {
        lock (ShopGate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var item = BattlePassStore
                .GetLotteryShopItems()
                .FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
            if (item is null || !IsVisible(item, now))
            {
                return (false, "商品不存在或未上架", walletService.GetWallet(profileId));
            }

            var bpSeason = BattlePassStore.GetSeason();
            var bpProgress = battlePassService.GetOrResetProgress(profileId, bpSeason);
            if (battlePassService.HasReward(profileId, bpProgress, item.Reward))
            {
                return (false, "你已拥有该奖励", walletService.GetWallet(profileId));
            }

            var purchaseProgress = BattlePassStore.GetLotteryShopPurchases(profileId);
            var counter = GetCounter(item.Id, purchaseProgress);
            NormalizeCounter(counter);
            if (!CanBuy(item, counter, out var limitMessage))
            {
                return (false, limitMessage, walletService.GetWallet(profileId));
            }

            var deducted = walletService.TryDeductExchangeCoins(profileId, item.Price);
            if (!deducted.ok)
            {
                return (false, deducted.message, walletService.GetWallet(profileId));
            }

            battlePassService.GrantRewards(
                profileId,
                bpProgress,
                [item.Reward],
                $"【通行证抽奖兑换商店】兑换「{item.Name}」奖励，请查收。"
            );
            BattlePassStore.SaveProgress(profileId, bpProgress);

            IncrementCounter(item, counter);
            BattlePassStore.SaveLotteryShopPurchases(profileId, purchaseProgress);
            return (true, "兑换成功", walletService.GetWallet(profileId));
        }
    }

    private object ToView(string profileId, BpLotteryShopItem item, BpLotteryShopPurchaseProgress progress, long now)
    {
        var counter = GetCounter(item.Id, progress);
        NormalizeCounter(counter);
        var canBuy = CanBuy(item, counter, out var limitMessage);
        return new
        {
            item.Id,
            item.Name,
            item.Description,
            item.IconUrl,
            item.SortOrder,
            item.Price,
            item.LimitType,
            item.LimitCount,
            reward = item.Reward,
            canBuy,
            limitMessage,
            remaining = Remaining(item, counter),
            now,
        };
    }

    private static bool IsVisible(BpLotteryShopItem item, long now)
    {
        if (!item.Enabled || !item.VisibleToPlayers)
        {
            return false;
        }

        if (!string.Equals(item.Status, BpLotteryConstants.PoolStatusActive, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (item.StartUtc > 0 && now < item.StartUtc)
        {
            return false;
        }

        if (item.EndUtc > 0 && now >= item.EndUtc)
        {
            return false;
        }

        return true;
    }

    private static BpLotteryShopPurchaseCounter GetCounter(string itemId, BpLotteryShopPurchaseProgress progress)
    {
        if (!progress.Items.TryGetValue(itemId, out var counter))
        {
            counter = new BpLotteryShopPurchaseCounter();
            progress.Items[itemId] = counter;
        }

        return counter;
    }

    private static bool CanBuy(BpLotteryShopItem item, BpLotteryShopPurchaseCounter counter, out string message)
    {
        message = "";
        if (item.Price <= 0)
        {
            message = "商品价格配置无效";
            return false;
        }

        if (item.LimitCount <= 0 || string.Equals(item.LimitType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var used = UsedForLimit(item, counter);
        if (used >= item.LimitCount)
        {
            message = "已达限购上限";
            return false;
        }

        return true;
    }

    private static int Remaining(BpLotteryShopItem item, BpLotteryShopPurchaseCounter counter)
    {
        if (item.LimitCount <= 0 || string.Equals(item.LimitType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return Math.Max(0, item.LimitCount - UsedForLimit(item, counter));
    }

    private static int UsedForLimit(BpLotteryShopItem item, BpLotteryShopPurchaseCounter counter)
    {
        return item.LimitType?.Trim().ToLowerInvariant() switch
        {
            "lifetime" => counter.Lifetime,
            "daily" => counter.Daily,
            "weekly" => counter.Weekly,
            "season" => counter.Season,
            _ => 0,
        };
    }

    private static void IncrementCounter(BpLotteryShopItem item, BpLotteryShopPurchaseCounter counter)
    {
        counter.Lifetime++;
        switch (item.LimitType?.Trim().ToLowerInvariant())
        {
            case "daily":
                counter.Daily++;
                break;
            case "weekly":
                counter.Weekly++;
                break;
            case "season":
                counter.Season++;
                break;
        }
    }

    private static void NormalizeCounter(BpLotteryShopPurchaseCounter counter)
    {
        var settings = BattlePassStore.GetLotterySettings();
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveTimeZone(settings.TimeZoneId));
        var dailyKey = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (!string.Equals(counter.DailyKey, dailyKey, StringComparison.Ordinal))
        {
            counter.DailyKey = dailyKey;
            counter.Daily = 0;
        }

        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var weeklyKey = monday.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (!string.Equals(counter.WeeklyKey, weeklyKey, StringComparison.Ordinal))
        {
            counter.WeeklyKey = weeklyKey;
            counter.Weekly = 0;
        }

        var seasonId = BattlePassStore.GetSeason().SeasonId;
        if (!string.Equals(counter.SeasonId, seasonId, StringComparison.OrdinalIgnoreCase))
        {
            counter.SeasonId = seasonId;
            counter.Season = 0;
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }
}
