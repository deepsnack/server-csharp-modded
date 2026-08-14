using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace SPTarkov.Server.Core.Controllers;

[Injectable]
public class AchievementController(ProfileHelper profileHelper, DatabaseService databaseService, ConfigServer configServer)
{
    protected readonly CoreConfig CoreConfig = configServer.GetConfig<CoreConfig>();

    /// <summary>
    ///     Get base achievements
    /// </summary>
    /// <param name="sessionID">Session/player id</param>
    /// <returns></returns>
    public virtual GetAchievementsResponse GetAchievements(MongoId sessionID)
    {
        return new GetAchievementsResponse { Elements = databaseService.GetAchievements() };
    }

    /// <summary>
    ///     Shows % of 'other' players who've completed each achievement
    /// </summary>
    /// <param name="sessionId">Session/Player id</param>
    /// <returns>CompletedAchievementsResponse</returns>
    public virtual CompletedAchievementsResponse GetAchievementStatics(MongoId sessionId)
    {
        var blacklist = CoreConfig.Features.AchievementProfileIdBlacklist;
        var profiles = profileHelper
            .GetAchievementIdsByProfile()
            .Where(entry => blacklist?.Contains(entry.Key.ToString()) != true)
            .Select(entry => entry.Value);
        var achievementIds = databaseService
            .GetAchievements()
            .Select(achievement => achievement.Id)
            .Where(achievementId => !achievementId.IsEmpty);
        var stats = CalculateAchievementPercentages(achievementIds, profiles);

        return new CompletedAchievementsResponse { Elements = stats };
    }

    internal static Dictionary<string, int> CalculateAchievementPercentages(
        IEnumerable<MongoId> achievementIds,
        IEnumerable<IReadOnlySet<MongoId>> profiles
    )
    {
        var completedCounts = new Dictionary<MongoId, int>();
        var profileCount = 0;
        foreach (var profileAchievements in profiles)
        {
            profileCount++;
            foreach (var achievementId in profileAchievements)
            {
                completedCounts[achievementId] = completedCounts.GetValueOrDefault(achievementId) + 1;
            }
        }

        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var achievementId in achievementIds)
        {
            var completed = completedCounts.GetValueOrDefault(achievementId);
            stats[achievementId.ToString()] = profileCount == 0 ? 0 : (int) Math.Round((double) completed / profileCount * 100);
        }

        return stats;
    }
}
