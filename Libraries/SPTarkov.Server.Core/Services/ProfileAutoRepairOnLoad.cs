using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace SPTarkov.Server.Core.Services;

/// <summary>
///     启动期对全部已加载存档跑一遍自修复并保存有变更者（原 SPT-Optimizations ProfileAutoRepairOnLoad 内置化）。
///     存盘前修复改为 SaveServer.SaveProfileAsync 内直调（不再用过时的 AddBeforeSaveCallback）。
/// </summary>
[Injectable(TypePriority = OnLoadOrder.SaveCallbacks + 3)]
public class ProfileAutoRepairOnLoad(
    SaveServer saveServer,
    ProfileAutoRepairService repairService,
    ISptLogger<ProfileAutoRepairOnLoad> logger
) : IOnLoad
{
    public async Task OnLoad()
    {
        if (!repairService.Enabled)
        {
            return;
        }

        // 懒加载开启时跳过启动批修复（避免全量物化打破懒加载）；存盘前/拉档前修复仍然生效
        if (saveServer.LazyEnabled)
        {
            logger.Info("[ProfileAutoRepair] 懒加载开启，跳过启动批修复（存盘前/拉档前修复不受影响）");
            return;
        }

        var repairedProfiles = 0;
        foreach (var (sessionId, profile) in saveServer.GetProfiles())
        {
            if (saveServer.IsProfileInvalidOrUnloadable(sessionId))
            {
                continue;
            }

            var summary = repairService.RepairProfile(profile, sessionId, "startup");
            if (!summary.Changed)
            {
                continue;
            }

            repairedProfiles++;
            await saveServer.SaveProfileAsync(sessionId);
        }

        if (repairedProfiles > 0)
        {
            logger.Success($"[ProfileAutoRepair] auto-repaired and saved {repairedProfiles} profile(s)");
        }
    }
}
