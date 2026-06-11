namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     随机任务轮换选择器：从管理员定义的任务模板池按权重随机抽取当期任务。
///     fixed 任务常驻，random 任务进入随机池。日/周按自然周期滚动。
/// </summary>
public static class TaskRotationService
{
    // 每个 scope 同时活跃的随机任务数量
    public const int DailyRandomCount = 3;
    public const int WeeklyRandomCount = 2;

    private static readonly Random Rng = new();

    /// <summary>按权重不放回随机抽取 count 个模板。</summary>
    public static List<BpTaskTemplate> PickWeighted(List<BpTaskTemplate> pool, int count)
    {
        var picked = new List<BpTaskTemplate>();
        var working = pool.ToList();

        while (picked.Count < count && working.Count > 0)
        {
            var totalWeight = working.Sum(t => Math.Max(1, t.Weight));
            var roll = Rng.Next(totalWeight);
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
        string scope,
        ISet<string>? excludeTaskIds = null
    )
    {
        var scoped = allTasks.Where(t => string.Equals(t.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToList();
        var result = scoped.Where(t => string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase)).ToList();

        var randomPool = scoped
            .Where(t => !string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var count = scope switch
        {
            "daily" => DailyRandomCount,
            "weekly" => WeeklyRandomCount,
            _ => randomPool.Count, // season：随机池也全保留
        };

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

    /// <summary>是否到了该 scope 的滚动时刻（daily=隔自然日，weekly=隔 7 天，season=只初始化一次）。</summary>
    public static bool ShouldRoll(string scope, long lastRollUtc, long nowUtc)
    {
        if (lastRollUtc == 0)
        {
            return true;
        }

        return scope switch
        {
            "daily" => !SameUtcDay(lastRollUtc, nowUtc),
            "weekly" => nowUtc - lastRollUtc >= 7L * 24 * 3600,
            _ => false,
        };
    }

    private static bool SameUtcDay(long a, long b)
    {
        var da = DateTimeOffset.FromUnixTimeSeconds(a).UtcDateTime.Date;
        var db = DateTimeOffset.FromUnixTimeSeconds(b).UtcDateTime.Date;
        return da == db;
    }
}
