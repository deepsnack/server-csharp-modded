using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     商人任务读侧：任务清单、依赖图谱（解锁/互斥边）、单任务详情（条件+前置+奖励，本地化解析为中文）。
///     纯查内存 DB + 覆盖层状态标记；不做任何写。名称解析走 主→ch→en 三表链（复用 ItemSearchService 解析物品名）。
/// </summary>
[Injectable]
public class QuestGraphService(
    DatabaseService databaseService,
    LocaleService localeService,
    ItemSearchService itemSearch
)
{
    // ---- 商人清单（新建任务表单用） ----
    public List<BpQuestTraderInfo> GetTraders()
    {
        var result = new List<BpQuestTraderInfo>();
        foreach (var (traderId, trader) in databaseService.GetTables().Traders)
        {
            // 过滤掉 Fence 等非发任务商人？—— 保留全部，前端自行取舍；带昵称便于识别。
            result.Add(new BpQuestTraderInfo
            {
                TraderId = traderId.ToString(),
                Name = trader.Base?.Nickname ?? traderId.ToString(),
            });
        }

        return result.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- 任务清单 ----
    /// <summary>列出任务（可按商人/关键字过滤）。附带禁用/自定义/奖励覆盖状态标记。</summary>
    public List<BpQuestListItem> ListQuests(string? traderId, string? query)
    {
        var q = (query ?? "").Trim();
        var wantTrader = !string.IsNullOrWhiteSpace(traderId) && MongoIdEx.TryParse(traderId, out _);

        var overrides = BattlePassStore.GetQuestOverrides()
            .Where(o => MongoIdEx.TryParse(o.QuestId, out _))
            .GroupBy(o => o.QuestId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var customIds = BattlePassStore.GetCustomQuests()
            .Select(c => c.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mainLocale = localeService.GetLocaleDb();
        var chLocale = localeService.GetLocaleDb("ch");
        var enLocale = localeService.GetLocaleDb("en");

        var result = new List<BpQuestListItem>();
        foreach (var (questId, quest) in databaseService.GetQuests())
        {
            var idStr = questId.ToString();
            if (wantTrader && !string.Equals(quest.TraderId.ToString(), traderId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = ResolveQuestName(idStr, quest, mainLocale, chLocale, enLocale);
            if (q.Length > 0
                && !name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !idStr.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !(quest.QuestName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            overrides.TryGetValue(idStr, out var ov);
            result.Add(new BpQuestListItem
            {
                QuestId = idStr,
                Name = name,
                TraderId = quest.TraderId.ToString(),
                TraderName = ResolveTraderName(quest.TraderId),
                Side = quest.Side,
                Disabled = ov?.Disabled ?? false,
                HasRewardOverride = ov?.Rewards is { Count: > 0 },
                IsCustom = customIds.Contains(idStr),
                UnlockCount = CountUnlockPrereqs(quest),
                FailCount = quest.Conditions?.Fail?.Count(c => string.Equals(c.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase)) ?? 0,
            });
        }

        return result.OrderBy(x => x.TraderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- 依赖图谱 ----
    /// <summary>构建任务依赖图：解锁边（A 需 B 达某状态 → B→A）、互斥/失败边（A 完成使 B 失败 → A⇒B）。</summary>
    public BpQuestGraph GetGraph(string? traderId)
    {
        var wantTrader = !string.IsNullOrWhiteSpace(traderId) && MongoIdEx.TryParse(traderId, out _);
        var quests = databaseService.GetQuests();

        var overrides = BattlePassStore.GetQuestOverrides()
            .GroupBy(o => o.QuestId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var customIds = BattlePassStore.GetCustomQuests()
            .Select(c => c.Id).Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mainLocale = localeService.GetLocaleDb();
        var chLocale = localeService.GetLocaleDb("ch");
        var enLocale = localeService.GetLocaleDb("en");

        // 按商人过滤时，节点集为该商人任务 + 其直接前置（便于看清跨商人依赖）
        bool InScope(Quest quest) => !wantTrader
                                     || string.Equals(quest.TraderId.ToString(), traderId, StringComparison.OrdinalIgnoreCase);

        var nodes = new List<BpQuestGraphNode>();
        var edges = new List<BpQuestGraphEdge>();
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (questId, quest) in quests)
        {
            if (!InScope(quest))
            {
                continue;
            }

            var idStr = questId.ToString();
            included.Add(idStr);

            // 解锁边：本任务 AvailableForStart 的 Quest 条件 → prereq→this
            foreach (var cond in quest.Conditions?.AvailableForStart ?? new List<QuestCondition>())
            {
                if (!string.Equals(cond.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var target in ExtractTargets(cond.Target))
                {
                    edges.Add(new BpQuestGraphEdge
                    {
                        From = target,
                        To = idStr,
                        Kind = "unlock",
                        Status = cond.Status is null ? null : string.Join(",", cond.Status.Select(s => (int)s)),
                    });
                }
            }

            // 失败/互斥边：本任务 Fail 的 Quest 条件 → this⇒target 会失败
            foreach (var cond in quest.Conditions?.Fail ?? new List<QuestCondition>())
            {
                if (!string.Equals(cond.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var target in ExtractTargets(cond.Target))
                {
                    edges.Add(new BpQuestGraphEdge { From = idStr, To = target, Kind = "fail" });
                }
            }
        }

        // 收集边端点里尚未纳入的任务（跨商人前置/失败目标），补成节点（标记 external）
        var referenced = new HashSet<string>(edges.SelectMany(e => new[] { e.From, e.To }), StringComparer.OrdinalIgnoreCase);
        foreach (var idStr in included.Concat(referenced).Distinct().ToList())
        {
            if (!MongoIdEx.TryParse(idStr, out var qid) || !quests.TryGetValue(qid, out var quest))
            {
                continue;
            }

            overrides.TryGetValue(idStr, out var ov);
            nodes.Add(new BpQuestGraphNode
            {
                QuestId = idStr,
                Name = ResolveQuestName(idStr, quest, mainLocale, chLocale, enLocale),
                TraderId = quest.TraderId.ToString(),
                TraderName = ResolveTraderName(quest.TraderId),
                Disabled = ov?.Disabled ?? false,
                IsCustom = customIds.Contains(idStr),
                External = wantTrader && !string.Equals(quest.TraderId.ToString(), traderId, StringComparison.OrdinalIgnoreCase),
            });
        }

        // 只保留两端都在节点集里的边
        var nodeIds = nodes.Select(n => n.QuestId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        edges = edges.Where(e => nodeIds.Contains(e.From) && nodeIds.Contains(e.To)).ToList();

        return new BpQuestGraph { Nodes = nodes, Edges = edges };
    }

    // ---- 单任务详情 ----
    public BpQuestDetail? GetQuestDetail(string questId)
    {
        if (!MongoIdEx.TryParse(questId, out var qid) || !databaseService.GetQuests().TryGetValue(qid, out var quest))
        {
            return null;
        }

        var mainLocale = localeService.GetLocaleDb();
        var chLocale = localeService.GetLocaleDb("ch");
        var enLocale = localeService.GetLocaleDb("en");

        var overrides = BattlePassStore.GetQuestOverrides()
            .LastOrDefault(o => string.Equals(o.QuestId, questId, StringComparison.OrdinalIgnoreCase));
        var isCustom = BattlePassStore.GetCustomQuests()
            .Any(c => string.Equals(c.Id, questId, StringComparison.OrdinalIgnoreCase));

        // 前置（AvailableForStart 的 Quest 条件）
        var prereqs = new List<BpQuestPrereqView>();
        foreach (var cond in quest.Conditions?.AvailableForStart ?? new List<QuestCondition>())
        {
            if (!string.Equals(cond.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var target in ExtractTargets(cond.Target))
            {
                var name = MongoIdEx.TryParse(target, out var tqid)
                           && databaseService.GetQuests().TryGetValue(tqid, out var tq)
                    ? ResolveQuestName(target, tq, mainLocale, chLocale, enLocale)
                    : target;
                prereqs.Add(new BpQuestPrereqView
                {
                    QuestId = target,
                    Name = name,
                    Status = cond.Status?.Select(s => (int)s).ToList() ?? new List<int>(),
                    AvailableAfter = cond.AvailableAfter ?? 0,
                });
            }
        }

        // 完成目标（AvailableForFinish）— 概要展示
        var objectives = new List<BpQuestObjectiveView>();
        foreach (var cond in quest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
        {
            objectives.Add(new BpQuestObjectiveView
            {
                ConditionType = cond.ConditionType,
                Value = cond.Value,
                Targets = ExtractTargets(cond.Target),
                OnlyFoundInRaid = cond.OnlyFoundInRaid ?? false,
            });
        }

        // 奖励（按桶）— 解析物品名
        var rewards = new Dictionary<string, List<BpQuestRewardView>>();
        foreach (var (bucket, list) in quest.Rewards ?? new Dictionary<string, List<Reward>>())
        {
            rewards[bucket] = list.Select(r => ToRewardView(r)).ToList();
        }

        return new BpQuestDetail
        {
            QuestId = questId,
            Name = ResolveQuestName(questId, quest, mainLocale, chLocale, enLocale),
            Description = ResolveLocale($"{questId} description", mainLocale, chLocale, enLocale),
            QuestName = quest.QuestName,
            TraderId = quest.TraderId.ToString(),
            TraderName = ResolveTraderName(quest.TraderId),
            Side = quest.Side,
            Location = quest.Location,
            Type = quest.Type.ToString(),
            Disabled = overrides?.Disabled ?? false,
            HasRewardOverride = overrides?.Rewards is { Count: > 0 },
            IsCustom = isCustom,
            Prerequisites = prereqs,
            Objectives = objectives,
            Rewards = rewards,
        };
    }

    private BpQuestRewardView ToRewardView(Reward r)
    {
        var view = new BpQuestRewardView
        {
            Type = r.Type?.ToString() ?? "Item",
            Value = r.Value,
        };

        if (r.Type == RewardType.Item && r.Items is { Count: > 0 })
        {
            var tpl = r.Items[0].Template;
            view.Tpl = tpl.ToString();
            view.Name = itemSearch.ResolveItemNameZh(tpl);
            view.Count = (int)(r.Items[0].Upd?.StackObjectsCount ?? (r.Value ?? 1));
        }
        else if (r.Type is RewardType.TraderStanding or RewardType.TraderUnlock)
        {
            view.TraderId = r.Target;
            view.Name = r.Target is not null && MongoIdEx.TryParse(r.Target, out var tid)
                ? ResolveTraderName(tid)
                : r.Target;
        }

        return view;
    }

    // ---- 名称/本地化解析 ----
    public string ResolveQuestName(string questId, Quest quest,
        Dictionary<string, string> main, Dictionary<string, string> ch, Dictionary<string, string> en)
    {
        var name = ResolveLocale($"{questId} name", main, ch, en);
        if (!string.IsNullOrWhiteSpace(name) && name != "UNKNOWN")
        {
            return name;
        }

        return quest.Name ?? quest.QuestName ?? questId;
    }

    private static string ResolveLocale(string key,
        Dictionary<string, string> main, Dictionary<string, string> ch, Dictionary<string, string> en)
    {
        if (main.TryGetValue(key, out var m) && m.Length > 0) return m;
        if (ch.TryGetValue(key, out var c) && c.Length > 0) return c;
        if (en.TryGetValue(key, out var e) && e.Length > 0) return e;
        return "";
    }

    private string ResolveTraderName(MongoId traderId)
    {
        return databaseService.GetTables().Traders.TryGetValue(traderId, out var trader)
            ? trader.Base?.Nickname ?? traderId.ToString()
            : traderId.ToString();
    }

    private static int CountUnlockPrereqs(Quest quest)
    {
        return quest.Conditions?.AvailableForStart?
            .Count(c => string.Equals(c.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase)) ?? 0;
    }

    private static List<string> ExtractTargets(Utils.Json.ListOrT<string>? target)
    {
        if (target is null)
        {
            return new List<string>();
        }

        if (target.IsList)
        {
            return target.List!.Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        return target.Item is null ? new List<string>() : new List<string> { target.Item };
    }
}

// ============================================================================
//  商人任务读侧 DTO（仅 API 响应用，非持久化）
// ============================================================================

public sealed record BpQuestTraderInfo
{
    [JsonPropertyName("traderId")]
    public string TraderId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed record BpQuestListItem
{
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("traderId")]
    public string TraderId { get; set; } = "";

    [JsonPropertyName("traderName")]
    public string TraderName { get; set; } = "";

    [JsonPropertyName("side")]
    public string? Side { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("hasRewardOverride")]
    public bool HasRewardOverride { get; set; }

    [JsonPropertyName("isCustom")]
    public bool IsCustom { get; set; }

    [JsonPropertyName("unlockCount")]
    public int UnlockCount { get; set; }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }
}

public sealed record BpQuestGraph
{
    [JsonPropertyName("nodes")]
    public List<BpQuestGraphNode> Nodes { get; set; } = new();

    [JsonPropertyName("edges")]
    public List<BpQuestGraphEdge> Edges { get; set; } = new();
}

public sealed record BpQuestGraphNode
{
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("traderId")]
    public string TraderId { get; set; } = "";

    [JsonPropertyName("traderName")]
    public string TraderName { get; set; } = "";

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("isCustom")]
    public bool IsCustom { get; set; }

    [JsonPropertyName("external")]
    public bool External { get; set; }
}

public sealed record BpQuestGraphEdge
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    /// <summary>unlock（前置解锁）/ fail（完成使对方失败）。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "unlock";

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed record BpQuestPrereqView
{
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public List<int> Status { get; set; } = new();

    [JsonPropertyName("availableAfter")]
    public int AvailableAfter { get; set; }
}

public sealed record BpQuestObjectiveView
{
    [JsonPropertyName("conditionType")]
    public string ConditionType { get; set; } = "";

    [JsonPropertyName("value")]
    public double? Value { get; set; }

    [JsonPropertyName("targets")]
    public List<string> Targets { get; set; } = new();

    [JsonPropertyName("onlyFoundInRaid")]
    public bool OnlyFoundInRaid { get; set; }
}

public sealed record BpQuestRewardView
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Item";

    [JsonPropertyName("tpl")]
    public string? Tpl { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }

    [JsonPropertyName("traderId")]
    public string? TraderId { get; set; }
}

public sealed record BpQuestDetail
{
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("questName")]
    public string? QuestName { get; set; }

    [JsonPropertyName("traderId")]
    public string TraderId { get; set; } = "";

    [JsonPropertyName("traderName")]
    public string TraderName { get; set; } = "";

    [JsonPropertyName("side")]
    public string? Side { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("hasRewardOverride")]
    public bool HasRewardOverride { get; set; }

    [JsonPropertyName("isCustom")]
    public bool IsCustom { get; set; }

    [JsonPropertyName("prerequisites")]
    public List<BpQuestPrereqView> Prerequisites { get; set; } = new();

    [JsonPropertyName("objectives")]
    public List<BpQuestObjectiveView> Objectives { get; set; } = new();

    [JsonPropertyName("rewards")]
    public Dictionary<string, List<BpQuestRewardView>> Rewards { get; set; } = new();
}
