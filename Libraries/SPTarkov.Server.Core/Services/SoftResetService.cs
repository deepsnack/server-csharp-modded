using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     软重置（原 SPT-ProfileCore SoftReset 内置化）：启动器"删除存档"改为保留账号身份（Info）、
///     擦除全部进度并标记 IsWiped。需要保留的账号存在时间（注册日期）与在线时间（TotalInGameTime）
///     擦除前暂存到 ProfileInfo——它不随 CharacterData 擦除、且会随存档一起被 CreateProfile 重建流程读到，
///     角色重建后写回并清空（取代旧版 sidecar 文件 + ready-flag 编排，去掉跨文件读写与时序脆弱性）。
///     开关：SPT_Data/softreset/config.json（enabled，默认关）。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class SoftResetService(FileUtil fileUtil, JsonUtil jsonUtil, ISptLogger<SoftResetService> logger)
{
    protected static readonly string ConfigDir = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "SPT_Data", "softreset");

    protected SoftResetConfig? config;

    protected SoftResetConfig Config => config ??= LoadConfig();

    public bool Enabled => Config.Enabled;

    /// <summary>擦除前把账号存在时间(注册日期)与在线时间暂存到 ProfileInfo。</summary>
    public void CapturePreservedStats(SptProfile profile)
    {
        var info = profile.ProfileInfo;
        if (info is null)
        {
            return;
        }

        var pmc = profile.CharacterData?.PmcData;
        var scav = profile.CharacterData?.ScavData;

        info.PreservedRegistrationDate = pmc?.Info?.RegistrationDate;
        info.PreservedPmcInGameTime = pmc?.Stats?.Eft?.TotalInGameTime;
        info.PreservedScavInGameTime = scav?.Stats?.Eft?.TotalInGameTime;
    }

    /// <summary>擦除全部进度，仅保留 Info（账号身份/密码/版本 + 暂存的保留统计）。</summary>
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
    ///     角色重建后把暂存的注册日期/在线时间写回。可被多次调用——scav 较晚生成，
    ///     PMC 在 AddProfile 时即就绪，scav 在生成后再调用一次补齐。
    /// </summary>
    public void ApplyPreservedStats(SptProfile profile)
    {
        var info = profile.ProfileInfo;
        if (info is null)
        {
            return;
        }

        var pmc = profile.CharacterData?.PmcData;
        var scav = profile.CharacterData?.ScavData;

        if (info.PreservedRegistrationDate is { } regDate && pmc?.Info is not null)
        {
            pmc.Info.RegistrationDate = regDate;
        }

        if (info.PreservedPmcInGameTime is { } pmcTime && pmc?.Stats?.Eft is not null)
        {
            pmc.Stats.Eft.TotalInGameTime = pmcTime;
            UpsertLifetimeCounter(pmc.Stats.Eft.OverallCounters, "Pmc", pmcTime);
        }

        if (info.PreservedScavInGameTime is { } scavTime && scav?.Stats?.Eft is not null)
        {
            scav.Stats.Eft.TotalInGameTime = scavTime;
            UpsertLifetimeCounter(scav.Stats.Eft.OverallCounters, "Savage", scavTime);
        }
    }

    /// <summary>重建完成后清空暂存值，避免下次普通建号误用旧数据。</summary>
    public void ClearPreservedStats(SptProfile profile)
    {
        var info = profile.ProfileInfo;
        if (info is null)
        {
            return;
        }

        if (info.PreservedRegistrationDate is null && info.PreservedPmcInGameTime is null && info.PreservedScavInGameTime is null)
        {
            return;
        }

        info.PreservedRegistrationDate = null;
        info.PreservedPmcInGameTime = null;
        info.PreservedScavInGameTime = null;
        logger.Info($"[SoftReset] preserved account-age/in-game-time restored into rebuilt profile {info.ProfileId}");
    }

    protected static void UpsertLifetimeCounter(OverallCounters? counters, string sideKey, long totalInGameTime)
    {
        if (counters is null || totalInGameTime <= 0)
        {
            return;
        }

        counters.Items ??= [];
        var existing = counters.Items.FirstOrDefault(kv => kv.Key?.Contains("LifeTime") == true && kv.Key.Contains(sideKey));
        if (existing is not null)
        {
            existing.Value = Math.Max(existing.Value ?? 0, totalInGameTime);
            return;
        }

        counters.Items.Add(new CounterKeyValue { Key = ["LifeTime", sideKey], Value = totalInGameTime });
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
}
