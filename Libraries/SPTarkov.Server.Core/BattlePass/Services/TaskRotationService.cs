namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     随机任务轮换选择器：从管理员定义的任务模板池按权重随机抽取当期任务。
///     fixed 任务常驻，random 任务进入随机池。日/周按自然周期滚动。
/// </summary>
public static class TaskRotationService
{
    // 每个 scope 同时活跃的随机任务数量默认值（管理员可在赛季设置里覆盖，见 BpSeason.*TaskCount）。
    public const int DailyRandomCount = 3;
    public const int WeeklyRandomCount = 2;

    /// <summary>某 scope 同时活跃的随机任务数量（读赛季设置；season &lt;=0 = 全保留）。</summary>
    internal static int RandomCountForScope(BpSeason season, string scope)
    {
        return (scope ?? "").ToLowerInvariant() switch
        {
            "daily" => Math.Max(0, season.DailyTaskCount),
            "weekly" => Math.Max(0, season.WeeklyTaskCount),
            _ => season.SeasonTaskCount > 0 ? season.SeasonTaskCount : int.MaxValue, // 赛季 0 = 全部
        };
    }

    /// <summary>按权重不放回随机抽取 count 个模板。</summary>
    public static List<BpTaskTemplate> PickWeighted(List<BpTaskTemplate> pool, int count)
    {
        var picked = new List<BpTaskTemplate>();
        var working = pool.ToList();

        while (picked.Count < count && working.Count > 0)
        {
            var totalWeight = working.Sum(t => Math.Max(1, t.Weight));
            var roll = Random.Shared.Next(totalWeight);
            var acc = 0;
            var chosenIndex = working.Count - 1;
            for (var i = 0; i < working.Count; i++)
            {
                acc += Math.Max(1, working[i].Weight);
                if (roll < acc)
                {
                    chosenIndex = i;
                    break;
                }
            }

            picked.Add(working[chosenIndex]);
            working.RemoveAt(chosenIndex);
        }

        return picked;
    }

    /// <summary>选出某 scope 当期应活跃的模板：fixed 全保留 + random 按数量抽取（或按难度预算抽取）。</summary>
    public static List<BpTaskTemplate> SelectForScope(
        List<BpTaskTemplate> allTasks,
        BpSeason season,
        string scope,
        ISet<string>? excludeTaskIds = null
    )
    {
        var scoped = GetScopedTemplates(allTasks, scope);
        var result = scoped.Where(t => string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase)).ToList();

        var randomPool = scoped
            .Where(t => !string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var count = Math.Min(RandomCountForScope(season, scope), randomPool.Count);

        var preferredPool = excludeTaskIds is { Count: > 0 }
            ? randomPool.Where(t => !excludeTaskIds.Contains(t.Id)).ToList()
            : randomPool;
        if (preferredPool.Count < count)
        {
            preferredPool = randomPool;
        }

        // 难度预算模式（WeekendDrops 式）：抽一组难度之和=预算的任务；失败回退到按数量加权抽。
        if (BudgetEnabledForScope(season, scope) && count > 0)
        {
            var budget = BudgetForScope(season, scope);
            var byBudget = PickByDifficultyBudget(preferredPool, count, budget)
                ?? PickByDifficultyBudget(randomPool, count, budget);
            if (byBudget is not null)
            {
                result.AddRange(byBudget);
                return result;
            }
        }

        result.AddRange(PickWeighted(preferredPool, count));
        return result;
    }

    internal static bool BudgetEnabledForScope(BpSeason season, string scope) => (scope ?? "").ToLowerInvariant() switch
    {
        "daily" => season.DailyBudgetEnabled,
        "weekly" => season.WeeklyBudgetEnabled,
        "season" => season.SeasonBudgetEnabled,
        _ => false,
    };

    internal static int BudgetForScope(BpSeason season, string scope) => (scope ?? "").ToLowerInvariant() switch
    {
        "daily" => season.DailyDifficultyBudget,
        "weekly" => season.WeeklyDifficultyBudget,
        "season" => season.SeasonDifficultyBudget,
        _ => 0,
    };

    /// <summary>任务"组"键：同组每期至多出一题，保证种类多样（如不会同时出两条 PMC 击杀）。</summary>
    private static string GroupKey(BpTaskTemplate t)
    {
        var ct = (t.ConditionType ?? "").Trim().ToLowerInvariant();
        return ct == "kills" ? $"kills|{(t.Target ?? "").Trim().ToLowerInvariant()}" : ct;
    }

    /// <summary>难度归一到 1..3。</summary>
    private static int Diff(BpTaskTemplate t) => Math.Clamp(t.Difficulty <= 0 ? 1 : t.Difficulty, 1, 3);

    /// <summary>
    ///     从随机池抽 <paramref name="n"/> 个任务，使难度之和恰为 <paramref name="budget"/>，每组至多一题。
    ///     找不到满足的组合时返回 null（调用方回退到按数量加权抽）。移植自 WeekendDrops 难度预算抽题。
    /// </summary>
    public static List<BpTaskTemplate>? PickByDifficultyBudget(List<BpTaskTemplate> pool, int n, int budget)
    {
        if (n <= 0 || budget <= 0 || pool.Count < n)
        {
            return null;
        }

        var byDifficulty = pool
            .GroupBy(Diff)
            .ToDictionary(g => g.Key, g => g.OrderBy(_ => Random.Shared.Next()).ToList());

        foreach (var comp in DifficultyCompositions(n, budget).OrderBy(_ => Random.Shared.Next()))
        {
            if (!comp.All(kv => byDifficulty.TryGetValue(kv.Key, out var avail) && avail.Count >= kv.Value))
            {
                continue;
            }

            var picked = new List<BpTaskTemplate>();
            var usedGroups = new HashSet<string>();
            var ok = true;
            foreach (var (diff, need) in comp)
            {
                var remaining = need;
                foreach (var cand in byDifficulty[diff])
                {
                    if (remaining == 0)
                    {
                        break;
                    }

                    if (!usedGroups.Add(GroupKey(cand)))
                    {
                        continue; // 同组已取
                    }

                    picked.Add(cand);
                    remaining--;
                }

                if (remaining > 0)
                {
                    ok = false;
                    break;
                }
            }

            if (ok && picked.Count == n)
            {
                return picked.OrderBy(_ => Random.Shared.Next()).ToList();
            }
        }

        return null;
    }

    /// <summary>枚举 n 个难度(1/2/3)的张数组合，使 1*易+2*中+3*难 = budget。</summary>
    private static IEnumerable<Dictionary<int, int>> DifficultyCompositions(int n, int budget)
    {
        for (var hard = 0; hard <= n; hard++)
        {
            for (var med = 0; med <= n - hard; med++)
            {
                var easy = n - hard - med;
                if (easy * 1 + med * 2 + hard * 3 != budget)
                {
                    continue;
                }

                var map = new Dictionary<int, int>();
                if (easy > 0) map[1] = easy;
                if (med > 0) map[2] = med;
                if (hard > 0) map[3] = hard;
                yield return map;
            }
        }
    }

    /// <summary>
    ///     检查当前活跃任务是否仍符合最新自定义池：移除已删除/改 scope 的模板，补齐 fixed，
    ///     并在管理员调整任务数量后立即校正 random 数量，但不会仅因池中新增普通 random 就提前换题。
    /// </summary>
    public static bool NeedsReconciliation(
        List<BpTaskTemplate> allTasks,
        BpSeason season,
        IEnumerable<BpActiveTask> activeTasks,
        string scope
    )
    {
        var scoped = GetScopedTemplates(allTasks, scope);
        var templates = scoped.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var active = activeTasks.Where(t => string.Equals(t.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToList();
        var activeIds = active.Select(t => t.TaskId).ToList();

        if (activeIds.Count != activeIds.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            return true;
        }

        if (activeIds.Any(id => !templates.ContainsKey(id)))
        {
            return true;
        }

        var activeIdSet = activeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fixedIds = scoped
            .Where(t => string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!fixedIds.IsSubsetOf(activeIdSet))
        {
            return true;
        }

        var randomPoolCount = scoped.Count(t => !string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase));
        var expectedRandomCount = Math.Min(RandomCountForScope(season, scope), randomPoolCount);
        var activeRandomCount = active.Count(t =>
            templates.TryGetValue(t.TaskId, out var template)
            && !string.Equals(template.Rotation, "fixed", StringComparison.OrdinalIgnoreCase)
        );

        return activeRandomCount != expectedRandomCount;
    }

    /// <summary>
    ///     是否到了该 scope 的自动滚动时刻。周期读赛季设置（小时）：
    ///     daily 默认 24h（&lt;=0 回退按自然日）；weekly 默认 168h；season 默认不自动滚动。周期 &lt;=0（daily 除外）= 不自动刷新。
    /// </summary>
    public static bool ShouldRoll(BpSeason season, string scope, long lastRollUtc, long nowUtc)
    {
        if (lastRollUtc == 0)
        {
            return true;
        }

        var sc = (scope ?? "").ToLowerInvariant();
        var hours = sc switch
        {
            "daily" => season.DailyPeriodHours,
            "weekly" => season.WeeklyPeriodHours,
            _ => season.SeasonPeriodHours,
        };

        if (hours <= 0)
        {
            // daily 未配置周期时回退「自然日」语义；其余 scope 不自动滚动
            return sc == "daily" && !SameUtcDay(lastRollUtc, nowUtc);
        }

        return nowUtc - lastRollUtc >= (long)(hours * 3600);
    }

    private static bool SameUtcDay(long a, long b)
    {
        var da = DateTimeOffset.FromUnixTimeSeconds(a).UtcDateTime.Date;
        var db = DateTimeOffset.FromUnixTimeSeconds(b).UtcDateTime.Date;
        return da == db;
    }

    internal static List<BpTaskTemplate> GetScopedTemplates(List<BpTaskTemplate> allTasks, string scope)
    {
        return allTasks
            .Where(t =>
                !string.IsNullOrWhiteSpace(t.Id)
                && string.Equals(t.Scope, scope, StringComparison.OrdinalIgnoreCase)
            )
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
    }
}
