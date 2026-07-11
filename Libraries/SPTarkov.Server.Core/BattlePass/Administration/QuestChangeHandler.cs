using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     商人任务模块命令处理器：原版任务覆盖（禁用 / 奖励替换）与自定义任务新增/编辑/删除。
///     <para>写 <c>quest-overrides.json</c> / <c>custom-quests.json</c> 后调用 <see cref="QuestSync.Sync"/>
///     热重放进内存 DB（可逆、无需重启，绝不回写 5.6MB 的 quests.json）。</para>
///     <para>统一供正常管理员即时写入（各自控制器端点）与协管审核放行（/review/submit）复用同一业务逻辑。</para>
/// </summary>
[Injectable]
public class QuestChangeHandler(
    QuestSync questSync,
    ISptLogger<QuestChangeHandler> logger
) : IBattlePassChangeHandler
{
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Module => "quests";
    public IReadOnlyList<string> CommandTypes { get; } = ["quest.override", "quest.customUpsert", "quest.customDelete"];
    public string RequiredCapability => "quests.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        return commandType switch
        {
            "quest.override" => NormalizeOverride(input),
            "quest.customUpsert" => NormalizeCustom(input),
            "quest.customDelete" => NormalizeDelete(input),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "quest.override" => ValidateOverride((BpQuestOverride) normalizedInput),
            "quest.customUpsert" => ValidateCustom((BpCustomQuest) normalizedInput),
            "quest.customDelete" => ValidateDelete((QuestDeleteInput) normalizedInput),
            _ => $"未知命令类型: {commandType}",
        };
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        switch (commandType)
        {
            case "quest.override":
                var ov = (BpQuestOverride) normalizedInput;
                var parts = new List<string>();
                if (ov.Disabled)
                {
                    parts.Add("禁用");
                }
                else
                {
                    parts.Add("启用");
                }

                if (ov.Rewards is { Count: > 0 })
                {
                    parts.Add($"替换奖励({string.Join("/", ov.Rewards.Keys)})");
                }

                return $"原版任务覆盖 {ov.QuestId}：{string.Join("，", parts)}";
            case "quest.customUpsert":
                var cq = (BpCustomQuest) normalizedInput;
                var verb = currentState is null ? "新建" : "编辑";
                return $"{verb}自定义商人任务「{cq.NameZh}」（商人={cq.TraderId}，目标 {cq.Objectives.Count} 项，奖励 {cq.Rewards.Count} 项）";
            case "quest.customDelete":
                return $"删除自定义任务 id={((QuestDeleteInput) normalizedInput).Id}";
            default:
                return commandType;
        }
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "quest.override" => $"quests:override:{((BpQuestOverride) normalizedInput).QuestId}",
            "quest.customUpsert" => $"quests:custom:{((BpCustomQuest) normalizedInput).Id}",
            "quest.customDelete" => $"quests:custom:{((QuestDeleteInput) normalizedInput).Id}",
            _ => $"quests:unknown:{commandType}",
        };
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "quest.override" => ((BpQuestOverride) normalizedInput).QuestId,
            "quest.customUpsert" => ((BpCustomQuest) normalizedInput).NameZh,
            "quest.customDelete" => ((QuestDeleteInput) normalizedInput).Id,
            _ => commandType,
        };
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        // targetKey format: quests:<type>:<id>
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3)
        {
            return null;
        }

        return parts[1] switch
        {
            "override" => BattlePassStore.GetQuestOverrides()
                .FirstOrDefault(o => string.Equals(o.QuestId, parts[2], StringComparison.OrdinalIgnoreCase)),
            "custom" => BattlePassStore.GetCustomQuests()
                .FirstOrDefault(c => string.Equals(c.Id, parts[2], StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }

    public string GetRevision(object? snapshot)
    {
        if (snapshot is null)
        {
            return "";
        }

        var json = JsonSerializer.Serialize(snapshot, snapshot.GetType(), SerializeOpts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public string ApplyAndActivate(string commandType, object normalizedInput, string? expectedBaseRevision, string? changeId)
    {
        var targetKey = GetTargetKey(commandType, normalizedInput);

        if (!string.IsNullOrEmpty(expectedBaseRevision))
        {
            var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
            if (!string.Equals(currentRevision, expectedBaseRevision, StringComparison.Ordinal))
            {
                throw new ChangeConflictException(
                    $"目标 {targetKey} 基线已变化：期望 {expectedBaseRevision}，当前 {currentRevision}");
            }
        }

        return commandType switch
        {
            "quest.override" => ApplyOverride((BpQuestOverride) normalizedInput),
            "quest.customUpsert" => ApplyCustomUpsert((BpCustomQuest) normalizedInput),
            "quest.customDelete" => ApplyCustomDelete((QuestDeleteInput) normalizedInput),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
        {
            throw new ChangeConflictException(
                $"回溯目标 {targetKey} 已发生后续改动：期望 {expectedCurrentRevision}，当前 {currentRevision}");
        }

        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3)
        {
            throw new InvalidOperationException($"不支持回溯目标: {targetKey}");
        }

        return parts[1] switch
        {
            "override" => beforeSnapshot is null
                ? ApplyOverride(new BpQuestOverride { QuestId = parts[2] }) // 空覆盖 → 还原原版
                : ApplyOverride(BattlePassSnapshotCodec.Deserialize<BpQuestOverride>(beforeSnapshot)),
            "custom" => beforeSnapshot is null
                ? ApplyCustomDelete(new QuestDeleteInput { Id = parts[2] })
                : ApplyCustomUpsert(BattlePassSnapshotCodec.Deserialize<BpCustomQuest>(beforeSnapshot)),
            _ => throw new InvalidOperationException($"不支持回溯目标: {targetKey}"),
        };
    }

    // ---- Normalize ----

    private static BpQuestOverride NormalizeOverride(JsonElement input)
    {
        var ov = JsonSerializer.Deserialize<BpQuestOverride>(input.GetRawText(), SerializeOpts)
                 ?? throw new ArgumentException("无法解析任务覆盖数据");
        ov.QuestId = (ov.QuestId ?? "").Trim();
        NormalizeRewardBuckets(ov.Rewards);
        ov.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return ov;
    }

    private static BpCustomQuest NormalizeCustom(JsonElement input)
    {
        var cq = JsonSerializer.Deserialize<BpCustomQuest>(input.GetRawText(), SerializeOpts)
                 ?? throw new ArgumentException("无法解析自定义任务数据");

        // 空 id 生成稳定 MongoId（targetKey 稳定，审核落盘/批准全程一致）
        cq.Id = string.IsNullOrWhiteSpace(cq.Id) ? new MongoId().ToString() : cq.Id.Trim();
        cq.TraderId = (cq.TraderId ?? "").Trim();
        cq.QuestName = (cq.QuestName ?? "").Trim();
        cq.NameZh = (cq.NameZh ?? "").Trim();
        cq.DescriptionZh = (cq.DescriptionZh ?? "").Trim();
        cq.Side = string.IsNullOrWhiteSpace(cq.Side) ? "Pmc" : cq.Side.Trim();
        cq.Location = string.IsNullOrWhiteSpace(cq.Location) ? "any" : cq.Location.Trim();
        cq.Prerequisites ??= new List<BpQuestPrereq>();
        cq.Objectives ??= new List<BpQuestObjective>();
        cq.Rewards ??= new List<BpQuestReward>();
        foreach (var pre in cq.Prerequisites)
        {
            pre.QuestId = (pre.QuestId ?? "").Trim();
            pre.Status = pre.Status is { Count: > 0 } ? pre.Status : new List<int> { 4 };
        }

        foreach (var obj in cq.Objectives)
        {
            obj.Type = string.IsNullOrWhiteSpace(obj.Type) ? "handoverItem" : obj.Type.Trim();
            obj.Tpl = obj.Tpl?.Trim();
            obj.Count = Math.Max(1, obj.Count);
            obj.Target = string.IsNullOrWhiteSpace(obj.Target) ? "Any" : obj.Target.Trim();
        }

        NormalizeRewardList(cq.Rewards);
        cq.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return cq;
    }

    private static QuestDeleteInput NormalizeDelete(JsonElement input)
    {
        var id = input.TryGetProperty("id", out var prop) ? prop.GetString()?.Trim() : null;
        return new QuestDeleteInput { Id = id ?? "" };
    }

    private static void NormalizeRewardBuckets(Dictionary<string, List<BpQuestReward>>? buckets)
    {
        if (buckets is null)
        {
            return;
        }

        foreach (var list in buckets.Values)
        {
            NormalizeRewardList(list);
        }
    }

    private static void NormalizeRewardList(List<BpQuestReward>? list)
    {
        if (list is null)
        {
            return;
        }

        foreach (var r in list)
        {
            r.Type = string.IsNullOrWhiteSpace(r.Type) ? "item" : r.Type.Trim();
            r.Tpl = r.Tpl?.Trim();
            r.TraderId = r.TraderId?.Trim();
            r.Count = Math.Max(1, r.Count);
        }
    }

    // ---- Validate ----

    private static string? ValidateOverride(BpQuestOverride ov)
    {
        if (!MongoId.IsValidMongoId(ov.QuestId))
        {
            return "任务 id 无效";
        }

        if (ov.Rewards is not null)
        {
            foreach (var (bucket, list) in ov.Rewards)
            {
                if (bucket is not ("Started" or "Success" or "Fail"))
                {
                    return $"奖励桶名非法: {bucket}（仅 Started/Success/Fail）";
                }

                var err = ValidateRewardList(list);
                if (err is not null)
                {
                    return err;
                }
            }
        }

        return null;
    }

    private static string? ValidateCustom(BpCustomQuest cq)
    {
        if (!MongoId.IsValidMongoId(cq.Id))
        {
            return "任务 id 无效";
        }

        if (!MongoId.IsValidMongoId(cq.TraderId))
        {
            return "商人 id 无效";
        }

        if (string.IsNullOrWhiteSpace(cq.NameZh))
        {
            return "中文任务名必填";
        }

        if (cq.Objectives.Count == 0)
        {
            return "至少需要一个完成目标";
        }

        foreach (var obj in cq.Objectives)
        {
            if (obj.Type is not ("handoverItem" or "kills"))
            {
                return $"目标类型暂不支持: {obj.Type}（仅 handoverItem/kills）";
            }

            if (obj.Type == "handoverItem" && !MongoId.IsValidMongoId(obj.Tpl))
            {
                return "上交物品目标的 tpl 无效";
            }
        }

        foreach (var pre in cq.Prerequisites)
        {
            if (!MongoId.IsValidMongoId(pre.QuestId))
            {
                return $"前置任务 id 无效: {pre.QuestId}";
            }
        }

        return ValidateRewardList(cq.Rewards);
    }

    private static string? ValidateDelete(QuestDeleteInput input)
    {
        return string.IsNullOrWhiteSpace(input.Id) ? "缺少 id" : null;
    }

    private static string? ValidateRewardList(List<BpQuestReward>? list)
    {
        if (list is null)
        {
            return null;
        }

        foreach (var r in list)
        {
            var type = (r.Type ?? "item").ToLowerInvariant();
            switch (type)
            {
                case "item":
                    if (!MongoId.IsValidMongoId(r.Tpl))
                    {
                        return "物品奖励的 tpl 无效";
                    }

                    break;
                case "experience":
                    break;
                case "traderstanding":
                case "traderunlock":
                    if (!MongoId.IsValidMongoId(r.TraderId))
                    {
                        return $"{type} 奖励的商人 id 无效";
                    }

                    break;
                default:
                    return $"奖励类型暂不支持: {r.Type}（仅 item/experience/traderStanding/traderUnlock）";
            }
        }

        return null;
    }

    // ---- Apply ----

    private string ApplyOverride(BpQuestOverride ov)
    {
        var list = BattlePassStore.GetQuestOverrides();
        list.RemoveAll(o => string.Equals(o.QuestId, ov.QuestId, StringComparison.OrdinalIgnoreCase));

        // 空覆盖（未禁用且无奖励替换）不落盘 → 等价还原原版
        var isEmpty = !ov.Disabled && (ov.Rewards is null || ov.Rewards.Count == 0);
        if (!isEmpty)
        {
            list.Add(ov);
        }

        BattlePassStore.SaveQuestOverrides(list);
        questSync.Sync();

        var saved = BattlePassStore.GetQuestOverrides()
            .FirstOrDefault(o => string.Equals(o.QuestId, ov.QuestId, StringComparison.OrdinalIgnoreCase));
        return GetRevision(saved);
    }

    private string ApplyCustomUpsert(BpCustomQuest cq)
    {
        var list = BattlePassStore.GetCustomQuests();
        list.RemoveAll(c => string.Equals(c.Id, cq.Id, StringComparison.OrdinalIgnoreCase));
        list.Add(cq);
        BattlePassStore.SaveCustomQuests(list);
        questSync.Sync();

        var saved = BattlePassStore.GetCustomQuests()
            .FirstOrDefault(c => string.Equals(c.Id, cq.Id, StringComparison.OrdinalIgnoreCase));
        return GetRevision(saved);
    }

    private string ApplyCustomDelete(QuestDeleteInput input)
    {
        var list = BattlePassStore.GetCustomQuests();
        list.RemoveAll(c => string.Equals(c.Id, input.Id, StringComparison.OrdinalIgnoreCase));
        BattlePassStore.SaveCustomQuests(list);
        questSync.Sync();
        return GetRevision(null);
    }
}

/// <summary>任务删除/自定义任务删除命令的规范化输入。</summary>
public record QuestDeleteInput
{
    public string Id { get; set; } = "";
}
