using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     判定玩家是否正处于「战局中」。战局内客户端持有权威库存/任务进度、结算时整档回写，
///     服务端此时改档（如任务跳过）会与客户端结算相互覆盖，故战局中必须拒绝跳过。
///     <para>联机（Fika）与单机战局判定来源不同，二者取「或」：</para>
///     <list type="bullet">
///         <item>Fika：<c>MatchService.GetMatchIdByProfile(id)</c> 是否非空——该玩家是否登记在某场对局里。
///             服务端权威、随战局生命周期即时增删；<b>不用</b> presence（房主结束战局后会长期滞留 IN_RAID → 误判）。</item>
///         <item>单机：<c>ProfileActivityService.IsRaidActive</c>（StartLocalRaid→true / EndLocalRaid→false，地图转移期间保持 true）。</item>
///     </list>
///     Fika 类不在编译期 NuGet 引用内，故反射调用；未装 Fika / 不在对局均安全降级为「不在战局」。
///     判定逻辑与 SPT-DynamicFleaPrice 的 FleaPlayerAuthService.IsInRaid 保持一致。
/// </summary>
[Injectable(InjectionType.Singleton)]
public class QuestSkipRaidStateProbe(
    ProfileActivityService profileActivityService,
    IServiceProvider serviceProvider,
    ISptLogger<QuestSkipRaidStateProbe> logger
)
{
    public bool IsInRaid(MongoId sessionId)
    {
        return FikaReportsInRaid(sessionId) || SptReportsRaidActive(sessionId);
    }

    private static MethodInfo? _isRaidActiveMethod;
    private static bool _isRaidActiveResolved;

    /// <summary>单机战局：反射调用 <c>ProfileActivityService.IsRaidActive(MongoId)</c>。</summary>
    private bool SptReportsRaidActive(MongoId sessionId)
    {
        try
        {
            if (!_isRaidActiveResolved)
            {
                _isRaidActiveMethod = profileActivityService.GetType().GetMethod("IsRaidActive", new[] { typeof(MongoId) });
                _isRaidActiveResolved = true;
            }

            return _isRaidActiveMethod?.Invoke(profileActivityService, new object[] { sessionId }) is true;
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] 读取 SPT 战局状态失败: {ex.Message}");
            return false;
        }
    }

    private static Type? _matchServiceType;
    private static MethodInfo? _getMatchIdByProfileMethod;
    private static bool _matchResolved;

    /// <summary>联机战局：反射查 Fika <c>MatchService.GetMatchIdByProfile(id)</c> 是否非空。未装 Fika / 不在对局返回 false。</summary>
    private bool FikaReportsInRaid(MongoId sessionId)
    {
        try
        {
            if (!_matchResolved)
            {
                _matchServiceType = FindLoadedType("FikaServer.Services.MatchService");
                _getMatchIdByProfileMethod = _matchServiceType?.GetMethod("GetMatchIdByProfile", new[] { typeof(MongoId) });
                _matchResolved = true;
            }

            if (_matchServiceType is null || _getMatchIdByProfileMethod is null)
            {
                return false; // 未安装 Fika
            }

            var matchService = serviceProvider.GetService(_matchServiceType);
            if (matchService is null)
            {
                return false;
            }

            // 返回 MongoId?(Nullable<MongoId>)：装箱后 null=不在任何对局；非 null=在对局中（含 LOADING/COMPLETE 窗口，均应拦截）。
            var matchId = _getMatchIdByProfileMethod.Invoke(matchService, new object[] { sessionId });
            return matchId is not null;
        }
        catch (Exception ex)
        {
            logger.Warning($"[SPT-BattlePass] 读取 Fika 对局状态失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>在已加载程序集中按全名查类型（Fika 作为服务端 mod 动态加载，程序集限定名不稳定，故遍历）。</summary>
    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type is not null)
                {
                    return type;
                }
            }
            catch
            {
                // 个别动态程序集 GetType 可能抛异常，跳过
            }
        }

        return null;
    }
}
