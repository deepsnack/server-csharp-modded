namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>协管授权记录的统一 profileId 匹配与删除规则。</summary>
public static class BattlePassCollaboratorGrantPolicy
{
    private static readonly string[] LegacyDefaultCapabilities =
    [
        "shop.read", "shop.submit",
        "tasks.read", "tasks.submit",
        "tracks.read", "tracks.submit",
        "lottery.read", "lottery.submit",
        "trader.read", "trader.submit",
        "recipes.read", "recipes.submit",
        "items.read", "items.submit",
        "flea.read", "flea.submit",
        "quests.read", "quests.submit",
    ];

    private static readonly string[] BaseDefaultCapabilities =
    [
        ..LegacyDefaultCapabilities,
        "titles.read", "titles.submit",
    ];

    public static string NormalizeProfileId(string? profileId) => (profileId ?? "").Trim();

    public static List<string> DefaultCapabilities() => NormalizeCapabilities(BaseDefaultCapabilities);

    public static List<string> NormalizeCapabilities(IEnumerable<string>? capabilities)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (capabilities is not null)
        {
            foreach (var capability in capabilities)
            {
                var normalized = (capability ?? "").Trim();
                if (normalized.Length > 0)
                {
                    set.Add(normalized);
                }
            }
        }

        if (set.Contains("*"))
        {
            return ["*"];
        }

        // Legacy collaborator grants predate the dedicated trader-quest module.
        // They granted full "battle-pass tasks" + "trader" access but have no quests.* entries.
        if (set.Contains("tasks.read") && set.Contains("trader.read"))
        {
            set.Add("quests.read");
        }

        if (set.Contains("tasks.submit") && set.Contains("trader.submit"))
        {
            set.Add("quests.submit");
        }

        // 2026-07:称号管理纳入协管默认全模块授权。仅旧版“完整默认授权”补齐，
        // 手工裁剪的受限授权不自动扩权。
        if (LegacyDefaultCapabilities.All(cap => set.Contains(cap)))
        {
            set.Add("titles.read");
            set.Add("titles.submit");
        }

        return set
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static HashSet<string> NormalizeCapabilitySet(IEnumerable<string>? capabilities) =>
        new(NormalizeCapabilities(capabilities), StringComparer.OrdinalIgnoreCase);

    public static BpCollaboratorGrant? Find(
        IEnumerable<BpCollaboratorGrant> grants,
        string? profileId,
        bool requireEnabled = false
    )
    {
        var normalized = NormalizeProfileId(profileId);
        if (normalized.Length == 0) return null;

        return grants.FirstOrDefault(g =>
            (!requireEnabled || g.Enabled)
            && string.Equals(NormalizeProfileId(g.ProfileId), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>永久移除指定玩家的全部重复/历史授权记录。</summary>
    public static int RemoveAll(List<BpCollaboratorGrant> grants, string? profileId)
    {
        var normalized = NormalizeProfileId(profileId);
        if (normalized.Length == 0) return 0;

        return grants.RemoveAll(g =>
            string.Equals(NormalizeProfileId(g.ProfileId), normalized, StringComparison.OrdinalIgnoreCase));
    }
}
