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
    protected static readonly string[] PreservedKeywords = ["LifeTime", "Sessions", "Session", "ExitStatus", "Online", "TotalInGameTime", "InGameTime"];

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
            PmcRegistrationDate = pmc?.Info?.RegistrationDate,
            ScavRegistrationDate = scav?.Info?.RegistrationDate,
            PmcOverallCounters = Config.PreserveBasicRaidCounters ? FilterBasicCounters(pmc?.Stats?.Eft?.OverallCounters) : [],
            ScavOverallCounters = Config.PreserveBasicRaidCounters ? FilterBasicCounters(scav?.Stats?.Eft?.OverallCounters) : [],
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

        var pmcCounters = BuildCountersToRestore(
            sidecar.PmcOverallCounters.Count > 0 ? sidecar.PmcOverallCounters : sidecar.OverallCounters,
            "Pmc",
            sidecar.PmcTotalInGameTime
        );
        var scavCounters = BuildCountersToRestore(sidecar.ScavOverallCounters, "Savage", sidecar.ScavTotalInGameTime);

        var pmcStatsReady = !Config.PreserveInGameTime;
        var scavStatsReady = !Config.PreserveInGameTime;
        var pmcCountersReady = pmcCounters.Count == 0;
        var scavCountersReady = scavCounters.Count == 0;
        var pmcRegistrationReady = sidecar.PmcRegistrationDate is null;
        var scavRegistrationReady = sidecar.ScavRegistrationDate is null;

        if (sidecar.PmcRegistrationDate is not null && pmc?.Info is not null)
        {
            pmc.Info.RegistrationDate = sidecar.PmcRegistrationDate;
            pmcRegistrationReady = true;
        }

        if (sidecar.ScavRegistrationDate is not null && scav?.Info is not null)
        {
            scav.Info.RegistrationDate = sidecar.ScavRegistrationDate;
            scavRegistrationReady = true;
        }

        if (Config.PreserveInGameTime)
        {
            if (pmc?.Stats?.Eft is not null)
            {
                pmc.Stats.Eft.TotalInGameTime = sidecar.PmcTotalInGameTime;
                pmcStatsReady = true;
            }

            if (scav?.Stats?.Eft is not null)
            {
                scav.Stats.Eft.TotalInGameTime = sidecar.ScavTotalInGameTime;
                scavStatsReady = true;
            }
        }

        if (pmcCounters.Count > 0 && pmc?.Stats?.Eft?.OverallCounters is { } pmcOverallCounters)
        {
            MergeCounters(pmcOverallCounters, pmcCounters);
            pmcCountersReady = true;
        }

        if (scavCounters.Count > 0 && scav?.Stats?.Eft?.OverallCounters is { } scavOverallCounters)
        {
            MergeCounters(scavOverallCounters, scavCounters);
            scavCountersReady = true;
        }

        // AddProfile runs before the player-scav is generated, so the first merge can only restore PMC stats.
        // Keep the sidecar until both PMC and scav targets are present, then the create-profile flow calls this again.
        if (pmcStatsReady && scavStatsReady && pmcCountersReady && scavCountersReady && pmcRegistrationReady && scavRegistrationReady)
        {
            fileUtil.DeleteFile(path);
            logger.Info($"[SoftReset] merged preserved stats back into rebuilt profile {sessionId}");
        }
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

    protected List<CounterKeyValue> BuildCountersToRestore(List<CounterKeyValue> counters, string sideKey, long? totalInGameTime)
    {
        var result = Config.PreserveBasicRaidCounters ? counters.ToList() : [];

        if (Config.PreserveInGameTime && totalInGameTime is > 0)
        {
            UpsertLifetimeCounter(result, sideKey, totalInGameTime.Value);
        }

        return result;
    }

    protected static void UpsertLifetimeCounter(List<CounterKeyValue> counters, string sideKey, long totalInGameTime)
    {
        var existing = counters.FirstOrDefault(kv => IsLifetimeCounter(kv, sideKey));
        if (existing is not null)
        {
            existing.Value = Math.Max(existing.Value ?? 0, totalInGameTime);
            return;
        }

        counters.Add(new CounterKeyValue { Key = ["LifeTime", sideKey], Value = totalInGameTime });
    }

    protected static bool IsLifetimeCounter(CounterKeyValue counter, string sideKey)
    {
        return counter.Key?.Contains("LifeTime") == true && counter.Key.Contains(sideKey);
    }

    protected static void MergeCounters(OverallCounters counters, List<CounterKeyValue> preserved)
    {
        var existing = counters.Items ?? [];
        counters.Items = existing
            .Where(e => !preserved.Any(p => p.Key is not null && e.Key is not null && p.Key.SetEquals(e.Key)))
            .Concat(preserved)
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

    [JsonPropertyName("pmcRegistrationDate")]
    public int? PmcRegistrationDate { get; init; }

    [JsonPropertyName("scavRegistrationDate")]
    public int? ScavRegistrationDate { get; init; }

    [JsonPropertyName("overallCounters")]
    public List<CounterKeyValue> OverallCounters { get; init; } = [];

    [JsonPropertyName("pmcOverallCounters")]
    public List<CounterKeyValue> PmcOverallCounters { get; init; } = [];

    [JsonPropertyName("scavOverallCounters")]
    public List<CounterKeyValue> ScavOverallCounters { get; init; } = [];
}
