using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils.Cloners;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     启动期对全部已加载存档跑一遍自修复并保存有变更者（原 SPT-Optimizations ProfileAutoRepairOnLoad 内置化）。
///     存盘前修复改为 SaveServer.SaveProfileAsync 内直调（不再用过时的 AddBeforeSaveCallback）。
/// </summary>
[Injectable(TypePriority = OnLoadOrder.SaveCallbacks + 3)]
public class ProfileAutoRepairOnLoad(
    SaveServer saveServer,
    ProfileHelper profileHelper,
    ProfileAutoRepairService repairService,
    BackupService backupService,
    ICloner cloner,
    ISptLogger<ProfileAutoRepairOnLoad> logger
) : IOnLoad
{
    public async Task OnLoad()
    {
        if (!repairService.Enabled)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var scannedProfiles = 0;
        var modifiedProfiles = 0;
        var repairedProfiles = 0;
        var skippedWrites = 0;
        BackupSnapshotResult? repairSnapshot = null;
        var profiles = profileHelper.GetActiveProfilesSnapshot();
        var deferredProfiles = saveServer.LazyEnabled ? Math.Max(0, saveServer.GetLazyHeaders().Count - profiles.Count) : 0;

        logger.Info(
            $"[ProfileAutoRepair] startup repair enabled; scanning {profiles.Count} loaded profile(s), "
                + $"deferred={deferredProfiles}"
        );
        foreach (var (sessionId, profile) in profiles)
        {
            scannedProfiles++;
            if (saveServer.IsProfileInvalidOrUnloadable(sessionId))
            {
                continue;
            }

            var probe = cloner.Clone(profile);
            var probeSummary = repairService.RepairProfile(probe, sessionId, "startup", false);
            if (!probeSummary.Changed)
            {
                continue;
            }

            modifiedProfiles++;
            repairSnapshot ??= await backupService.CreateRepairSnapshotAsync();
            if (!repairSnapshot.Succeeded)
            {
                skippedWrites++;
                logger.Error(
                    $"[ProfileAutoRepair] repair snapshot failed; skipping startup write for {sessionId}: {repairSnapshot.FailureReason}"
                );
                continue;
            }

            repairService.RepairProfile(profile, sessionId, "startup");
            await saveServer.SaveProfileAsync(sessionId);
            repairedProfiles++;

            await Task.Yield();
        }

        stopwatch.Stop();
        logger.Success(
            "[ProfileAutoRepair] startup repair complete; "
                + $"scanned={scannedProfiles}, modified={modifiedProfiles}, saved={repairedProfiles}, "
                + $"deferred={deferredProfiles}, skippedWrites={skippedWrites}, "
                + $"snapshot={(repairSnapshot?.Succeeded == true ? repairSnapshot.SnapshotPath : "none")}, "
                + $"elapsedMs={stopwatch.ElapsedMilliseconds}"
        );
    }
}
