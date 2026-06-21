namespace SPTarkov.Server.Core.BattlePass;

/// <summary>普通等级奖励轨的发放账本；奖励身份不含数量和展示字段，配置改量不会被误判为新增奖励。</summary>
public static class BattlePassRewardLedger
{
    public sealed record PendingReward(string TrackKey, BpReward Reward, string RewardKey);

    public static string TrackKey(int level, bool premium) => $"level:{level}:{(premium ? "premium" : "free")}";

    public static string? RewardKey(BpReward reward)
    {
        var type = (reward.Type ?? "item").Trim().ToLowerInvariant();
        if (type is not ("purchaseright" or "recipe" or "title"))
        {
            type = "item";
        }

        var identity = type switch
        {
            "purchaseright" => reward.OfferId,
            "recipe" => reward.RecipeId,
            "title" => reward.TitleId,
            _ => reward.Tpl,
        };

        if (string.IsNullOrWhiteSpace(identity))
        {
            return null;
        }

        return $"{type}:{identity.Trim().ToLowerInvariant()}";
    }

    /// <summary>为旧进度按当前配置建立基线，避免升级后把历史奖励全部当成补偿。</summary>
    public static void InitializeBaseline(BpProgress progress, IReadOnlyDictionary<int, BpLevelRewards> tracks)
    {
        progress.GrantedTrackRewards ??= new Dictionary<string, HashSet<string>>();
        RecordClaimedTracks(progress, tracks);
        progress.RewardLedgerInitialized = true;
    }

    public static void RecordTrack(BpProgress progress, int level, bool premium, IEnumerable<BpReward> rewards)
    {
        progress.GrantedTrackRewards ??= new Dictionary<string, HashSet<string>>();
        var trackKey = TrackKey(level, premium);
        if (!progress.GrantedTrackRewards.TryGetValue(trackKey, out var granted) || granted is null)
        {
            granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            progress.GrantedTrackRewards[trackKey] = granted;
        }

        foreach (var reward in rewards)
        {
            var rewardKey = RewardKey(reward);
            if (rewardKey is not null)
            {
                granted.Add(rewardKey);
            }
        }

        progress.RewardLedgerInitialized = true;
    }

    public static List<PendingReward> GetPending(BpProgress progress, IReadOnlyDictionary<int, BpLevelRewards> tracks)
    {
        if (!progress.RewardLedgerInitialized)
        {
            return [];
        }

        progress.GrantedTrackRewards ??= new Dictionary<string, HashSet<string>>();
        var pending = new List<PendingReward>();
        CollectPending(progress, tracks, progress.ClaimedFree, premium: false, pending);
        CollectPending(progress, tracks, progress.ClaimedPremium, premium: true, pending);
        return pending;
    }

    public static void RecordPending(BpProgress progress, IEnumerable<PendingReward> pending)
    {
        progress.GrantedTrackRewards ??= new Dictionary<string, HashSet<string>>();
        foreach (var item in pending)
        {
            if (!progress.GrantedTrackRewards.TryGetValue(item.TrackKey, out var granted) || granted is null)
            {
                granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                progress.GrantedTrackRewards[item.TrackKey] = granted;
            }

            granted.Add(item.RewardKey);
        }
    }

    private static void RecordClaimedTracks(BpProgress progress, IReadOnlyDictionary<int, BpLevelRewards> tracks)
    {
        foreach (var level in progress.ClaimedFree)
        {
            if (tracks.TryGetValue(level, out var rewards))
            {
                RecordTrack(progress, level, premium: false, rewards.Free);
            }
        }

        foreach (var level in progress.ClaimedPremium)
        {
            if (tracks.TryGetValue(level, out var rewards))
            {
                RecordTrack(progress, level, premium: true, rewards.Premium);
            }
        }
    }

    private static void CollectPending(
        BpProgress progress,
        IReadOnlyDictionary<int, BpLevelRewards> tracks,
        IEnumerable<int> claimedLevels,
        bool premium,
        List<PendingReward> pending
    )
    {
        foreach (var level in claimedLevels)
        {
            if (!tracks.TryGetValue(level, out var levelRewards))
            {
                continue;
            }

            var trackKey = TrackKey(level, premium);
            progress.GrantedTrackRewards.TryGetValue(trackKey, out var granted);
            var seenInTrack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rewards = premium ? levelRewards.Premium : levelRewards.Free;
            foreach (var reward in rewards)
            {
                var rewardKey = RewardKey(reward);
                if (rewardKey is null || !seenInTrack.Add(rewardKey) || granted?.Contains(rewardKey) == true)
                {
                    continue;
                }

                pending.Add(new PendingReward(trackKey, reward, rewardKey));
            }
        }
    }
}
