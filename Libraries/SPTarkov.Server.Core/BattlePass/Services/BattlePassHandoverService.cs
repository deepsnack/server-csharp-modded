using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     网页「上交物品」(HandoverItem)：玩家在通行证网页把存档中符合要求的物品上交以推进任务。
///     <para>入口明确：<b>HandoverItem 只能在网页上交</b>（读 PMC 存档→按 itemRequirements 匹配→移除→累计进度→落盘）；
///     而 FindItem 仍是「带出撤离即计」。两者不再混同，避免「在游戏内交还是网页交」的歧义。</para>
///     <para>FiR 规则<b>跟随任务自身</b>：<see cref="BpTaskTemplate.FindInRaid"/>=true 时只接受战局中找到(SpawnedInSession)的物品。</para>
///     <para>安全：只从<b>仓库(Stash)</b>内取、跳过带子物品的容器（避免连带删除嵌套战利品），支持堆叠部分扣减。</para>
/// </summary>
[Injectable]
public class BattlePassHandoverService(
    BattlePassService battlePassService,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    BattlePassStashService stashService,
    ItemSearchService itemSearchService,
    ISptLogger<BattlePassHandoverService> logger
)
{
    /// <summary>列出某 HandoverItem 任务当前可上交的存档物品明细（不改动任何状态）。</summary>
    public (bool ok, string message, HandoverInfo? info) GetEligible(string profileId)
    {
        return GetEligible(profileId, taskId: null);
    }

    public (bool ok, string message, HandoverInfo? info) GetEligible(string profileId, string? taskId)
    {
        if (!TryResolve(profileId, taskId, out var ctx, out var message))
        {
            return (false, message, null);
        }

        var pmc = profileHelper.GetPmcProfile(new MongoId(profileId));
        if (pmc?.Inventory?.Items is null)
        {
            return (false, "未找到玩家档案库存", null);
        }

        var wanted = WantedTpls(ctx.Template);
        var lines = new List<HandoverItemLine>();
        foreach (var tpl in wanted)
        {
            var available = stashService.CountTpl(pmc, tpl, ctx.Template.FindInRaid);
            lines.Add(new HandoverItemLine
            {
                Tpl = tpl,
                Name = NameOf(ctx.Template, tpl),
                Available = available,
            });
        }

        var info = new HandoverInfo
        {
            TaskId = ctx.Active.TaskId,
            Title = ctx.Template.Title,
            Target = ctx.Target,
            Progress = ctx.Active.Progress,
            Remaining = Math.Max(0, ctx.Target - ctx.Active.Progress),
            FindInRaid = ctx.Template.FindInRaid,
            Completed = ctx.Active.CreditedXp,
            Items = lines,
        };
        return (true, "", info);
    }

    /// <summary>执行上交：从仓库移除匹配物品（至多补足剩余所需），累计任务进度并落盘。</summary>
    public (bool ok, string message, HandoverResult? result) Handover(string profileId, string? taskId)
    {
        if (!TryResolve(profileId, taskId, out var ctx, out var message))
        {
            return (false, message, null);
        }

        if (ctx.Active.CreditedXp)
        {
            return (false, "该任务已完成", null);
        }

        var remaining = Math.Max(0, ctx.Target - ctx.Active.Progress);
        if (remaining <= 0)
        {
            return (false, "该任务进度已满，无需上交", null);
        }

        var pmc = profileHelper.GetPmcProfile(new MongoId(profileId));
        if (pmc?.Inventory?.Items is null)
        {
            return (false, "未找到玩家档案库存", null);
        }

        var sessionId = new MongoId(profileId);
        var wanted = WantedTpls(ctx.Template);
        var handed = 0;

        foreach (var tpl in wanted)
        {
            if (handed >= remaining)
            {
                break;
            }

            handed += stashService.RemoveTpl(pmc, sessionId, tpl, remaining - handed, ctx.Template.FindInRaid);
        }

        if (handed <= 0)
        {
            return (false, "存档仓库中没有可上交的物品", null);
        }

        var season = BattlePassStore.GetSeason();
        ctx.Active.Progress += handed;
        var gainedXp = 0;
        if (ctx.Active.Progress >= ctx.Target)
        {
            ctx.Active.Progress = ctx.Target;
            // 统一结算：按任务 rewardMode 给 BP 经验 / 任务自带奖励 / 两者
            gainedXp = battlePassService.CreditTaskCompletion(profileId, ctx.Progress, season, ctx.Template);
            ctx.Active.CreditedXp = true;
        }

        // 落盘：档案(移除了物品) + 通行证进度
        try
        {
            saveServer.SaveProfileAsync(sessionId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] 上交后保存档案失败（内存态已生效）profile={profileId}: {ex.Message}");
        }

        BattlePassStore.SaveProgress(profileId, ctx.Progress);
        logger.Info($"[SPT-BattlePass] 网页上交 profile={profileId} task={ctx.Active.TaskId} 交={handed} 进度={ctx.Active.Progress}/{ctx.Target} +{gainedXp}xp");

        return (true, "", new HandoverResult
        {
            TaskId = ctx.Active.TaskId,
            Handed = handed,
            Progress = ctx.Active.Progress,
            Target = ctx.Target,
            Completed = ctx.Active.CreditedXp,
            GainedXp = gainedXp,
        });
    }

    // ============================ 内部 ============================

    private sealed class HandoverContext
    {
        public required BpProgress Progress { get; init; }
        public required BpActiveTask Active { get; init; }
        public required BpTaskTemplate Template { get; init; }
        public int Target { get; init; }
    }

    private bool TryResolve(string profileId, string? taskId, out HandoverContext ctx, out string message)
    {
        ctx = null!;
        message = "";
        var season = BattlePassStore.GetSeason();
        var prog = battlePassService.GetOrResetProgress(profileId, season);

        var templates = BattlePassStore.GetAllTasks().ToDictionary(t => t.Id);
        var candidates = prog.ActiveTasks
            .Where(a => templates.TryGetValue(a.TaskId, out var t)
                && string.Equals(t.ConditionType?.Trim(), "HandoverItem", StringComparison.OrdinalIgnoreCase))
            .ToList();

        BpActiveTask? active;
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            active = candidates.FirstOrDefault(a => string.Equals(a.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            active = candidates.Count == 1 ? candidates[0] : null;
        }

        if (active is null)
        {
            message = candidates.Count == 0 ? "当前没有「上交物品」任务" : "请指定要上交的任务";
            return false;
        }

        var template = templates[active.TaskId];
        ctx = new HandoverContext
        {
            Progress = prog,
            Active = active,
            Template = template,
            Target = Math.Max(1, template.Count),
        };
        return true;
    }

    private static HashSet<string> WantedTpls(BpTaskTemplate tpl)
    {
        return (tpl.ItemRequirements ?? new List<BpTaskItemRequirement>())
            .Select(r => r.Tpl)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string NameOf(BpTaskTemplate tpl, string itemTpl)
    {
        var req = tpl.ItemRequirements?.FirstOrDefault(r => string.Equals(r.Tpl, itemTpl, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(req?.Name))
        {
            return req!.Name!;
        }

        var resolved = MongoId.IsValidMongoId(itemTpl) ? itemSearchService.ResolveItemName(new MongoId(itemTpl)) : "";
        return string.IsNullOrWhiteSpace(resolved) ? itemTpl : resolved;
    }
}

/// <summary>上交物品任务的当前可上交明细（GET）。</summary>
public class HandoverInfo
{
    public string TaskId { get; set; } = "";
    public string Title { get; set; } = "";
    public int Target { get; set; }
    public int Progress { get; set; }
    public int Remaining { get; set; }
    public bool FindInRaid { get; set; }
    public bool Completed { get; set; }
    public List<HandoverItemLine> Items { get; set; } = new();
}

public class HandoverItemLine
{
    public string Tpl { get; set; } = "";
    public string Name { get; set; } = "";
    public int Available { get; set; }
}

/// <summary>一次上交的结果（POST）。</summary>
public class HandoverResult
{
    public string TaskId { get; set; } = "";
    public int Handed { get; set; }
    public int Progress { get; set; }
    public int Target { get; set; }
    public bool Completed { get; set; }
    public int GainedXp { get; set; }
}
