using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;

namespace SPTarkov.Server.Core.BattlePass;

/// <summary>
///     服务端权威战后追踪：从 EndLocalRaid 上报的完整战后档案生成通行证任务快照。
///     不写 pmcData.Quests / TaskConditionCounters，只更新 BattlePassStore 的独立进度 JSON。
/// </summary>
[Injectable]
public class BattlePassRaidEndService(
    BattlePassService battlePassService,
    BattlePassTrackService trackService
)
{
    public RaidTrackResult ApplyRaidEndTrack(MongoId sessionId, EndLocalRaidRequestData? request)
    {
        var result = new RaidTrackResult();
        if (request?.Results?.Profile?.Stats?.Eft is null)
        {
            return result;
        }

        var profileId = sessionId.ToString();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return result;
        }

        var payload = BuildPayload(sessionId, request);
        if (payload is null)
        {
            return result;
        }

        var season = BattlePassStore.GetSeason();
        var prog = battlePassService.GetOrResetProgress(profileId, season);
        trackService.RefreshActiveTasks(profileId, prog);

        result = trackService.ApplyAuthoritativeRaidTrack(profileId, prog, season, payload);
        BattlePassStore.SaveProgress(profileId, prog);
        return result;
    }

    private RaidTrackPayload? BuildPayload(MongoId sessionId, EndLocalRaidRequestData request)
    {
        var profile = request.Results?.Profile;
        var eft = profile?.Stats?.Eft;
        if (eft is null)
        {
            return null;
        }

        var location = ResolveLocation(request);
        var exitStatus = request.Results?.Result?.ToString();
        var payload = new RaidTrackPayload
        {
            RaidId = ResolveRaidId(sessionId, request),
            Location = location,
            ExitStatus = exitStatus,
            Kills = (eft.Victims ?? [])
                .Select(v => new BpKillEvent
                {
                    Side = v.Side,
                    Role = v.Role,
                    Weapon = v.Weapon,
                    BodyPart = v.BodyPart,
                    Distance = v.Distance,
                    Time = v.Time,
                })
                .ToList(),
            FoundItems = BuildFoundItems(profile, eft, exitStatus),
        };

        return payload;
    }

    private static string ResolveRaidId(MongoId sessionId, EndLocalRaidRequestData request)
    {
        if (!string.IsNullOrWhiteSpace(request.ServerId))
        {
            return request.ServerId;
        }

        var location = ResolveLocation(request) ?? "unknown";
        var lastSessionDate = request.Results?.Profile?.Stats?.Eft?.LastSessionDate?.ToString() ?? "0";
        return $"{sessionId}:{location}:{lastSessionDate}";
    }

    private static string? ResolveLocation(EndLocalRaidRequestData request)
    {
        if (!string.IsNullOrWhiteSpace(request.ServerId))
        {
            return request.ServerId.Split('.', 2)[0];
        }

        if (!string.IsNullOrWhiteSpace(request.LocationTransit?.SptLastVisitedLocation))
        {
            return request.LocationTransit.SptLastVisitedLocation;
        }

        return request.Results?.Profile?.Stats?.Eft?.Victims?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.Location))?.Location;
    }

    private List<BpTrackItem> BuildFoundItems(BotBase profile, EftStats eft, string? exitStatus)
    {
        if (!KeepsGear(exitStatus))
        {
            return [];
        }

        var items = profile.Inventory?.Items;
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var byId = items.ToDictionary(i => i.Id.ToString(), StringComparer.OrdinalIgnoreCase);
        var foundIds = (eft.FoundInRaidItems ?? [])
            .Select(f => f.ItemId?.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var foundItems = foundIds.Count > 0
            ? foundIds.Select(id => byId.GetValueOrDefault(id!)).Where(i => i is not null).Select(i => i!)
            : items.Where(i => i.Upd?.SpawnedInSession == true);

        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in foundItems)
        {
            var tpl = item.Template.ToString();
            if (string.IsNullOrWhiteSpace(tpl))
            {
                continue;
            }

            var count = Math.Max(1, (int)Math.Floor(item.Upd?.StackObjectsCount ?? 1));
            totals.TryGetValue(tpl, out var previous);
            totals[tpl] = previous + count;
        }

        return totals
            .Select(kv => new BpTrackItem { Tpl = kv.Key, Count = kv.Value })
            .ToList();
    }

    private static bool KeepsGear(string? exitStatus)
    {
        return (exitStatus ?? "").Trim().ToLowerInvariant() switch
        {
            "survived" or "runner" or "transit" => true,
            _ => false,
        };
    }
}
