using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass.Patches;

/// <summary>EndLocalRaid Postfix：战局正常收尾后，从战后档案权威结算通行证任务。</summary>
public class EndLocalRaidTrackPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        return typeof(LocationLifecycleService).GetMethod(
            nameof(LocationLifecycleService.EndLocalRaid),
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(MongoId), typeof(EndLocalRaidRequestData)]
        );
    }

    [PatchPostfix]
    private static void PatchPostfix(MongoId sessionId, EndLocalRaidRequestData request)
    {
        try
        {
            var service = ServiceLocator.ServiceProvider.GetService(typeof(BattlePassRaidEndService)) as BattlePassRaidEndService;
            service?.ApplyRaidEndTrack(sessionId, request);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SPT-BattlePass] EndLocalRaid tracking failed: {ex}");
        }
    }
}
