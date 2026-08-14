using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     物品管控（获取途径覆盖）模块命令处理器：新增/移除获取途径（item-overrides.json）。
///     <para>一条 override 即一次编辑（op=add/remove，按 source 取不同定位字段）；应用后调用
///     <see cref="ItemControlSync.Sync"/> 即时重放对账（内存 DB 增删即时生效，无需重启）。
///     全局物品封禁（bans）与快捷跳蚤黑名单属独立操作，不在此处理。</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ItemsChangeHandler(
    ItemControlSync controlSync,
    ISptLogger<ItemsChangeHandler> logger
) : IBattlePassChangeHandler
{
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Module => "items";
    public IReadOnlyList<string> CommandTypes { get; } = ["item.edit", "item.override.delete"];
    public string RequiredCapability => "items.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        return commandType switch
        {
            "item.edit" => NormalizeEdit(input),
            "item.override.delete" => NormalizeDelete(input),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "item.edit" => ValidateEdit((BpItemOverride) normalizedInput),
            "item.override.delete" => ValidateDelete((ItemOverrideDeleteInput) normalizedInput),
            _ => $"未知命令类型: {commandType}",
        };
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        switch (commandType)
        {
            case "item.edit":
                var ov = (BpItemOverride) normalizedInput;
                var opVerb = ov.Op == "add" ? "新增" : "移除";
                return $"{opVerb}获取途径 {SourceLabel(ov.Source)} · 物品={ov.Tpl}";
            case "item.override.delete":
                var del = (ItemOverrideDeleteInput) normalizedInput;
                return $"撤销获取途径编辑 id={del.Id}";
            default:
                return commandType;
        }
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "item.edit" => $"items:override:{((BpItemOverride) normalizedInput).Id}",
            "item.override.delete" => $"items:override:{((ItemOverrideDeleteInput) normalizedInput).Id}",
            _ => $"items:unknown:{commandType}",
        };
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "item.edit" => ((BpItemOverride) normalizedInput).Tpl,
            "item.override.delete" => ((ItemOverrideDeleteInput) normalizedInput).Id,
            _ => commandType,
        };
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        // targetKey format: items:override:<id>
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3 || parts[1] != "override") return null;

        var id = parts[2];
        return BattlePassStore.GetItemOverrides()
            .FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal));
    }

    public string GetRevision(object? snapshot)
    {
        if (snapshot is null) return "";
        var json = JsonSerializer.Serialize(snapshot, snapshot.GetType(), SerializeOpts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public string ApplyAndActivate(string commandType, object normalizedInput, string? expectedBaseRevision, string? changeId)
    {
        var targetKey = GetTargetKey(commandType, normalizedInput);

        if (!string.IsNullOrEmpty(expectedBaseRevision))
        {
            var currentSnapshot = GetCurrentSnapshot(targetKey);
            var currentRevision = GetRevision(currentSnapshot);
            if (!string.Equals(currentRevision, expectedBaseRevision, StringComparison.Ordinal))
            {
                throw new ChangeConflictException(
                    $"目标 {targetKey} 基线已变化：期望 {expectedBaseRevision}，当前 {currentRevision}");
            }
        }

        return commandType switch
        {
            "item.edit" => ApplyEdit((BpItemOverride) normalizedInput),
            "item.override.delete" => ApplyDelete((ItemOverrideDeleteInput) normalizedInput),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
            throw new ChangeConflictException($"回溯目标 {targetKey} 已发生后续改动：期望 {expectedCurrentRevision}，当前 {currentRevision}");

        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3 || parts[1] != "override") throw new InvalidOperationException($"不支持回溯目标: {targetKey}");
        return beforeSnapshot is null
            ? ApplyDelete(new ItemOverrideDeleteInput { Id = parts[2] })
            : ApplyEdit(BattlePassSnapshotCodec.Deserialize<BpItemOverride>(beforeSnapshot));
    }

    // ---- Normalize helpers ----

    private static BpItemOverride NormalizeEdit(JsonElement input)
    {
        var ov = JsonSerializer.Deserialize<BpItemOverride>(input.GetRawText(), SerializeOpts)
            ?? throw new ArgumentException("无法解析获取途径数据");

        ov.Op = (ov.Op ?? "").Trim();
        ov.Source = (ov.Source ?? "").Trim();
        ov.Tpl = (ov.Tpl ?? "").Trim();
        ov.TraderId = ov.TraderId?.Trim();
        ov.QuestId = ov.QuestId?.Trim();
        ov.RecipeId = ov.RecipeId?.Trim();
        ov.LocationId = ov.LocationId?.Trim();
        ov.ContainerOrSpawn = ov.ContainerOrSpawn?.Trim();

        // 与后端 /edit 端点一致的稳定 id 生成规则（保证 targetKey 稳定、去重一致）。
        if (string.IsNullOrWhiteSpace(ov.Id))
        {
            ov.Id = $"{ov.Source}:{ov.Op}:{ov.Tpl}:{ov.TraderId}:{ov.QuestId}:{ov.RewardGroup}:{ov.RecipeId}:{ov.Role}:{ov.LocationId}:{ov.ContainerOrSpawn}:{ov.Side}";
        }

        return ov;
    }

    private static ItemOverrideDeleteInput NormalizeDelete(JsonElement input)
    {
        var id = input.TryGetProperty("id", out var prop) ? prop.GetString()?.Trim() : null;
        return new ItemOverrideDeleteInput { Id = id ?? "" };
    }

    // ---- Validate helpers ----

    private static string? ValidateEdit(BpItemOverride ov)
    {
        if (string.IsNullOrWhiteSpace(ov.Tpl) || string.IsNullOrWhiteSpace(ov.Source) || string.IsNullOrWhiteSpace(ov.Op))
        {
            return "缺少 tpl / source / op";
        }

        if (ov.Op != "add" && ov.Op != "remove")
        {
            return "op 只能是 add 或 remove";
        }

        if (ov.Source == AcqSource.StartInv && ov.Op == "add")
        {
            return "初始库存暂不支持新增（涉及背包网格摆放）";
        }

        return null;
    }

    private static string? ValidateDelete(ItemOverrideDeleteInput input)
    {
        return string.IsNullOrWhiteSpace(input.Id) ? "缺少 id" : null;
    }

    // ---- Apply helpers ----

    private string ApplyEdit(BpItemOverride ov)
    {
        var list = BattlePassStore.GetItemOverrides();
        list.RemoveAll(o => o.Id == ov.Id);
        list.Add(ov);
        BattlePassStore.SaveItemOverrides(list);
        controlSync.Sync(); // 即时重放对账

        var saved = BattlePassStore.GetItemOverrides()
            .FirstOrDefault(o => string.Equals(o.Id, ov.Id, StringComparison.Ordinal));
        return GetRevision(saved);
    }

    private string ApplyDelete(ItemOverrideDeleteInput input)
    {
        var list = BattlePassStore.GetItemOverrides();
        list.RemoveAll(o => o.Id == input.Id);
        BattlePassStore.SaveItemOverrides(list);
        controlSync.Sync();
        return GetRevision(null); // 删除后目标不存在，revision 为空
    }

    // ---- Shared helpers ----

    private static string SourceLabel(string source) => source switch
    {
        AcqSource.Trader => "商人",
        AcqSource.Quest => "任务奖励",
        AcqSource.Hideout => "藏身处",
        AcqSource.StartInv => "初始库存",
        AcqSource.Loot => "战利品",
        _ => source,
    };
}

/// <summary>获取途径 override 撤销命令的规范化输入。</summary>
public record ItemOverrideDeleteInput
{
    public string Id { get; set; } = "";
}
