using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     通行证任务追踪：进度全部来自客户端插件战后上报（POST /battlepass/api/track），
///     服务端按 <see cref="BpTaskTemplate"/> 条件判定并累计到 <see cref="BpActiveTask.Progress"/>，达成即结算 BP 经验。
///     <para><b>零档案副作用</b>：不注入任何原生 quest、不写 <c>pmcData.Quests</c>/<c>TaskConditionCounters</c>，加载链路完全不受影响。</para>
///     <para><b>跨局累计</b>：默认任务进度跨局累加，仅在 daily/weekly/season 轮换时清零；
///     <c>singleRaid=true</c> 的任务要求单局内一次达标、不跨局累加。</para>
///     <para>通行证经验只进 <see cref="BpProgress.Xp"/> 驱动 BP 等级，<b>绝不</b>触碰角色经验。</para>
/// </summary>
[Injectable]
public class BattlePassTrackService(
    BattlePassService battlePassService,
    ISptLogger<BattlePassTrackService> logger
)
{
    /// <summary>每个 profile 保留的最近 raidId 数量（幂等去重，防无界增长）。</summary>
    private const int MaxProcessedRaidIds = 200;

    // ============================ 任务轮换（不依赖 pmc，仅维护 BpProgress.ActiveTasks） ============================

    /// <summary>按日/周/赛季周期滚动刷新活跃任务；首次进入则初始化所有 scope。会改动 prog（调用方保存）。</summary>
    public bool RefreshActiveTasks(string profileId, BpProgress prog)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var allTasks = BattlePassStore.GetTasks();
        var changed = false;
        foreach (var scope in new[] { "daily", "weekly", "season" })
        {
            var last = GetLastRollForScope(prog, scope, now);
            if (!TaskRotationService.ShouldRoll(scope, last, now))
            {
                continue;
            }

            changed |= RollScope(prog, allTasks, scope, now, avoidPrevious: false, resetDailyRerolls: true);
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

        if (normalized == "all")
        {
            var changed = false;
            foreach (var s in new[] { "daily", "weekly", "season" })
            {
                changed |= RollScope(prog, allTasks, s, now, avoidPrevious: true, resetDailyRerolls: false);
            }

            return changed;
        }

        return RollScope(prog, allTasks, normalized, now, avoidPrevious: true, resetDailyRerolls: false);
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
        string scope,
        long now,
        bool avoidPrevious,
        bool resetDailyRerolls
    )
    {
        var oldTasks = prog.ActiveTasks.Where(t => ScopeMatches(t.Scope, scope)).ToList();
        ISet<string>? excluded = null;
        if (avoidPrevious && oldTasks.Count > 0)
        {
            excluded = oldTasks.Select(t => t.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        prog.ActiveTasks.RemoveAll(t => ScopeMatches(t.Scope, scope));

        var selected = TaskRotationService.SelectForScope(allTasks, scope, excluded);
        foreach (var template in selected)
        {
            prog.ActiveTasks.Add(NewActiveTask(template, now));
        }

        if (ScopeMatches(scope, "daily"))
        {
            prog.LastDailyRollUtc = now;
            if (resetDailyRerolls)
            {
                prog.DailyRerollsUsed = 0;
            }
        }
        else if (ScopeMatches(scope, "weekly"))
        {
            prog.LastWeeklyRollUtc = now;
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

    // ============================ 战后上报 → 进度累计 / 结算 ============================

    /// <summary>
    ///     应用客户端实时上报的「本场累计快照」：对每条未完成的活跃任务按模板条件算出「本场至今总量」，
    ///     只把相对上次快照的<b>增量</b>补进进度（单局任务直接取本场值），达成则结算 BP 经验。会改动 prog（调用方保存）。
    ///     <para><b>增量幂等</b>：同一 raidId 多次累计快照（逐事件 / 30s 心跳 / 战局结束）重复、乱序、丢包都安全，绝不重复加分；
    ///     已收尾战局的迟到上报按 <see cref="BpProgress.ProcessedRaidIds"/> 拦截。</para>
    /// </summary>
    public RaidTrackResult ApplyRaidTrack(string profileId, BpProgress prog, BpSeason season, RaidTrackPayload? payload)
    {
        var result = new RaidTrackResult();
        if (payload is null)
        {
            return result;
        }

        var raidId = payload.RaidId ?? "";

        // 战局切换 / 乱序迟到判定
        if (!string.Equals(raidId, prog.CurrentRaidId, StringComparison.Ordinal))
        {
            // 已收尾战局的乱序迟到上报：忽略，避免重复入账
            if (!string.IsNullOrWhiteSpace(raidId) && prog.ProcessedRaidIds.Contains(raidId))
            {
                result.Duplicate = true;
                return result;
            }

            // 切到新战局：归档旧局 id，重置本场已应用计数（旧局未收尾也无妨，跨局进度已累计）
            ArchiveRaid(prog, prog.CurrentRaidId);
            prog.CurrentRaidId = string.IsNullOrWhiteSpace(raidId) ? null : raidId;
            prog.CurrentRaidApplied.Clear();
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
            prog.CurrentRaidId = null;
            prog.CurrentRaidApplied.Clear();
        }

        return result;
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
    private static int ComputeDelta(BpTaskTemplate tpl, RaidTrackPayload payload)
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

        if (string.Equals(ct, "HandoverItem", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ct, "FindItem", StringComparison.OrdinalIgnoreCase))
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

        return payload.Kills.Count(k => KillMatchesTarget(tpl.Target, k));
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

    private static bool KillMatchesTarget(string? target, BpKillEvent k)
    {
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

    // ============================ 小工具 ============================

    private static long GetLastRollForScope(BpProgress prog, string scope, long now)
    {
        if (ScopeMatches(scope, "daily"))
        {
            return prog.LastDailyRollUtc;
        }

        if (ScopeMatches(scope, "weekly"))
        {
            return prog.LastWeeklyRollUtc;
        }

        return prog.ActiveTasks.Any(t => ScopeMatches(t.Scope, "season")) ? now : 0;
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
