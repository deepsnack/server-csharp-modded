using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass.ItemControl;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>
///     跳蚤黑名单管控模块命令处理器：整表配置保存（flea-control.json）+ 单物品黑/白名单增删。
///     <para>应用后调用 <see cref="FleaControlSync.Apply"/> 即时写回 RagfairConfig 生效。
///     整表保存用命令 flea.config.save（单例目标）；逐项拉黑/放开用 flea.blacklist.toggle /
///     flea.whitelist.toggle。</para>
/// </summary>
[Injectable(InjectionType.Singleton)]
public class FleaChangeHandler(
    FleaControlSync fleaSync,
    ISptLogger<FleaChangeHandler> logger
) : IBattlePassChangeHandler
{
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string ConfigTargetKey = "flea:config:global";

    public string Module => "flea";
    public IReadOnlyList<string> CommandTypes { get; } =
        ["flea.config.save", "flea.blacklist.toggle", "flea.whitelist.toggle"];
    public string RequiredCapability => "flea.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        return commandType switch
        {
            "flea.config.save" => NormalizeConfig(input),
            "flea.blacklist.toggle" => NormalizeToggle(input),
            "flea.whitelist.toggle" => NormalizeToggle(input),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "flea.config.save" => null, // 整表任意组合均合法（tpl 由前端搜索选取，非法项运行时忽略）
            "flea.blacklist.toggle" => ValidateToggle((FleaToggleInput) normalizedInput),
            "flea.whitelist.toggle" => ValidateToggle((FleaToggleInput) normalizedInput),
            _ => $"未知命令类型: {commandType}",
        };
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        switch (commandType)
        {
            case "flea.config.save":
                var cfg = (BpFleaControl) normalizedInput;
                return $"保存跳蚤黑名单配置（拉黑 {cfg.BlacklistTpls.Count} 项 / 放开 {cfg.WhitelistTpls.Count} 项 / 分类 {cfg.BlacklistCategories.Count} 项）";
            case "flea.blacklist.toggle":
                var bl = (FleaToggleInput) normalizedInput;
                return $"{(bl.Add ? "加入" : "移出")}跳蚤黑名单 tpl={bl.Tpl}";
            case "flea.whitelist.toggle":
                var wl = (FleaToggleInput) normalizedInput;
                return $"{(wl.Add ? "加入" : "移出")}跳蚤白名单(强制放开) tpl={wl.Tpl}";
            default:
                return commandType;
        }
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "flea.config.save" => ConfigTargetKey,
            "flea.blacklist.toggle" => $"flea:blacklist:{((FleaToggleInput) normalizedInput).Tpl}",
            "flea.whitelist.toggle" => $"flea:whitelist:{((FleaToggleInput) normalizedInput).Tpl}",
            _ => $"flea:unknown:{commandType}",
        };
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return commandType switch
        {
            "flea.config.save" => "跳蚤黑名单配置",
            "flea.blacklist.toggle" => ((FleaToggleInput) normalizedInput).Tpl,
            "flea.whitelist.toggle" => ((FleaToggleInput) normalizedInput).Tpl,
            _ => commandType,
        };
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        var cfg = BattlePassStore.GetFleaControl();

        if (targetKey == ConfigTargetKey)
        {
            return cfg;
        }

        // targetKey format: flea:blacklist:<tpl> / flea:whitelist:<tpl>
        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3) return null;

        var tpl = parts[2];
        return parts[1] switch
        {
            "blacklist" => new FleaMembership { Tpl = tpl, Present = cfg.BlacklistTpls.Contains(tpl) },
            "whitelist" => new FleaMembership { Tpl = tpl, Present = cfg.WhitelistTpls.Contains(tpl) },
            _ => null,
        };
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
            "flea.config.save" => ApplyConfig((BpFleaControl) normalizedInput),
            "flea.blacklist.toggle" => ApplyBlacklistToggle((FleaToggleInput) normalizedInput),
            "flea.whitelist.toggle" => ApplyWhitelistToggle((FleaToggleInput) normalizedInput),
            _ => throw new ArgumentException($"未知命令类型: {commandType}"),
        };
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
            throw new ChangeConflictException($"回溯目标 {targetKey} 已发生后续改动：期望 {expectedCurrentRevision}，当前 {currentRevision}");

        if (targetKey == ConfigTargetKey)
        {
            return ApplyConfig(BattlePassSnapshotCodec.Deserialize<BpFleaControl>(beforeSnapshot));
        }

        var parts = targetKey.Split(':', 3);
        if (parts.Length < 3) throw new InvalidOperationException($"无效目标键: {targetKey}");
        var membership = BattlePassSnapshotCodec.Deserialize<FleaMembership>(beforeSnapshot);
        var input = new FleaToggleInput { Tpl = parts[2], Add = membership.Present };
        return parts[1] switch
        {
            "blacklist" => ApplyBlacklistToggle(input),
            "whitelist" => ApplyWhitelistToggle(input),
            _ => throw new InvalidOperationException($"不支持回溯目标: {targetKey}"),
        };
    }

    // ---- Normalize helpers ----

    private static BpFleaControl NormalizeConfig(JsonElement input)
    {
        return JsonSerializer.Deserialize<BpFleaControl>(input.GetRawText(), SerializeOpts)
            ?? throw new ArgumentException("无法解析跳蚤配置数据");
    }

    private static FleaToggleInput NormalizeToggle(JsonElement input)
    {
        var tpl = input.TryGetProperty("tpl", out var t) ? t.GetString()?.Trim() : null;
        // 与后端 toggle 端点一致：未显式 false 即视为 add=true
        var add = !input.TryGetProperty("add", out var a) || a.ValueKind != JsonValueKind.False;
        return new FleaToggleInput { Tpl = tpl ?? "", Add = add };
    }

    // ---- Validate helpers ----

    private static string? ValidateToggle(FleaToggleInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Tpl))
        {
            return "缺少 tpl";
        }

        if (!MongoId.IsValidMongoId(input.Tpl))
        {
            return "tpl 无效";
        }

        return null;
    }

    // ---- Apply helpers ----

    private string ApplyConfig(BpFleaControl config)
    {
        BattlePassStore.SaveFleaControl(config);
        fleaSync.Apply();
        return GetRevision(BattlePassStore.GetFleaControl());
    }

    private string ApplyBlacklistToggle(FleaToggleInput input)
    {
        var cfg = BattlePassStore.GetFleaControl();
        var changed = input.Add ? cfg.BlacklistTpls.Add(input.Tpl) : cfg.BlacklistTpls.Remove(input.Tpl);
        if (changed)
        {
            BattlePassStore.SaveFleaControl(cfg);
            fleaSync.Apply();
        }

        return GetRevision(new FleaMembership { Tpl = input.Tpl, Present = cfg.BlacklistTpls.Contains(input.Tpl) });
    }

    private string ApplyWhitelistToggle(FleaToggleInput input)
    {
        var cfg = BattlePassStore.GetFleaControl();
        var changed = input.Add ? cfg.WhitelistTpls.Add(input.Tpl) : cfg.WhitelistTpls.Remove(input.Tpl);
        if (changed)
        {
            BattlePassStore.SaveFleaControl(cfg);
            fleaSync.Apply();
        }

        return GetRevision(new FleaMembership { Tpl = input.Tpl, Present = cfg.WhitelistTpls.Contains(input.Tpl) });
    }
}

/// <summary>单物品黑/白名单增删命令的规范化输入。</summary>
public record FleaToggleInput
{
    public string Tpl { get; set; } = "";
    public bool Add { get; set; } = true;
}

/// <summary>单物品在某名单中的存在性快照（用于 toggle 的 revision/冲突检测）。</summary>
public record FleaMembership
{
    public string Tpl { get; set; } = "";
    public bool Present { get; set; }
}
