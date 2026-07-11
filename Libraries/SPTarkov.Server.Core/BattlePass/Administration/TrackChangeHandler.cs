using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace SPTarkov.Server.Core.BattlePass.Administration;

/// <summary>奖励轨模块命令处理器：tracks.save（整体保存）。</summary>
[Injectable]
public class TrackChangeHandler(ISptLogger<TrackChangeHandler> logger) : IBattlePassChangeHandler
{
    public string Module => "tracks";
    public IReadOnlyList<string> CommandTypes { get; } = ["tracks.save"];
    public string RequiredCapability => "tracks.submit";

    public object Normalize(string commandType, JsonElement input)
    {
        var tracks = JsonSerializer.Deserialize<Dictionary<int, BpLevelRewards>>(input.GetRawText())
            ?? throw new ArgumentException("无法反序列化奖励轨数据");
        return tracks;
    }

    public string? Validate(string commandType, object normalizedInput)
    {
        return null;
    }

    public string Describe(string commandType, object normalizedInput, object? currentState)
    {
        var tracks = (Dictionary<int, BpLevelRewards>) normalizedInput;
        return $"改动奖励轨（{tracks.Count} 个等级）";
    }

    public string GetTargetKey(string commandType, object normalizedInput)
    {
        return "tracks:tracks:_global";
    }

    public string GetTargetDisplayName(string commandType, object normalizedInput)
    {
        return "双轨奖励配置";
    }

    public object? GetCurrentSnapshot(string targetKey)
    {
        return BattlePassStore.GetTracks();
    }

    public string GetRevision(object? snapshot)
    {
        if (snapshot is null) return "";
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash);
    }

    public string ApplyAndActivate(string commandType, object normalizedInput, string? expectedBaseRevision, string? changeId)
    {
        var partial = (Dictionary<int, BpLevelRewards>) normalizedInput;

        if (expectedBaseRevision is not null)
        {
            var current = GetCurrentSnapshot("tracks:tracks:_global");
            var currentRev = GetRevision(current);
            if (currentRev != expectedBaseRevision)
                throw new ChangeConflictException($"奖励轨基线版本冲突: expected={expectedBaseRevision}, actual={currentRev}");
        }

        // 差量合并：仅覆盖本次提交的改动等级，其余等级保持库中现值
        var merged = BattlePassStore.GetTracks();
        foreach (var kv in partial)
            merged[kv.Key] = kv.Value;
        BattlePassStore.SaveTracks(merged);

        var newSnapshot = GetCurrentSnapshot("tracks:tracks:_global");
        return GetRevision(newSnapshot);
    }

    public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
    {
        var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
        if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
            throw new ChangeConflictException($"奖励轨已有后续改动: expected={expectedCurrentRevision}, actual={currentRevision}");

        var tracks = BattlePassSnapshotCodec.Deserialize<Dictionary<int, BpLevelRewards>>(beforeSnapshot);
        BattlePassStore.SaveTracks(tracks);
        return GetRevision(GetCurrentSnapshot("tracks:tracks:_global"));
    }
}
