using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     通行证任务追踪：服务端战后档案是普通 Kills/FindItem/HandoverItem/Exploration 的权威来源；
///     客户端插件上报（POST /battlepass/api/track）负责带武器/口径/配件条件的 Kills，以及 VisitZone/PlaceItem 等服务端档案缺失维度。
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
    TaskGeneratorService taskGenerator,
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
        // 玩家交互时顺带触发到期的自动生成（无独立调度器）；会按需重建共享池的 gen_ 任务。
        taskGenerator.MaybeAutoGenerate();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var allTasks = BattlePassStore.GetAllTasks();
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
        var allTasks = BattlePassStore.GetAllTasks();
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

        var allTasks = BattlePassStore.GetAllTasks();
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
    ///     应用客户端实时上报的「本场累计快照」。客户端负责带武器上下文条件的 <c>Kills</c>，以及
    ///     <c>VisitZone</c> / <c>PlaceItem</c>；其余 Kills/FindItem/HandoverItem/Exploration 由
    ///     <see cref="ApplyAuthoritativeRaidTrack"/> 在战后从服务端档案权威结算。每种任务只选择一个来源，
    ///     避免客户端随机 raidId 与服务端 ServerId 不一致时重复入账。
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
        var previousRaidApplied = new Dictionary<string, int>(prog.CurrentRaidApplied, StringComparer.OrdinalIgnoreCase);
        var previousRaidHadAppliedProgress = previousRaidApplied.Any(kv => kv.Value > 0);
        var authoritative = source == RaidTrackSource.ServerAuthoritative;
        var resumesPendingSupplemental = !authoritative
            && !string.IsNullOrWhiteSpace(raidId)
            && string.Equals(raidId, prog.PendingSupplementalRaidId, StringComparison.Ordinal);

        // 战局切换 / 乱序迟到判定
        if (!string.Equals(raidId, prog.CurrentRaidId, StringComparison.Ordinal))
        {
            // 已收尾战局的乱序迟到上报：忽略，避免重复入账
            if (!string.IsNullOrWhiteSpace(raidId) && prog.ProcessedRaidIds.Contains(raidId))
            {
                result.Duplicate = true;
                return result;
            }

            if (resumesPendingSupplemental)
            {
                prog.CurrentRaidId = raidId;
                prog.CurrentRaidApplied = new Dictionary<string, int>(
                    prog.PendingSupplementalRaidApplied ?? new Dictionary<string, int>(),
                    StringComparer.OrdinalIgnoreCase);
            }
            else
            {
            // 客户端补充上报切到新战局：归档旧局 id，重置本场已应用计数。
            // 服务端权威收尾即便 raidId 不同，也先保留当前基准，兼容旧版客户端已预先入账的同类进度。
                if (!authoritative)
                {
                    ArchiveRaid(prog, prog.CurrentRaidId);
                    prog.CurrentRaidApplied.Clear();
                    ClearPendingSupplemental(prog);
                }

                prog.CurrentRaidId = string.IsNullOrWhiteSpace(raidId) ? null : raidId;
            }
        }

        var templates = BattlePassStore.GetAllTasks().ToDictionary(t => t.Id);

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

            if (tpl.SingleRaid || tpl.OneLife)
            {
                // 单局/一命任务：进度即本场累计值（不跨局累加；切局时本值自然从小重新计）
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

            // 一命任务必须等权威战后结果确认 Survived；实时补充上报只能展示本局进度，绝不提前发奖。
            if (tpl.OneLife)
            {
                if (advanced && string.IsNullOrWhiteSpace(payload.ExitStatus))
                {
                    result.Credited.Add(new RaidTrackCredit { TaskId = active.TaskId, GainedXp = 0, Done = false });
                }

                continue;
            }

            if (active.Progress >= target)
            {
                // 统一结算：按任务 rewardMode 给 BP 经验 / 任务自带奖励 / 两者
                var xp = battlePassService.CreditTaskCompletion(profileId, prog, season, tpl);
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

        if (!string.IsNullOrWhiteSpace(payload.ExitStatus))
        {
            FinalizeOneLifeTasks(profileId, prog, season, templates, payload, result);
        }

        // 战局结束（上报带撤离状态）：归档本局，停止后续增量
        if (!string.IsNullOrWhiteSpace(payload.ExitStatus))
        {
            ArchiveRaid(prog, prog.CurrentRaidId);
            if (authoritative
                && previousRaidHadAppliedProgress
                && !string.IsNullOrWhiteSpace(previousRaidId)
                && !string.Equals(previousRaidId, prog.CurrentRaidId, StringComparison.Ordinal))
            {
                prog.PendingSupplementalRaidId = previousRaidId;
                prog.PendingSupplementalRaidApplied = previousRaidApplied;
            }
            else if (!authoritative)
            {
                ClearPendingSupplemental(prog);
            }

            prog.CurrentRaidId = null;
            prog.CurrentRaidApplied.Clear();
        }

        return result;
    }

    private void FinalizeOneLifeTasks(
        string profileId,
        BpProgress prog,
        BpSeason season,
        IReadOnlyDictionary<string, BpTaskTemplate> templates,
        RaidTrackPayload payload,
        RaidTrackResult result)
    {
        var survived = string.Equals(payload.ExitStatus, "Survived", StringComparison.OrdinalIgnoreCase);
        foreach (var active in prog.ActiveTasks)
        {
            if (active.CreditedXp
                || !templates.TryGetValue(active.TaskId, out var tpl)
                || !tpl.OneLife)
            {
                continue;
            }

            var target = Math.Max(1, tpl.Count);
            if (survived && active.Progress >= target)
            {
                var xp = battlePassService.CreditTaskCompletion(profileId, prog, season, tpl);
                active.CreditedXp = true;
                active.Progress = target;
                result.Credited.RemoveAll(credit => credit.TaskId == active.TaskId && !credit.Done);
                result.Credited.Add(new RaidTrackCredit { TaskId = active.TaskId, GainedXp = xp, Done = true });
                logger.Info($"[SPT-BattlePass] 一命任务达成 profile={profileId} task={active.TaskId} +{xp}xp (raid={payload.RaidId})");
            }
            else
            {
                active.Progress = 0;
                result.Credited.RemoveAll(credit => credit.TaskId == active.TaskId && !credit.Done);
            }
        }
    }

    private static bool TaskSourceCanProcess(BpTaskTemplate tpl, RaidTrackSource source)
    {
        var ct = tpl.ConditionType?.Trim() ?? "Kills";

        // 武器条件击杀统一使用客户端击杀瞬间抓到的枪身 tpl/整枪搭配：
        // 服务端 Victim.Weapon 的表示不保证是 tpl，且完全不含配件。每项任务只走一个来源，避免双计数。
        var killWithWeaponContext = string.Equals(ct, "Kills", StringComparison.OrdinalIgnoreCase)
            && ((tpl.Weapons?.Any(value => !string.IsNullOrWhiteSpace(value)) ?? false)
                || (tpl.WeaponCalibers?.Any(value => !string.IsNullOrWhiteSpace(value)) ?? false)
                || (tpl.WeaponMods?.Any(value => !string.IsNullOrWhiteSpace(value)) ?? false));

        var supplemental = killWithWeaponContext
            || string.Equals(ct, "VisitZone", StringComparison.OrdinalIgnoreCase)
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

    private static void ClearPendingSupplemental(BpProgress prog)
    {
        prog.PendingSupplementalRaidId = null;
        prog.PendingSupplementalRaidApplied.Clear();
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

        if (!WeaponModsMatch(tpl.WeaponMods, k.WeaponMods))
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

    /// <summary>
    ///     武器配件 inclusive 匹配（仿原版"试驾"weaponModsInclusive）：击杀所用武器须<b>同时</b>装有全部指定配件才计数。
    ///     配件数据只来自客户端上报（<see cref="BpKillEvent.WeaponMods"/>）；required 非空但本次击杀无配件信息 = 不匹配。
    /// </summary>
    private static bool WeaponModsMatch(IEnumerable<string>? required, IEnumerable<string>? actual)
    {
        var need = required?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (need is null || need.Count == 0)
        {
            return true;
        }

        var have = actual?.Where(v => !string.IsNullOrWhiteSpace(v)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return have is { Count: > 0 } && need.All(have.Contains);
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
