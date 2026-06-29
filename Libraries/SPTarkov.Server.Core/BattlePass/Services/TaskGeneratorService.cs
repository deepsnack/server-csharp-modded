using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     程序化任务生成器：按管理员规格(<see cref="BpGenSpec"/>)向<b>独立生成池</b>（<c>tasks-gen.json</c>，
///     与管理员自定义池物理隔离、不污染后台任务管理）合成任务模板，以 <c>gen_</c> 前缀标记，
///     重新生成时按 scope 整体替换本前缀任务。玩家抽取活跃任务时由 <see cref="BattlePassStore.GetAllTasks"/>
///     合并自定义池∪生成池，再由 <see cref="BattlePassTrackService.RefreshActiveTasks"/> 各自随机抽取并按周期过期。
///     <para>支持手动一键生成与按周期自动生成（<see cref="BpGenScopeSpec.AutoPeriodHours"/>）。
///     仅生成无需策划物品的类型（Kills / Exploration）；物品/区域类任务仍由管理员手写。</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class TaskGeneratorService(ISptLogger<TaskGeneratorService> logger)
{
    public const string GenPrefix = "gen_";

    private static readonly string[] SupportedTypes = ["Kills", "Exploration"];
    private static readonly object GenLock = new();

    // 生成标题用的地图中文名（与后台 tasks 页地图列表一致；缺失则回退 id）。
    private static readonly Dictionary<string, string> MapNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bigmap"] = "海关",
        ["factory4_day"] = "工厂(昼)",
        ["factory4_night"] = "工厂(夜)",
        ["Woods"] = "森林",
        ["Shoreline"] = "海岸线",
        ["RezervBase"] = "储备站",
        ["Interchange"] = "立交桥",
        ["laboratory"] = "实验室",
        ["Lighthouse"] = "灯塔",
        ["TarkovStreets"] = "街区",
        ["Sandbox"] = "机房",
    };

    private static readonly Dictionary<string, string> TargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Any"] = "任意敌人",
        ["Savage"] = "Scav",
        ["AnyPmc"] = "PMC",
        ["Bear"] = "BEAR",
        ["Usec"] = "USEC",
        ["Boss"] = "头目",
    };

    /// <summary>手动生成：对指定 scope（空 = 全部已启用 scope）重建生成任务。返回新生成的任务总数。</summary>
    public int Generate(IEnumerable<string>? scopes = null)
    {
        var spec = BattlePassStore.GetGenSpec();
        var targets = (scopes ?? new[] { "daily", "weekly", "season" })
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s is "daily" or "weekly" or "season")
            .Distinct()
            .ToList();

        var total = 0;
        lock (GenLock)
        {
            // 写独立生成池（tasks-gen.json），与管理员自定义池物理隔离，不污染后台任务管理。
            var pool = BattlePassStore.GetGenTasks();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var scope in targets)
            {
                var scopeSpec = ScopeSpec(spec, scope);
                if (!scopeSpec.Enabled)
                {
                    continue;
                }

                pool.RemoveAll(t => t.Id.StartsWith(GenPrefix, StringComparison.OrdinalIgnoreCase) && ScopeMatches(t.Scope, scope));
                var generated = GenerateScope(scope, scopeSpec);
                pool.AddRange(generated);
                scopeSpec.LastGenUtc = now;
                total += generated.Count;
            }

            BattlePassStore.SaveGenTasks(pool);
            BattlePassStore.SaveGenSpec(spec);
        }

        if (total > 0)
        {
            logger.Info($"[SPT-BattlePass] 任务生成：写入生成池 {total} 条（独立于自定义池；scopes={string.Join(",", targets)}）");
        }

        return total;
    }

    /// <summary>
    ///     自动生成：对启用且配置了周期的 scope，按 <see cref="BpGenScopeSpec.AutoPeriodHours"/> 到期重建。
    ///     由 <see cref="BattlePassTrackService.RefreshActiveTasks"/> 在玩家交互时顺带调用，无需独立调度器。
    /// </summary>
    public void MaybeAutoGenerate()
    {
        var spec = BattlePassStore.GetGenSpec();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var due = new List<string>();
        foreach (var scope in new[] { "daily", "weekly", "season" })
        {
            var s = ScopeSpec(spec, scope);
            if (s.Enabled && s.AutoPeriodHours > 0 && now - s.LastGenUtc >= (long)(s.AutoPeriodHours * 3600))
            {
                due.Add(scope);
            }
        }

        if (due.Count > 0)
        {
            Generate(due);
        }
    }

    private List<BpTaskTemplate> GenerateScope(string scope, BpGenScopeSpec spec)
    {
        var types = (spec.ConditionTypes ?? [])
            .Where(t => SupportedTypes.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (types.Count == 0)
        {
            types = ["Kills"];
        }

        var result = new List<BpTaskTemplate>();
        var count = Math.Clamp(spec.Count, 1, 100);
        for (var i = 0; i < count; i++)
        {
            var ct = types[Random.Shared.Next(types.Count)];
            var diff = Random.Shared.Next(1, 4); // 1..3
            var xp = diff switch { 1 => spec.XpEasy, 2 => spec.XpMed, _ => spec.XpHard };
            var location = PickLocation(spec);
            var targetCount = ScaledCount(spec, diff);

            var template = new BpTaskTemplate
            {
                Id = $"{GenPrefix}{scope}_{ct.ToLowerInvariant()}_{RandHex(8)}",
                Scope = scope,
                Rotation = "random",
                Weight = 4 - diff, // 简单题权重更高，与原 PickWeighted 直觉一致
                ConditionType = ct,
                Count = targetCount,
                Location = location,
                Xp = xp,
                Difficulty = diff,
                RewardMode = "xp",
            };

            if (ct.Equals("Kills", StringComparison.OrdinalIgnoreCase))
            {
                var targets = spec.KillTargets is { Count: > 0 } ? spec.KillTargets : new List<string> { "Any" };
                template.Target = targets[Random.Shared.Next(targets.Count)];
                template.Title = BuildKillTitle(template.Target, targetCount, location);
            }
            else // Exploration
            {
                template.Target = "Any";
                template.Title = BuildExploreTitle(targetCount, location);
            }

            template.Description = template.Title;
            result.Add(template);
        }

        return result;
    }

    private static string? PickLocation(BpGenScopeSpec spec)
    {
        if (spec.Locations is not { Count: > 0 })
        {
            return null;
        }

        // 约半数带地图约束，留出"不限地图"的变化
        return Random.Shared.Next(2) == 0 ? null : spec.Locations[Random.Shared.Next(spec.Locations.Count)];
    }

    private static int ScaledCount(BpGenScopeSpec spec, int diff)
    {
        var min = Math.Max(1, spec.MinCount);
        var max = Math.Max(min, spec.MaxCount);
        // diff 1→min, 3→max, 2→中点；再叠加 ±1 抖动
        var baseVal = min + (max - min) * (diff - 1) / 2.0;
        var jitter = Random.Shared.Next(-1, 2);
        return Math.Max(1, (int)Math.Round(baseVal) + jitter);
    }

    private static string BuildKillTitle(string target, int count, string? location)
    {
        var who = TargetNames.GetValueOrDefault(target, target);
        return location is null ? $"击杀 {count} 名{who}" : $"在{MapName(location)}击杀 {count} 名{who}";
    }

    private static string BuildExploreTitle(int count, string? location)
    {
        return location is null ? $"成功撤离 {count} 次" : $"从{MapName(location)}成功撤离 {count} 次";
    }

    private static string MapName(string id) => MapNames.GetValueOrDefault(id, id);

    private static BpGenScopeSpec ScopeSpec(BpGenSpec spec, string scope) => scope switch
    {
        "weekly" => spec.Weekly,
        "season" => spec.Season,
        _ => spec.Daily,
    };

    private static bool ScopeMatches(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string RandHex(int len)
    {
        const string hex = "0123456789abcdef";
        return string.Create(len, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = hex[Random.Shared.Next(16)];
            }
        });
    }
}
