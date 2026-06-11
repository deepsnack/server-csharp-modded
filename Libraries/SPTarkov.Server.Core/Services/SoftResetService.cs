using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     软重置（原 SPT-ProfileCore SoftReset 内置化）：启动器"删除存档"改为保留账号身份（Info）、
///     擦除全部进度并标记 IsWiped；可保留的统计（在场时间/基础战局计数）先写 sidecar，
///     角色重建（SaveServer.AddProfile）时合并回去。
///     与 mod 版差异：擦除后的存档保留在内存（mod 版依赖 LazyProfile 重注册 header，树内全量加载无需驱逐）；
///     落盘走 SaveProfileAsync 的用户名命名路径（修正 mod 版直写 MongoId 文件名导致的重复文件）。
///     开关：SPT_Data/softreset/config.json（enabled，默认关，与 mod 版路径一致）。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class SoftResetService(FileUtil fileUtil, JsonUtil jsonUtil, ISptLogger<SoftResetService> logger)
{
    protected static readonly string[] PreservedKeywords = ["LifeTime", "Sessions", "Session", "ExitStatus"];

    protected static readonly string ConfigDir = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "softreset");

    protected SoftResetConfig? config;

    protected SoftResetConfig Config => config ??= LoadConfig();

    public bool Enabled => Config.Enabled;

    /// <summary>擦除前把可保留统计写入 sidecar。</summary>
    public void CaptureSidecar(MongoId sessionId, SptProfile profile)
    {
        var pmc = profile.CharacterData?.PmcData;
        var scav = profile.CharacterData?.ScavData;

        var sidecar = new SoftResetSidecar
        {
            PmcTotalInGameTime = Config.PreserveInGameTime ? pmc?.Stats?.Eft?.TotalInGameTime ?? 0 : 0,
            ScavTotalInGameTime = Config.PreserveInGameTime ? scav?.Stats?.Eft?.TotalInGameTime ?? 0 : 0,
            OverallCounters = Config.PreserveBasicRaidCounters ? FilterBasicCounters(pmc?.Stats?.Eft?.OverallCounters) : [],
        };

        try
        {
            fileUtil.WriteFile(SidecarPath(sessionId), jsonUtil.Serialize(sidecar, true) ?? "{}");
        }
        catch (Exception ex)
        {
            logger.Warning($"[SoftReset] failed writing sidecar for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>擦除全部进度，仅保留 Info（账号身份/密码/版本）。</summary>
    public void WipeProfileContent(SptProfile profile)
    {
        profile.CharacterData = new Characters();
        profile.UserBuildData = null;
        profile.DialogueRecords = null;
        profile.InsuranceList = null;
        profile.BtrDeliveryList = null;
        profile.TraderPurchases = null;
        profile.FriendProfileIds = null;
        profile.CustomisationUnlocks = null;
        profile.DialogueProgress = null;
        profile.InraidData = null;
        profile.SptData = null;
#pragma warning disable CS0618
        profile.Suits = null;
#pragma warning restore CS0618
    }

    /// <summary>
    ///     角色重建后若存在待合并 sidecar，把保留统计写回新角色并删除 sidecar。
    /// </summary>
    public void MergePreservedStatsIfPending(SptProfile profile)
    {
        var sessionId = profile.ProfileInfo?.ProfileId;
        if (sessionId is null || sessionId.Value.IsEmpty)
        {
            return;
        }

        var path = SidecarPath(sessionId.Value);
        if (!fileUtil.FileExists(path))
        {
            return;
        }

        SoftResetSidecar? sidecar;
        try
        {
            sidecar = jsonUtil.Deserialize<SoftResetSidecar>(fileUtil.ReadFile(path));
        }
        catch
        {
            sidecar = null;
        }

        if (sidecar is null)
        {
            return;
        }

        var pmc = profile.CharacterData?.PmcData;
        var scav = profile.CharacterData?.ScavData;

        if (Config.PreserveInGameTime)
        {
            if (pmc?.Stats?.Eft is not null)
            {
                pmc.Stats.Eft.TotalInGameTime = sidecar.PmcTotalInGameTime;
            }

            if (scav?.Stats?.Eft is not null)
            {
                scav.Stats.Eft.TotalInGameTime = sidecar.ScavTotalInGameTime;
            }
        }

        if (Config.PreserveBasicRaidCounters && sidecar.OverallCounters.Count > 0 && pmc?.Stats?.Eft?.OverallCounters is { } oc)
        {
            var existing = oc.Items ?? [];
            // 同键集合的旧条目替换为保留条目，其余追加
            var preserved = sidecar.OverallCounters;
            oc.Items = existing
                .Where(e => !preserved.Any(p => p.Key is not null && e.Key is not null && p.Key.SetEquals(e.Key)))
                .Concat(preserved)
                .ToList();
        }

        fileUtil.DeleteFile(path);
        logger.Info($"[SoftReset] merged preserved stats back into rebuilt profile {sessionId}");
    }

    protected static List<CounterKeyValue> FilterBasicCounters(OverallCounters? counters)
    {
        if (counters?.Items is null)
        {
            return [];
        }

        return counters
            .Items.Where(kv => kv.Key?.Any(k => PreservedKeywords.Any(kw => k.Contains(kw, StringComparison.OrdinalIgnoreCase))) == true)
            .ToList();
    }

    protected static string SidecarPath(MongoId sessionId)
    {
        return System.IO.Path.Combine(ConfigDir, $"{sessionId}.json");
    }

    protected SoftResetConfig LoadConfig()
    {
        var path = System.IO.Path.Combine(ConfigDir, "config.json");
        try
        {
            if (!fileUtil.FileExists(path))
            {
                var template = new SoftResetConfig();
                fileUtil.WriteFile(path, jsonUtil.Serialize(template, true) ?? "{}");
                return template;
            }

            return jsonUtil.Deserialize<SoftResetConfig>(fileUtil.ReadFile(path)) ?? new SoftResetConfig();
        }
        catch
        {
            return new SoftResetConfig();
        }
    }
}

public record SoftResetConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("preserveInGameTime")]
    public bool PreserveInGameTime { get; set; } = true;

    [JsonPropertyName("preserveBasicRaidCounters")]
    public bool PreserveBasicRaidCounters { get; set; } = true;
}

public record SoftResetSidecar
{
    [JsonPropertyName("requestedAt")]
    public string RequestedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("pmcTotalInGameTime")]
    public long PmcTotalInGameTime { get; init; }

    [JsonPropertyName("scavTotalInGameTime")]
    public long ScavTotalInGameTime { get; init; }

    [JsonPropertyName("overallCounters")]
    public List<CounterKeyValue> OverallCounters { get; init; } = [];
}
