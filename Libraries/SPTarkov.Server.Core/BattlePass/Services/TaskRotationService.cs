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

    /// <summary>选出某 scope 当期应活跃的模板：fixed 全保留 + random 按数量抽取。</summary>
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

        result.AddRange(PickWeighted(preferredPool, count));
        return result;
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
