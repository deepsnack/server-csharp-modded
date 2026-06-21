using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     通行证任务追踪：服务端战后档案是 Kills/FindItem/HandoverItem/Exploration 的权威来源；
///     客户端插件上报（POST /battlepass/api/track）只作为 VisitZone/PlaceItem 等服务端档案缺失维度的补充。
///     两条路径共用同一套 <see cref="BpTaskTemplate"/> 条件判定与进度结算。
///     <para><b>零档案副作用</b>：不注入任何原生 quest、不写 <c>pmcData.Quests</c>/<c>TaskConditionCounters</c>，加载链路完全不受影响。</para>
///     <para><b>跨局累计</b>：默认任务进度跨局累加，仅在 daily/weekly/season 轮换时清零；
///     <c>singleRaid=true</c> 的任务要求单局内一次达标、不跨局累加。</para>
///     <para>通行证经验只进 <see cref="BpProgress.Xp"/> 驱动 BP 等级，<b>绝不</b>触碰角色经验。</para>
/// </summary>
[Injectable]
public class BattlePassTrackService(
    BattlePassService battlePassService,
    Services.DatabaseService databaseService,
    ISptLogger<BattlePassTrackService> logger
)
{
    /// <summary>每个 profile 保留的最近 raidId 数量（幂等去重，防无界增长）。</summary>
    private const int MaxProcessedRaidIds = 200;

    private enum RaidTrackSource
    {
        ClientSupplemental,
        ServerAuthoritative,
    }

    // ============================ 任务轮换（不依赖 pmc，仅维护 BpProgress.ActiveTasks） ============================

    /// <summary>按日/周/赛季周期滚动刷新活跃任务；首次进入则初始化所有 scope。会改动 prog（调用方保存）。</summary>
    public bool RefreshActiveTasks(string profileId, BpProgress prog)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var allTasks = BattlePassStore.GetTasks();
        var season = BattlePassStore.GetSeason();
        var changed = false;
        foreach (var scope in new[] { "daily", "weekly", "season" })
        {
            var last = GetLastRollForScope(prog, scope);
            var shouldRoll = TaskRotationService.ShouldRoll(season, scope, last, now);
            var needsReconciliation = TaskRotationService.NeedsReconciliation(allTasks, season, prog.ActiveTasks, scope);
            if (!shouldRoll && !needsReconciliation)
            {
                continue;
            }

            if (shouldRoll)
            {
                RollScope(prog, allTasks, season, scope, now, avoidPrevious: false, resetRefreshBudget: true);
                changed = true;
            }
            else
            {
                changed |= ReconcileScope(prog, allTasks, season, scope, now);
            }
        }

        return changed;
    }

    /// <summary>强制刷新某 scope（或 all）的活跃任务；玩家每日刷新与管理端刷新共用。</summary>
    public bool ForceRefreshTasks(string profileId, BpProgress prog, string? scope)
    {
        var normalized = NormalizeScope(scope);
        if (normalized is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var allTasks = BattlePassStore.GetTasks();
        var season = BattlePassStore.GetSeason();

        if (normalized == "all")
        {
            var changed = false;
            foreach (var s in new[] { "daily", "weekly", "season" })
            {
                changed |= RollScope(prog, allTasks, season, s, now, avoidPrevious: true, resetRefreshBudget: false);
            }

            return changed;
        }

        return RollScope(prog, allTasks, season, normalized, now, avoidPrevious: true, resetRefreshBudget: false);
    }

    /// <summary>替换某条日任务为同池内另一随机任务（消耗刷新次数由调用方控制）。</summary>
    public bool RerollTask(string profileId, BpProgress prog, string taskId)
    {
        var target = prog.ActiveTasks.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        var allTasks = BattlePassStore.GetTasks();
        var targetTemplate = allTasks.FirstOrDefault(t => string.Equals(t.Id, target.TaskId, StringComparison.OrdinalIgnoreCase));
        if (targetTemplate is null || string.Equals(targetTemplate.Rotation, "fixed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var activeIds = prog.ActiveTasks.Select(t => t.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pool = allTasks
            .Where(t =>
                string.Equals(t.Scope, target.Scope, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase)
                && !activeIds.Contains(t.Id)
            )
            .ToList();

        var replacement = TaskRotationService.PickWeighted(pool, 1).FirstOrDefault();
        if (replacement is null)
        {
            return false;
        }

        prog.ActiveTasks.Remove(target);
        prog.ActiveTasks.Add(NewActiveTask(replacement, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return true;
    }

    private static bool RollScope(
        BpProgress prog,
        List<BpTaskTemplate> allTasks,
        BpSeason season,
        string scope,
        long now,
        bool avoidPrevious,
        bool resetRefreshBudget
    )
    {
        var oldTasks = prog.ActiveTasks.Where(t => ScopeMatches(t.Scope, scope)).ToList();
        ISet<string>? excluded = null;
        if (avoidPrevious && oldTasks.Count > 0)
        {
            excluded = oldTasks.Select(t => t.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        prog.ActiveTasks.RemoveAll(t => ScopeMatches(t.Scope, scope));

        var selected = TaskRotationService.SelectForScope(allTasks, season, scope, excluded);
        foreach (var template in selected)
        {
            prog.ActiveTasks.Add(NewActiveTask(template, now));
        }

        if (ScopeMatches(scope, "daily"))
        {
            prog.LastDailyRollUtc = now;
            if (resetRefreshBudget)
            {
                prog.DailyRerollsUsed = 0;
            }
        }
        else if (ScopeMatches(scope, "weekly"))
        {
            prog.LastWeeklyRollUtc = now;
            if (resetRefreshBudget)
            {
                prog.WeeklyRefreshUsed = 0;
            }
        }
        else if (ScopeMatches(scope, "season"))
        {
            prog.LastSeasonRollUtc = now;
            if (resetRefreshBudget)
            {
                prog.SeasonRefreshUsed = 0;
            }
        }

        return oldTasks.Count > 0 || selected.Count > 0;
    }

    private static BpActiveTask NewActiveTask(BpTaskTemplate template, long now)
    {
        return new BpActiveTask
        {
            TaskId = template.Id,
            QuestId = "",
            Scope = template.Scope,
            AcceptedUtc = now,
            Progress = 0,
            CreditedXp = false,
        };
    }

    internal static bool ReconcileScope(BpProgress prog, List<BpTaskTemplate> allTasks, BpSeason season, string scope, long now)
    {
        var scoped = TaskRotationService.GetScopedTemplates(allTasks, scope);
        var templates = scoped.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var current = prog.ActiveTasks.Where(t => ScopeMatches(t.Scope, scope)).ToList();
        var retained = new List<BpActiveTask>();
        var retainedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var active in current)
        {
            if (templates.ContainsKey(active.TaskId) && retainedIds.Add(active.TaskId))
            {
                retained.Add(active);
            }
        }

        foreach (var fixedTemplate in scoped.Where(t => string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase)))
        {
            if (retainedIds.Add(fixedTemplate.Id))
            {
                retained.Add(NewActiveTask(fixedTemplate, now));
            }
        }

        var desiredRandomCount = Math.Min(
            TaskRotationService.RandomCountForScope(season, scope),
            scoped.Count(t => !string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase))
        );
        var retainedRandom = retained
            .Where(t => templates.TryGetValue(t.TaskId, out var template)
                && !string.Equals(template.Rotation, "fixed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (retainedRandom.Count > desiredRandomCount)
        {
            var removeIds = retainedRandom
                .Skip(desiredRandomCount)
                .Select(t => t.TaskId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            retained.RemoveAll(t => removeIds.Contains(t.TaskId));
            retainedIds.ExceptWith(removeIds);
        }
        else if (retainedRandom.Count < desiredRandomCount)
        {
            var candidates = scoped
                .Where(t =>
                    !string.Equals(t.Rotation, "fixed", StringComparison.OrdinalIgnoreCase)
                    && !retainedIds.Contains(t.Id)
                )
                .ToList();
            foreach (var template in TaskRotationService.PickWeighted(candidates, desiredRandomCount - retainedRandom.Count))
            {
                retained.Add(NewActiveTask(template, now));
                retainedIds.Add(template.Id);
            }
        }

        prog.ActiveTasks.RemoveAll(t => ScopeMatches(t.Scope, scope));
        prog.ActiveTasks.AddRange(retained);
        return true;
    }

    // ============================ 战后上报 → 进度累计 / 结算 ============================

    /// <summary>
    ///     应用客户端实时上报的「本场累计快照」。客户端只负责服务端战后档案缺失的补充维度：
    ///     <c>VisitZone</c> / <c>PlaceItem</c>；Kills/FindItem/HandoverItem/Exploration 由
    ///     <see cref="ApplyAuthoritativeRaidTrack"/> 在战后从服务端档案权威结算，避免客户端随机 raidId 与
    ///     服务端 ServerId 不一致时重复入账。
    /// </summary>
    public RaidTrackResult ApplyRaidTrack(string profileId, BpProgress prog, BpSeason season, RaidTrackPayload? payload)
    {
        return ApplyRaidTrackInternal(profileId, prog, season, payload, RaidTrackSource.ClientSupplemental);
    }

    /// <summary>
    ///     应用服务端战后档案生成的权威累计快照。会改动 prog（调用方保存）。
    ///     <para><b>增量幂等</b>：同一 raidId 重复处理会按 <see cref="BpProgress.ProcessedRaidIds"/> 拦截；
    ///     若旧版客户端已在同一物理战局中预先累计过同类任务，本方法会沿用当前战局基准，只补最终差值。</para>
    /// </summary>
    public RaidTrackResult ApplyAuthoritativeRaidTrack(string profileId, BpProgress prog, BpSeason season, RaidTrackPayload? payload)
    {
        return ApplyRaidTrackInternal(profileId, prog, season, payload, RaidTrackSource.ServerAuthoritative);
    }

    private RaidTrackResult ApplyRaidTrackInternal(
        string profileId,
        BpProgress prog,
        BpSeason season,
        RaidTrackPayload? payload,
        RaidTrackSource source
    )
    {
        var result = new RaidTrackResult();
        if (payload is null)
        {
            return result;
        }

        var raidId = payload.RaidId ?? "";
        var previousRaidId = prog.CurrentRaidId;
        var previousRaidHadAppliedProgress = prog.CurrentRaidApplied.Any(kv => kv.Value > 0);
        var authoritative = source == RaidTrackSource.ServerAuthoritative;

        // 战局切换 / 乱序迟到判定
        if (!string.Equals(raidId, prog.CurrentRaidId, StringComparison.Ordinal))
        {
            // 已收尾战局的乱序迟到上报：忽略，避免重复入账
            if (!string.IsNullOrWhiteSpace(raidId) && prog.ProcessedRaidIds.Contains(raidId))
            {
                result.Duplicate = true;
                return result;
            }

            // 客户端补充上报切到新战局：归档旧局 id，重置本场已应用计数。
            // 服务端权威收尾即便 raidId 不同，也先保留当前基准，兼容旧版客户端已预先入账的同类进度。
            if (!authoritative)
            {
                ArchiveRaid(prog, prog.CurrentRaidId);
                prog.CurrentRaidApplied.Clear();
            }

            prog.CurrentRaidId = string.IsNullOrWhiteSpace(raidId) ? null : raidId;
        }

        var templates = BattlePassStore.GetTasks().ToDictionary(t => t.Id);

        foreach (var active in prog.ActiveTasks)
        {
            if (active.CreditedXp)
            {
                continue;
            }

            if (!templates.TryGetValue(active.TaskId, out var tpl))
            {
                continue;
            }

            if (!TaskSourceCanProcess(tpl, source))
            {
                continue;
            }

            var fullDelta = ComputeDelta(tpl, payload); // 本场累计快照换算出的「本场至今总量」
            var target = Math.Max(1, tpl.Count);
            var prevApplied = prog.CurrentRaidApplied.TryGetValue(active.TaskId, out var p) ? p : 0;
            bool advanced;

            if (tpl.SingleRaid)
            {
                // 单局任务：进度即本场累计值（不跨局累加；切局时本值自然从小重新计）
                advanced = active.Progress != fullDelta;
                active.Progress = fullDelta;
                prog.CurrentRaidApplied[active.TaskId] = fullDelta;
            }
            else
            {
                var inc = fullDelta - prevApplied; // 自上次快照以来的新增量
                advanced = inc > 0;
                if (advanced)
                {
                    active.Progress += inc; // 跨局累计
                }

                if (fullDelta > prevApplied)
                {
                    prog.CurrentRaidApplied[active.TaskId] = fullDelta; // 刷新本场已应用基准（乱序变小不回退）
                }
            }

            if (active.Progress >= target)
            {
                var xp = tpl.Xp;
                if (prog.PremiumUnlocked && season.PremiumXpMultiplier > 1.0)
                {
                    xp = (int)Math.Round(xp * season.PremiumXpMultiplier);
                }

                battlePassService.AddXp(prog, season, xp); // 仅记 BP 经验，不动角色经验
                active.CreditedXp = true;
                active.Progress = target;
                result.Credited.Add(new RaidTrackCredit { TaskId = active.TaskId, GainedXp = xp, Done = true });
                logger.Info($"[SPT-BattlePass] 任务达成 profile={profileId} task={active.TaskId} +{xp}xp (raid={raidId})");
            }
            else if (advanced)
            {
                result.Credited.Add(new RaidTrackCredit { TaskId = active.TaskId, GainedXp = 0, Done = false });
            }
        }

        // 战局结束（上报带撤离状态）：归档本局，停止后续增量
        if (!string.IsNullOrWhiteSpace(payload.ExitStatus))
        {
            ArchiveRaid(prog, prog.CurrentRaidId);
            if (authoritative && previousRaidHadAppliedProgress)
            {
                ArchiveRaid(prog, previousRaidId);
            }

            prog.CurrentRaidId = null;
            prog.CurrentRaidApplied.Clear();
        }

        return result;
    }

    private static bool TaskSourceCanProcess(BpTaskTemplate tpl, RaidTrackSource source)
    {
        var ct = tpl.ConditionType?.Trim() ?? "Kills";
        var supplemental = string.Equals(ct, "VisitZone", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ct, "PlaceItem", StringComparison.OrdinalIgnoreCase);

        return source == RaidTrackSource.ClientSupplemental ? supplemental : !supplemental;
    }

    /// <summary>把一个 raidId 记入已处理列表（容量上限，超出丢弃最旧）；用于拦截已收尾战局的迟到上报。</summary>
    private static void ArchiveRaid(BpProgress prog, string? raidId)
    {
        if (string.IsNullOrWhiteSpace(raidId) || prog.ProcessedRaidIds.Contains(raidId))
        {
            return;
        }

        prog.ProcessedRaidIds.Add(raidId);
        if (prog.ProcessedRaidIds.Count > MaxProcessedRaidIds)
        {
            prog.ProcessedRaidIds.RemoveRange(0, prog.ProcessedRaidIds.Count - MaxProcessedRaidIds);
        }
    }

    /// <summary>把一场上报对某任务模板换算成进度增量（条件判定全在服务端）。</summary>
    private int ComputeDelta(BpTaskTemplate tpl, RaidTrackPayload payload)
    {
        var ct = tpl.ConditionType?.Trim() ?? "Kills";

        if (string.Equals(ct, "Exploration", StringComparison.OrdinalIgnoreCase))
        {
            var survived = string.Equals(payload.ExitStatus, "Survived", StringComparison.OrdinalIgnoreCase);
            return survived && LocationMatches(tpl.Location, payload.Location) ? 1 : 0;
        }

        if (string.Equals(ct, "VisitZone", StringComparison.OrdinalIgnoreCase))
        {
            if (!LocationMatches(tpl.Location, payload.Location))
            {
                return 0;
            }

            if (string.IsNullOrEmpty(tpl.ZoneId))
            {
                return payload.VisitedZones.Count > 0 ? 1 : 0;
            }

            return payload.VisitedZones.Any(z => string.Equals(z, tpl.ZoneId, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        }

        // HandoverItem 只走网页上交（BattlePassHandoverService），不在战后档案自动计数，避免「游戏内还是网页交」歧义。
        if (string.Equals(ct, "HandoverItem", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(ct, "FindItem", StringComparison.OrdinalIgnoreCase))
        {
            if (!LocationMatches(tpl.Location, payload.Location))
            {
                return 0;
            }

            var wanted = WantedTpls(tpl);
            if (wanted.Count == 0)
            {
                return 0;
            }

            return payload.FoundItems.Where(f => wanted.Contains(f.Tpl)).Sum(f => Math.Max(0, f.Count));
        }

        if (string.Equals(ct, "PlaceItem", StringComparison.OrdinalIgnoreCase))
        {
            if (!LocationMatches(tpl.Location, payload.Location))
            {
                return 0;
            }

            var wanted = WantedTpls(tpl);
            return payload.PlacedItems.Count(p =>
                wanted.Contains(p.Tpl)
                && (string.IsNullOrEmpty(tpl.ZoneId) || string.Equals(p.ZoneId, tpl.ZoneId, StringComparison.OrdinalIgnoreCase))
            );
        }

        // 默认 Kills
        if (!LocationMatches(tpl.Location, payload.Location))
        {
            return 0;
        }

        return payload.Kills.Count(k => KillMatches(tpl, k));
    }

    private static HashSet<string> WantedTpls(BpTaskTemplate tpl)
    {
        return (tpl.ItemRequirements ?? new List<BpTaskItemRequirement>())
            .Select(r => r.Tpl)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool LocationMatches(string? templateLocation, string? raidLocation)
    {
        if (string.IsNullOrEmpty(templateLocation) ||
            string.Equals(templateLocation, "any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(templateLocation, raidLocation, StringComparison.OrdinalIgnoreCase);
    }

    internal bool KillMatches(BpTaskTemplate tpl, BpKillEvent k)
    {
        if (!ValueMatches(tpl.Weapons, k.Weapon) || !ValueMatches(tpl.BodyParts, k.BodyPart))
        {
            return false;
        }

        if (!WeaponCaliberMatches(tpl.WeaponCalibers, k.Weapon))
        {
            return false;
        }

        if (!ValueMatches(tpl.SavageRoles, k.Role))
        {
            return false;
        }

        if (!DistanceMatches(tpl.DistanceCompare, tpl.DistanceValue, k.Distance))
        {
            return false;
        }

        if (!DaytimeMatches(tpl.DaytimeFrom, tpl.DaytimeTo, k.Time))
        {
            return false;
        }

        var target = tpl.Target;
        var t = (target ?? "Any").Trim();
        if (string.Equals(t, "Any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var side = k.Side ?? "";
        var role = k.Role ?? "";
        return t.ToLowerInvariant() switch
        {
            "savage" or "scav" => side.Equals("Savage", StringComparison.OrdinalIgnoreCase),
            "anypmc" => side.Equals("Bear", StringComparison.OrdinalIgnoreCase) || side.Equals("Usec", StringComparison.OrdinalIgnoreCase),
            "bear" => side.Equals("Bear", StringComparison.OrdinalIgnoreCase),
            "usec" => side.Equals("Usec", StringComparison.OrdinalIgnoreCase),
            "boss" => role.StartsWith("boss", StringComparison.OrdinalIgnoreCase),
            _ => side.Equals(t, StringComparison.OrdinalIgnoreCase) || role.Equals(t, StringComparison.OrdinalIgnoreCase),
        };
    }

    private bool WeaponCaliberMatches(IEnumerable<string>? allowed, string? weaponTpl)
    {
        var calibers = allowed?.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (calibers is null || calibers.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(weaponTpl) || !Models.Common.MongoId.IsValidMongoId(weaponTpl))
        {
            return false;
        }

        var weaponId = new Models.Common.MongoId(weaponTpl);
        if (!databaseService.GetItems().TryGetValue(weaponId, out var weapon))
        {
            return false;
        }

        var actual = weapon.Properties?.Caliber ?? weapon.Properties?.AmmoCaliber;
        return ValueMatches(calibers, actual);
    }

    private static bool ValueMatches(IEnumerable<string>? allowed, string? actual)
    {
        var list = allowed?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (list is null || list.Count == 0)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(actual) && list.Any(v => string.Equals(v, actual, StringComparison.OrdinalIgnoreCase));
    }

    private static bool DistanceMatches(string? compare, double? expected, double? actual)
    {
        if (expected is null)
        {
            return true;
        }

        if (actual is null)
        {
            return false;
        }

        return (compare ?? ">=").Trim().ToLowerInvariant() switch
        {
            ">" or "gt" or "greater" or "more" => actual.Value > expected.Value,
            "<" or "lt" or "less" => actual.Value < expected.Value,
            "<=" or "lte" or "le" or "max" or "atmost" => actual.Value <= expected.Value,
            "=" or "==" or "eq" => Math.Abs(actual.Value - expected.Value) < 0.001,
            _ => actual.Value >= expected.Value,
        };
    }

    private static bool DaytimeMatches(int? from, int? to, string? time)
    {
        if (from is null && to is null)
        {
            return true;
        }

        if (!TryParseHour(time, out var hour))
        {
            return false;
        }

        var start = NormalizeHour(from ?? 0);
        var end = NormalizeHour(to ?? 24);

        if (start == end)
        {
            return true;
        }

        return start < end
            ? hour >= start && hour < end
            : hour >= start || hour < end;
    }

    private static bool TryParseHour(string? time, out int hour)
    {
        hour = 0;
        if (string.IsNullOrWhiteSpace(time))
        {
            return false;
        }

        var firstPart = time.Split(':', 2)[0];
        return int.TryParse(firstPart, out hour);
    }

    private static int NormalizeHour(int hour)
    {
        var normalized = hour % 24;
        return normalized < 0 ? normalized + 24 : normalized;
    }

    // ============================ 小工具 ============================

    private static long GetLastRollForScope(BpProgress prog, string scope)
    {
        if (ScopeMatches(scope, "daily"))
        {
            return prog.LastDailyRollUtc;
        }

        if (ScopeMatches(scope, "weekly"))
        {
            return prog.LastWeeklyRollUtc;
        }

        return prog.LastSeasonRollUtc;
    }

    private static string? NormalizeScope(string? scope)
    {
        return scope?.Trim().ToLowerInvariant() switch
        {
            "daily" => "daily",
            "weekly" => "weekly",
            "season" => "season",
            "all" => "all",
            _ => null,
        };
    }

    private static bool ScopeMatches(string? a, string? b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>一次 <see cref="BattlePassTrackService.ApplyRaidTrack"/> 的结算结果（回传客户端/前端展示）。</summary>
public class RaidTrackResult
{
    /// <summary>该 raidId 之前已处理过（幂等命中），本次未做任何变更。</summary>
    public bool Duplicate { get; set; }

    /// <summary>本次有进度变化或达成的任务列表。</summary>
    public List<RaidTrackCredit> Credited { get; set; } = new();
}

/// <summary>单条任务的本次结算明细。</summary>
public class RaidTrackCredit
{
    public string TaskId { get; set; } = "";

    /// <summary>本次因达成而获得的 BP 经验（未达成为 0）。</summary>
    public int GainedXp { get; set; }

    /// <summary>本次是否达成完成。</summary>
    public bool Done { get; set; }
}
