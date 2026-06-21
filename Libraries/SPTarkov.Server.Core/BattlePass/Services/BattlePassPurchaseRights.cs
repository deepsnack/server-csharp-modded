namespace SPTarkov.Server.Core.BattlePass;

/// <summary>通行证商人购买权账本与旧数据迁移。</summary>
public static class BattlePassPurchaseRights
{
    private const string LedgerPrefix = "purchaseright:";

    public static bool Has(BpProgress progress, string offerId)
    {
        if (string.IsNullOrWhiteSpace(offerId))
        {
            return false;
        }

        Normalize(progress);
        return progress.PurchaseRights.Contains(offerId.Trim());
    }

    public static bool Grant(BpProgress progress, string offerId)
    {
        Normalize(progress);
        return !string.IsNullOrWhiteSpace(offerId) && progress.PurchaseRights.Add(offerId.Trim());
    }

    public static int RevokeAll(BpProgress progress)
    {
        Normalize(progress);
        var count = progress.PurchaseRights.Count;
        progress.PurchaseRights.Clear();
        return count;
    }

    /// <summary>
    ///     从旧版奖励账本与领取记录恢复购买权。可重复调用，仅在实际补齐权限时返回 true。
    /// </summary>
    public static bool Migrate(BpProgress progress, IReadOnlyDictionary<int, BpLevelRewards> tracks)
    {
        Normalize(progress);
        var before = progress.PurchaseRights.Count;

        foreach (var rewardKey in (progress.GrantedTrackRewards ?? new Dictionary<string, HashSet<string>>())
                     .Values.SelectMany(keys => keys ?? []))
        {
            if (rewardKey.StartsWith(LedgerPrefix, StringComparison.OrdinalIgnoreCase)
                && rewardKey.Length > LedgerPrefix.Length)
            {
                progress.PurchaseRights.Add(rewardKey[LedgerPrefix.Length..].Trim());
            }
        }

        if (tracks.TryGetValue(BattlePassStore.CycleRewardLevelKey, out var cycleRewards))
        {
            if (progress.ClaimedCycleFree.Count > 0)
            {
                AddRewards(progress.PurchaseRights, cycleRewards.Free);
            }

            if (progress.ClaimedCyclePremium.Count > 0)
            {
                AddRewards(progress.PurchaseRights, cycleRewards.Premium);
            }
        }

        return progress.PurchaseRights.Count != before;
    }

    private static void Normalize(BpProgress progress)
    {
        if (progress.PurchaseRights is not null
            && progress.PurchaseRights.Comparer.Equals(StringComparer.OrdinalIgnoreCase)
            && progress.PurchaseRights.All(x => !string.IsNullOrWhiteSpace(x) && string.Equals(x, x.Trim(), StringComparison.Ordinal)))
        {
            return;
        }

        progress.PurchaseRights = new HashSet<string>(
            (progress.PurchaseRights ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
            StringComparer.OrdinalIgnoreCase
        );
    }

    private static void AddRewards(HashSet<string> rights, IEnumerable<BpReward> rewards)
    {
        foreach (var reward in rewards)
        {
            if (string.Equals(reward.Type, "purchaseRight", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(reward.OfferId))
            {
                rights.Add(reward.OfferId.Trim());
            }
        }
    }
}
